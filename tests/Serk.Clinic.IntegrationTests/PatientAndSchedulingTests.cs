using System.Net;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PatientAndSchedulingTests(SerkApiFixture f)
{
    [Fact(DisplayName = "PAT-001 registration assigns unique tenant-scoped MRNs, audited")]
    public async Task Registration_assigns_unique_mrn()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var tasks = Enumerable.Range(0, 8).Select(_ => Scenario.RegisterPatientAsync(r));
        var ids = await Task.WhenAll(tasks);
        var mrns = new List<string>();
        foreach (var id in ids)
        {
            mrns.Add((await r.GetJsonAsync($"/api/v1/patients/{id}")).GetProperty("mrn").GetString()!);
        }

        Assert.Equal(mrns.Count, mrns.Distinct().Count());
        Assert.All(mrns, m => Assert.Matches("^MRN-\\d{6}$", m));
        var audited = await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'patient.register' AND resource_id = ANY(@ids)", ("ids", ids));
        Assert.Equal(ids.Length, audited);
    }

    [Fact(DisplayName = "PAT-002 probable duplicate requires review; override requires reason and is audited")]
    public async Task Duplicate_review_flow()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var given = Scenario.UniqueName("Dup");
        var father = Scenario.UniqueName("Fa");
        var first = await Scenario.RegisterPatientAsync(r, given, father, new DateOnly(1985, 2, 2));

        var body = new { givenName = given.ToUpperInvariant(), fatherName = father, sex = "Female", birthDate = "1985-02-02", birthDateIsEstimated = false };
        var dup = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", body, Scenario.Key());
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var json = await ApiClient.Body(dup);
        Assert.Equal("DuplicateReviewRequired", json.GetProperty("outcome").GetString());
        Assert.Contains(json.GetProperty("candidates").Items(), c => c.GetProperty("id").GetGuid() == first && c.GetProperty("strength").GetString() == "Probable");

        var overridden = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { body.givenName, body.fatherName, body.sex, body.birthDate, duplicateOverrideReason = "Different mother; confirmed with patient ID card" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Created, overridden.StatusCode);
        var newId = (await ApiClient.Body(overridden)).GetProperty("patient").GetProperty("id").GetGuid();
        Assert.Equal("duplicate_override", await f.OwnerScalarAsync<string>("SELECT reason FROM audit.audit_events WHERE action='patient.register' AND resource_id=@id", ("id", newId)));
    }

    [Fact(DisplayName = "Definite duplicate (same national ID) cannot be overridden at registration")]
    public async Task National_id_duplicate_is_blocked()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var nid = "SYN-" + Guid.NewGuid().ToString("N")[..10];
        var a = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "Aa", fatherName = Scenario.UniqueName("B"), sex = "Male", birthDate = "1970-01-01", nationalId = nid }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        var b = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "Zz", fatherName = Scenario.UniqueName("Y"), sex = "Male", birthDate = "1999-01-01", nationalId = nid, duplicateOverrideReason = "trying to force" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Conflict, b.StatusCode);
    }

    [Fact(DisplayName = "Idempotent registration replays the original result and rejects key reuse with a different body")]
    public async Task Registration_is_idempotent()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var key = Scenario.Key();
        var body = new { givenName = Scenario.UniqueName("Idem"), fatherName = Scenario.UniqueName("F"), sex = "Male", birthDate = "1991-01-01" };
        var one = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", body, key);
        var two = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", body, key);
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        Assert.Equal(HttpStatusCode.Created, two.StatusCode);
        var id1 = (await ApiClient.Body(one)).GetProperty("patient").GetProperty("id").GetGuid();
        var id2 = (await ApiClient.Body(two)).GetProperty("patient").GetProperty("id").GetGuid();
        Assert.Equal(id1, id2);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM patients.patients WHERE given_name = @g", ("g", body.givenName)));
        var reuse = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { body.givenName, fatherName = "Changed", body.sex, body.birthDate }, key);
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal("idempotency_key_reused", (await ApiClient.Body(reuse)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await r.SendAsync(HttpMethod.Post, "/api/v1/patients", body)).StatusCode); // key required
    }

    [Fact]
    public async Task Reception_sees_demographics_but_not_allergies()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        using var d = await f.LoginAsync(DemoData.Clinician);
        var id = await Scenario.RegisterPatientAsync(r);
        Assert.Equal(HttpStatusCode.Created, (await d.SendAsync(HttpMethod.Post, $"/api/v1/patients/{id}/allergies", new { substance = "Sulfonamides", reaction = "Hives", severity = "Severe" })).StatusCode);
        var recep = await r.GetJsonAsync($"/api/v1/patients/{id}");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, recep.GetProperty("allergies").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, recep.GetProperty("allergyStatus").ValueKind);
        var clin = await d.GetJsonAsync($"/api/v1/patients/{id}");
        Assert.Equal("HasAllergies", clin.GetProperty("allergyStatus").GetString());
        Assert.Equal("Sulfonamides", clin.GetProperty("allergies")[0].GetProperty("substance").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await r.SendAsync(HttpMethod.Post, $"/api/v1/patients/{id}/allergies", new { substance = "x", severity = "Mild" })).StatusCode);
    }

    [Fact(DisplayName = "Mass assignment / unknown JSON members are rejected")]
    public async Task Unknown_json_members_rejected()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var res = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "A", fatherName = "B", sex = "Male", birthDate = "1990-01-01", tenantId = DemoData.TenantB, mrn = "MRN-000001" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact(DisplayName = "APPT-001 book, list and check in creates exactly one encounter")]
    public async Task Book_list_check_in()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var (apptId, encounterId) = await Scenario.BookAndCheckInAsync(r, pid);
        var again = await r.SendAsync(HttpMethod.Post, $"/api/v1/appointments/{apptId}/check-in");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM clinical.encounters WHERE appointment_id = @a", ("a", apptId)));
        Assert.Equal(encounterId, await f.OwnerScalarAsync<Guid>("SELECT encounter_id FROM scheduling.appointments WHERE id = @a", ("a", apptId)));
    }

    [Fact(DisplayName = "APPT-002 concurrent bookings of the same practitioner slot: exactly one succeeds")]
    public async Task Double_booking_race()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var slot = DateTimeOffset.UtcNow.Date.AddDays(30).AddHours(6);
        var start = new DateTimeOffset(slot, TimeSpan.Zero);
        var attempts = Enumerable.Range(0, 10).Select(i => r.SendAsync(HttpMethod.Post, "/api/v1/appointments", new
        {
            patientId = pid,
            practitionerId = DemoData.PractitionerHana,
            startsAt = start.AddMinutes(i % 3 * 5), // overlapping 20-minute windows
            durationMinutes = 20,
        }, Scenario.Key()));
        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(x => x.StatusCode != HttpStatusCode.Created), x => Assert.Equal(HttpStatusCode.Conflict, x.StatusCode));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM scheduling.appointments WHERE practitioner_id=@p AND starts_at >= @s AND starts_at < @e AND status='Booked'",
            ("p", DemoData.PractitionerHana), ("s", start), ("e", start.AddHours(1))));
    }

    [Fact]
    public async Task Concurrent_check_in_creates_one_encounter()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var book = await r.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerAbebe, startsAt = Scenario.NextSlot(), durationMinutes = 15 }, Scenario.Key());
        var apptId = (await ApiClient.Body(book)).GetProperty("id").GetGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => r.SendAsync(HttpMethod.Post, $"/api/v1/appointments/{apptId}/check-in")));
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM clinical.encounters WHERE appointment_id = @a", ("a", apptId)));
    }

    [Fact]
    public async Task Booking_with_practitioner_of_other_tenant_is_rejected()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var res = await r.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerB, startsAt = Scenario.NextSlot(), durationMinutes = 15 }, Scenario.Key());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var rb = await f.LoginAsync(DemoData.ReceptionB);
        var res2 = await rb.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerB, startsAt = Scenario.NextSlot(), durationMinutes = 15 }, Scenario.Key());
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
    }
}
