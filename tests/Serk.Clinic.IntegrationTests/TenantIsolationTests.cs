using System.Net;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TenantIsolationTests(SerkApiFixture f)
{
    [Fact(DisplayName = "TENANT-001 patient, encounter and document ids from tenant A are not found from tenant B")]
    public async Task Cross_tenant_object_access_is_not_found()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var create = await s.Clinician.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = s.NoteVersionId });
        var docId = (await ApiClient.Body(create)).GetProperty("id").GetGuid();

        using var b = await f.LoginAsync(DemoData.ClinicianB);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/patients/{s.PatientId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/encounters/{s.EncounterId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/documents/{docId}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = s.NoteVersionId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{s.EncounterId}/addenda", new { text = "x", reason = "cross tenant" }, Scenario.Key())).StatusCode);
        var dto = await b.GetJsonAsync($"/api/v1/documents?encounterId={s.EncounterId}");
        Assert.Equal(0, dto.GetArrayLength());
    }

    [Fact(DisplayName = "TENANT-002 search from tenant B does not reveal tenant A patients by name, MRN or phone")]
    public async Task Search_does_not_leak_across_tenants()
    {
        using var ra = await f.LoginAsync(DemoData.Reception);
        var given = Scenario.UniqueName("Leakcheck");
        var id = await Scenario.RegisterPatientAsync(ra, given: given, phone: "+251 900 777888");
        var mrn = (await ra.GetJsonAsync($"/api/v1/patients/{id}")).GetProperty("mrn").GetString();

        using var rb = await f.LoginAsync(DemoData.ReceptionB);
        foreach (var q in new[] { given, mrn!, "+251900777888", given[..6] })
        {
            var hits = await rb.GetJsonAsync($"/api/v1/patients?query={Uri.EscapeDataString(q)}");
            Assert.DoesNotContain(hits.Items(), h => h.GetProperty("id").GetGuid() == id);
        }

        // Duplicate check from tenant B must not reveal tenant A's record either.
        var reg = await rb.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = given, fatherName = "Father", sex = "Female", birthDate = "1988-04-04", phone = "+251 900 777888" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
    }

    [Fact(DisplayName = "TENANT-003 RLS fails closed without context and does not bleed across pooled connections")]
    public async Task Rls_fails_closed_and_context_is_transaction_local()
    {
        await using var ds = new NpgsqlDataSourceBuilder(f.AppConnectionString + ";Maximum Pool Size=1").Build();

        // 1. No tenant context => zero rows even though rows exist.
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM patients.patients", conn);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        // 2. Tenant A context inside a transaction => only tenant A rows.
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DemoData.TenantA}', true)", conn, tx);
            await set.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(DISTINCT tenant_id), bool_and(tenant_id = @a) FROM patients.patients", conn, tx);
            cmd.Parameters.AddWithValue("a", DemoData.TenantA);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            Assert.Equal(1L, r.GetInt64(0));
            Assert.True(r.GetBoolean(1));
            await r.CloseAsync();
            await tx.CommitAsync();
        }

        // 3. Same physical pooled connection (pool size 1) afterwards: context is gone => zero rows again.
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM patients.patients", conn);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact(DisplayName = "TENANT-003b RLS WITH CHECK rejects writing a row for another tenant")]
    public async Task Rls_rejects_cross_tenant_insert()
    {
        await using var conn = new NpgsqlConnection(f.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DemoData.TenantB}', true)", conn, tx))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO patients.allergies (id, tenant_id, patient_id, substance, severity, status, recorded_at, recorded_by) VALUES (gen_random_uuid(), @a, @p, 'x', 'Mild', 'Active', now(), gen_random_uuid())", conn, tx);
        insert.Parameters.AddWithValue("a", DemoData.TenantA);
        insert.Parameters.AddWithValue("p", DemoData.PatientSelam);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege: new row violates row-level security policy
    }

    [Fact(DisplayName = "TENANT-004 platform support account has no tenant data access")]
    public async Task Support_has_no_phi_access()
    {
        using var s = f.Client();
        var session = await s.LoginAsync(DemoData.Support, SerkApiFixture.DemoPassword);
        Assert.Equal(0, session.GetProperty("affiliations").GetArrayLength());
        Assert.Equal(0, session.GetProperty("capabilities").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.GetAsync("/api/v1/patients?query=Selam")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.GetAsync($"/api/v1/patients/{DemoData.PatientSelam}")).StatusCode);
    }

    [Fact(DisplayName = "IDENT-003 clinician working at another facility of the same tenant cannot open this facility's encounter")]
    public async Task Cross_facility_encounter_is_not_found()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        using var hana = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityAPiassa);
        Assert.Equal(HttpStatusCode.NotFound, (await hana.GetAsync($"/api/v1/encounters/{s.EncounterId}")).StatusCode);
        using var hanaBole = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityABole);
        Assert.Equal(HttpStatusCode.OK, (await hanaBole.GetAsync($"/api/v1/encounters/{s.EncounterId}")).StatusCode);
    }

    [Fact(DisplayName = "Clinic admin cannot read clinical encounters through admin role")]
    public async Task Clinic_admin_cannot_read_charts()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/encounters/{s.EncounterId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/patients/{s.PatientId}")).StatusCode);
    }
}
