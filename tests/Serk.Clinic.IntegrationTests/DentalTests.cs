using System.Net;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DentalTests(SerkApiFixture f)
{
    private async Task<(Guid PatientId, Guid EncounterId, ApiClient Dentist)> DentalEncounterAsync()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var (_, eid) = await Scenario.BookAndCheckInAsync(r, pid, DemoData.PractitionerDentist);
        return (pid, eid, await f.LoginAsync(DemoData.Dentist));
    }

    private static object Finding(int tooth, int confirm, string condition, string[] surfaces, Guid? corrects = null, string? reason = null) =>
        new { toothCode = tooth, confirmToothCode = confirm, surfaces, condition, note = "synthetic", correctsEntryId = corrects, correctionReason = reason };

    [Fact(DisplayName = "DENT-001/002 dentist records surface findings; chart shows current state with FDI names")]
    public async Task Record_and_read_chart()
    {
        var (_, eid, d) = await DentalEncounterAsync();
        Assert.Equal(HttpStatusCode.Created, (await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(36, 36, "Caries", ["Occlusal", "Mesial"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(11, 11, "Restored", ["Incisal"]))).StatusCode);
        var chart = await d.GetJsonAsync($"/api/v1/encounters/{eid}/dental/chart");
        var current = chart.GetProperty("current").Items().ToList();
        Assert.Equal(2, current.Count);
        Assert.Equal("36 lower left first molar", current.Single(c => c.GetProperty("toothCode").GetInt32() == 36).GetProperty("toothName").GetString());
        Assert.Equal("PENDING CLINICIAN REVIEW", chart.GetProperty("clinicalReviewStatus").GetString());
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='dental.chart.read' AND patient_id=@p", ("p", chart.GetProperty("patientId").GetGuid())) >= 1);
    }

    [Fact(DisplayName = "DENT-003 wrong-tooth confirmation mismatch and invalid surfaces are rejected; nothing stored")]
    public async Task Wrong_tooth_rejected()
    {
        var (pid, eid, d) = await DentalEncounterAsync();
        var r = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(36, 46, "Caries", ["Occlusal"]));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("tooth_confirmation_mismatch", (await ApiClient.Body(r)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(11, 11, "Caries", ["Occlusal"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(19, 19, "Caries", ["Occlusal"]))).StatusCode);
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM dental.findings WHERE patient_id=@p", ("p", pid)));
    }

    [Fact(DisplayName = "DENT-002 corrections preserve history; findings are append-only in the database")]
    public async Task Corrections_preserve_history()
    {
        var (_, eid, d) = await DentalEncounterAsync();
        var first = await ApiClient.Body(await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(46, 46, "Caries", ["Occlusal"])));
        var firstId = first.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(46, 46, "Caries", ["Distal"], firstId))).StatusCode); // reason required
        var corr = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(46, 46, "Caries", ["Distal"], firstId, "Surface misrecorded"));
        Assert.Equal(HttpStatusCode.Created, corr.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(46, 46, "Sound", [], firstId, "Second correction of same entry"))).StatusCode);

        var current = (await d.GetJsonAsync($"/api/v1/encounters/{eid}/dental/chart")).GetProperty("current").Items().Single(c => c.GetProperty("toothCode").GetInt32() == 46);
        Assert.Equal("Distal", current.GetProperty("surfaces")[0].GetString());
        var history = await d.GetJsonAsync($"/api/v1/encounters/{eid}/dental/teeth/46/history");
        Assert.Equal(2, history.GetArrayLength());

        await using var owner = new NpgsqlConnection(f.OwnerConnectionString);
        await owner.OpenAsync();
        await using var upd = new NpgsqlCommand("UPDATE dental.findings SET tooth_code = 36 WHERE id = @id", owner);
        upd.Parameters.AddWithValue("id", firstId);
        Assert.Contains("serk_immutable", (await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Dental authorization: non-dentist roles, non-attending, other tenant and signed encounters are refused")]
    public async Task Dental_authorization()
    {
        var (_, eid, _) = await DentalEncounterAsync();
        var body = Finding(36, 36, "Caries", ["Occlusal"]);
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/encounters/{eid}/dental/chart")).StatusCode);
        using var gp = await f.LoginAsync(DemoData.Clinician);
        Assert.Equal(HttpStatusCode.Forbidden, (await gp.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", body)).StatusCode); // no dental capability
        using var b = await f.LoginAsync(DemoData.ClinicianB);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/api/v1/encounters/{eid}/dental/chart")).StatusCode);

        // Dentist who is not the attending of a GP encounter cannot record into it.
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var (_, gpEncounter) = await Scenario.BookAndCheckInAsync(r, pid);
        using var dentist = await f.LoginAsync(DemoData.Dentist);
        var notAttending = await dentist.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{gpEncounter}/dental/findings", body);
        Assert.Equal(HttpStatusCode.Forbidden, notAttending.StatusCode);
        Assert.Equal("not_attending", (await ApiClient.Body(notAttending)).GetProperty("code").GetString());
    }

    [Fact(DisplayName = "Findings cannot be added to a signed dental encounter")]
    public async Task Signed_encounter_closed_for_findings()
    {
        var (pid, eid, d) = await DentalEncounterAsync();
        await Scenario.RecordNoKnownAllergiesAsync(d, pid);
        var v = await Scenario.SaveDraftAsync(d, eid, 1);
        Assert.Equal(HttpStatusCode.OK, (await Scenario.SignAsync(d, eid, v)).StatusCode);
        var r = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/dental/findings", Finding(36, 36, "Caries", ["Occlusal"]));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }
}
