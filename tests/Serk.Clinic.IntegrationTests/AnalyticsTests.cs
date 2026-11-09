using System.Net;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

/// <summary>Analytics on an exactly-known synthetic dataset inserted into tenant B for a fixed past week (2026-01-05..11).</summary>
[Collection(ApiCollection.Name)]
public class AnalyticsTests(SerkApiFixture f)
{
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static bool _seeded;

    private async Task SeedAsync()
    {
        await Once.WaitAsync();
        try
        {
            if (_seeded)
            {
                return;
            }

            await using var conn = new NpgsqlConnection(f.OwnerConnectionString);
            await conn.OpenAsync();
            async Task Exec(string sql)
            {
                await using var c = new NpgsqlCommand(sql, conn);
                await c.ExecuteNonQueryAsync();
            }

            var t = DemoData.TenantB;
            var fac = DemoData.FacilityBHawassa;
            var pr = DemoData.PractitionerB;
            var pat = DemoData.PatientTenantB;
            // 10 appointments on Mon 2026-01-05 (EAT = UTC+3): 6 attended, 3 no-show, 1 cancelled; check-ins at 09:xx EAT (06:xx UTC).
            string[] statuses = ["CheckedIn", "CheckedIn", "CheckedIn", "CheckedIn", "Completed", "CheckedIn", "NoShow", "NoShow", "NoShow", "Cancelled"];
            for (var i = 0; i < statuses.Length; i++)
            {
                var start = $"2026-01-05 06:00:00+00'::timestamptz + interval '{i * 30} minutes";
                var checkedIn = statuses[i] is "CheckedIn" or "Completed" ? $"'2026-01-05 06:00:00+00'::timestamptz + interval '{i * 30} minutes'" : "NULL";
                await Exec($"INSERT INTO scheduling.appointments (id, tenant_id, facility_id, patient_id, practitioner_id, starts_at, ends_at, status, created_at, created_by, checked_in_at) " +
                           $"VALUES (gen_random_uuid(), '{t}', '{fac}', '{pat}', '{pr}', '{start}', '{start}' + interval '20 minutes', '{statuses[i]}', '2026-01-01 08:00:00+00', gen_random_uuid(), {checkedIn})");
            }

            // 4 signed encounters with 10/20/30/40 minutes to signature.
            foreach (var minutes in new[] { 10, 20, 30, 40 })
            {
                await Exec($"INSERT INTO clinical.encounters (id, tenant_id, facility_id, patient_id, attending_practitioner_id, status, started_at, signed_at) " +
                           $"VALUES (gen_random_uuid(), '{t}', '{fac}', '{pat}', '{pr}', 'Signed', '2026-01-06 07:00:00+00', '2026-01-06 07:00:00+00'::timestamptz + interval '{minutes} minutes')");
            }

            // 2 registrations: female age 30, male age 3.
            await Exec($"INSERT INTO patients.patients (id, tenant_id, mrn, given_name, father_name, normalized_name, sex, birth_date, birth_date_is_estimated, registered_at_facility_id, created_at, created_by) VALUES " +
                       $"(gen_random_uuid(), '{t}', 'MRN-AN0001', 'Synth', 'Analytic', 'synth analytic', 'Female', '1995-06-01', false, '{fac}', '2026-01-07 08:00:00+00', gen_random_uuid())," +
                       $"(gen_random_uuid(), '{t}', 'MRN-AN0002', 'Synth', 'Child', 'synth child', 'Male', '2022-09-01', false, '{fac}', '2026-01-07 09:00:00+00', gen_random_uuid())");
            _seeded = true;
        }
        finally
        {
            Once.Release();
        }
    }

    [Fact(DisplayName = "ANALYTICS-001 exact aggregates, Wilson CI, percentiles, heatmap, demographics; no patient identifiers in output")]
    public async Task Overview_exact_values()
    {
        await SeedAsync();
        using var admin = await f.LoginAsync(DemoData.ClinicAdminB);
        var res = await admin.GetAsync("/api/v1/analytics/overview?from=2026-01-05&to=2026-01-11");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("analytics;dur=", res.Headers.GetValues("Server-Timing").First(), StringComparison.Ordinal);
        var raw = await res.Content.ReadAsStringAsync();
        var o = System.Text.Json.JsonDocument.Parse(raw).RootElement;

        Assert.Equal(10, o.GetProperty("appointments").GetInt32());
        Assert.Equal(1, o.GetProperty("cancelled").GetInt32());
        var noShow = o.GetProperty("noShowRate");
        Assert.Equal(3, noShow.GetProperty("numerator").GetInt32());
        Assert.Equal(9, noShow.GetProperty("denominator").GetInt32());
        Assert.Equal(0.3333, noShow.GetProperty("rate").GetDouble(), 4);
        Assert.Equal(0.1206, noShow.GetProperty("low95").GetDouble(), 3);   // Wilson 3/9
        Assert.Equal(0.6458, noShow.GetProperty("high95").GetDouble(), 3);
        var sign = o.GetProperty("timeToSignedNote");
        Assert.Equal(4, sign.GetProperty("n").GetInt32());
        Assert.Equal(25, sign.GetProperty("medianMinutes").GetDouble(), 6);
        Assert.Equal(37, sign.GetProperty("p90Minutes").GetDouble(), 6);
        Assert.Equal(7, o.GetProperty("daily").GetArrayLength());
        Assert.Equal(10, o.GetProperty("daily")[0].GetProperty("appointments").GetInt32());
        Assert.Equal("Tahsas 27, 2018 E.C.", o.GetProperty("daily")[0].GetProperty("ethiopianDate").GetString());
        // Check-ins Monday (ISO 1) at 09:00-11:59 EAT
        var heat = o.GetProperty("arrivalHeatmap").Items().ToList();
        Assert.All(heat, c => Assert.Equal(1, c.GetProperty("isoWeekday").GetInt32()));
        Assert.Equal(6, heat.Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Contains(heat, c => c.GetProperty("hour").GetInt32() == 9);
        Assert.Equal(2, o.GetProperty("newPatients").GetInt32());
        Assert.Equal(1, o.GetProperty("newPatientsByAgeBand").Items().Single(b => b.GetProperty("label").GetString() == "0-4").GetProperty("count").GetInt32());
        Assert.Equal(1, o.GetProperty("newPatientsByAgeBand").Items().Single(b => b.GetProperty("label").GetString() == "25-44").GetProperty("count").GetInt32());
        var clinician = Assert.Single(o.GetProperty("clinicians").Items());
        Assert.Equal(4, clinician.GetProperty("signed").GetInt32());

        foreach (var phi in new[] { "Kidist", "Synth", "MRN-", "1995-06-01", DemoData.PatientTenantB.ToString() })
        {
            Assert.DoesNotContain(phi, raw, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "ANALYTICS-002 analytics are role- and tenant-scoped; export audited; range validated")]
    public async Task Analytics_authorization()
    {
        await SeedAsync();
        using var reception = await f.LoginAsync(DemoData.ReceptionB);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/analytics/overview")).StatusCode);
        using var clinician = await f.LoginAsync(DemoData.ClinicianB);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinician.GetAsync("/api/v1/analytics/overview")).StatusCode);
        using var ownerA = await f.LoginAsync(DemoData.Owner);
        var a = await ownerA.GetJsonAsync("/api/v1/analytics/overview?from=2026-01-05&to=2026-01-11");
        Assert.Equal(0, a.GetProperty("appointments").GetInt32()); // tenant B data invisible to tenant A
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerA.GetAsync("/api/v1/analytics/overview?from=2025-01-01&to=2026-06-01")).StatusCode);

        using var adminB = await f.LoginAsync(DemoData.ClinicAdminB);
        var csv = await adminB.GetAsync("/api/v1/analytics/daily.csv?from=2026-01-05&to=2026-01-11");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var lines = (await csv.Content.ReadAsStringAsync()).Trim().Split('\n');
        Assert.Equal(8, lines.Length);
        Assert.StartsWith("2026-01-05,\"Tahsas 27, 2018 E.C.\",10,6,3,1,", lines[1], StringComparison.Ordinal);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='analytics.export.csv' AND tenant_id=@t", ("t", DemoData.TenantB)) >= 1);
    }

    [Fact(DisplayName = "Owner role has analytics/branding but no patient or clinical access")]
    public async Task Owner_has_no_patient_access()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/patients?query=Selam")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/patients/{DemoData.PatientSelam}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/encounters")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/analytics/overview")).StatusCode);
    }
}
