using System.Net;
using System.Text.Json;
using Serk.Clinic.Host.Seeding;

namespace Serk.Clinic.IntegrationTests.Infrastructure;

/// <summary>Builds the vertical-slice preconditions through the public API (never by writing tables directly).</summary>
public static class Scenario
{
    private static int _slot;

    public static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>Unique synthetic names avoid duplicate-detection interference between tests.</summary>
    public static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    public static async Task<Guid> RegisterPatientAsync(ApiClient reception, string? given = null, string? father = null, DateOnly? dob = null, string? phone = null)
    {
        var r = await reception.SendAsync(HttpMethod.Post, "/api/v1/patients", new
        {
            givenName = given ?? UniqueName("Synth"),
            fatherName = father ?? UniqueName("Father"),
            grandfatherName = "Testgrand",
            sex = "Female",
            birthDate = dob ?? new DateOnly(1988, 4, 4),
            birthDateIsEstimated = false,
            phone,
        }, Key());
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await ApiClient.Body(r)).GetProperty("patient").GetProperty("id").GetGuid();
    }

    /// <summary>Each call uses a distinct future slot so tests do not collide on the practitioner's calendar.</summary>
    public static DateTimeOffset NextSlot() =>
        DateTimeOffset.UtcNow.Date.AddDays(1).AddMinutes(30 * Interlocked.Increment(ref _slot)).ToUniversalTime() is var d ? new DateTimeOffset(d, TimeSpan.Zero) : default;

    public static async Task<(Guid AppointmentId, Guid EncounterId)> BookAndCheckInAsync(ApiClient reception, Guid patientId, Guid? practitioner = null)
    {
        var book = await reception.SendAsync(HttpMethod.Post, "/api/v1/appointments", new
        {
            patientId,
            practitionerId = practitioner ?? DemoData.PractitionerAbebe,
            startsAt = NextSlot(),
            durationMinutes = 20,
            reason = "Synthetic visit",
        }, Key());
        Assert.True(book.StatusCode == HttpStatusCode.Created, await book.Content.ReadAsStringAsync());
        var apptId = (await ApiClient.Body(book)).GetProperty("id").GetGuid();
        var ci = await reception.SendAsync(HttpMethod.Post, $"/api/v1/appointments/{apptId}/check-in");
        Assert.True(ci.StatusCode == HttpStatusCode.OK, await ci.Content.ReadAsStringAsync());
        return (apptId, (await ApiClient.Body(ci)).GetProperty("encounterId").GetGuid());
    }

    public static async Task<int> SaveDraftAsync(ApiClient clinician, Guid encounterId, int version, string assessment = "Synthetic assessment")
    {
        var r = await clinician.SendAsync(HttpMethod.Put, $"/api/v1/encounters/{encounterId}/note-draft",
            new { chiefComplaint = "Synthetic complaint", history = "Synthetic history", examination = "Synthetic exam", assessment, plan = "Synthetic plan" },
            ifMatch: $"\"v{version}\"");
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await ApiClient.Body(r)).GetProperty("version").GetInt32();
    }

    public static async Task RecordNoKnownAllergiesAsync(ApiClient clinician, Guid patientId)
    {
        var p = await clinician.GetAsync($"/api/v1/patients/{patientId}");
        var etag = p.Headers.ETag!.Tag;
        var r = await clinician.SendAsync(HttpMethod.Post, $"/api/v1/patients/{patientId}/allergies/no-known-allergies", ifMatch: etag);
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    /// <summary>Registers, books, checks in, records NKA, drafts and signs. Returns ids.</summary>
    public static async Task<SignedEncounter> SignedEncounterAsync(SerkApiFixture f, ApiClient? clinician = null)
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        clinician ??= await f.LoginAsync(DemoData.Clinician);
        var patientId = await RegisterPatientAsync(reception);
        var (_, encounterId) = await BookAndCheckInAsync(reception, patientId);
        await RecordNoKnownAllergiesAsync(clinician, patientId);
        var v = await SaveDraftAsync(clinician, encounterId, 1);
        var sign = await clinician.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{encounterId}/sign", new { draftVersion = v, allergiesReviewed = true }, Key());
        Assert.True(sign.StatusCode == HttpStatusCode.OK, await sign.Content.ReadAsStringAsync());
        var body = await ApiClient.Body(sign);
        return new SignedEncounter(patientId, encounterId, body.GetProperty("noteVersionId").GetGuid(), body.GetProperty("contentSha256").GetString()!, clinician);
    }
}

public sealed record SignedEncounter(Guid PatientId, Guid EncounterId, Guid NoteVersionId, string ContentSha256, ApiClient Clinician);

public static class JsonExtensions
{
    public static IEnumerable<JsonElement> Items(this JsonElement e) => e.EnumerateArray();
}
