using System.Net;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

/// <summary>Regression tests for findings of independent review round 1 (docs/reviews/2026-11-09-review-round-1.md).</summary>
[Collection(ApiCollection.Name)]
public class ReviewRound1RegressionTests(SerkApiFixture f)
{
    private static async Task<Guid> CreateDocAsync(ApiClient c, Guid noteVersionId)
    {
        var r = await c.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId });
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await ApiClient.Body(r)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddendumAsync(SignedEncounter s)
    {
        var add = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{s.EncounterId}/addenda", new { text = "Dose corrected (synthetic).", reason = "Correction of plan" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        return (await ApiClient.Body(add)).GetProperty("noteVersionId").GetGuid();
    }

    [Fact(DisplayName = "SEC-R1-01 a document cannot be prepared from an older note version once an addendum exists")]
    public async Task Older_version_document_rejected()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        await AddendumAsync(s);
        var r = await s.Clinician.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = s.NoteVersionId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("not_latest_version", (await ApiClient.Body(r)).GetProperty("code").GetString());
    }

    [Fact(DisplayName = "SEC-R1-01b a preview prepared before an addendum cannot be finalized afterwards")]
    public async Task Stale_preview_cannot_be_finalized()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var doc = await CreateDocAsync(s.Clinician, s.NoteVersionId);
        await AddendumAsync(s);
        var fin = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{doc}/finalize", idempotencyKey: Scenario.Key());
        Assert.Equal(HttpStatusCode.Conflict, fin.StatusCode);
        Assert.Equal("stale_document", (await ApiClient.Body(fin)).GetProperty("code").GetString());
    }

    [Fact(DisplayName = "SEC-R1-05 concurrent finalization of two drafts yields exactly one current final")]
    public async Task Concurrent_finalize_single_final()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var a = await CreateDocAsync(s.Clinician, s.NoteVersionId);
        var b = await CreateDocAsync(s.Clinician, s.NoteVersionId);
        var results = await Task.WhenAll(
            s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{a}/finalize", idempotencyKey: Scenario.Key()),
            s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{b}/finalize", idempotencyKey: Scenario.Key()));
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM documents.document_versions WHERE encounter_id=@e AND status='Final'", ("e", s.EncounterId)));
    }

    [Fact(DisplayName = "HAZ-014 superseded PDFs are not served as current")]
    public async Task Superseded_download_refused()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var first = await CreateDocAsync(s.Clinician, s.NoteVersionId);
        await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{first}/finalize", idempotencyKey: Scenario.Key());
        var v2 = await AddendumAsync(s);
        var second = await CreateDocAsync(s.Clinician, v2);
        Assert.Equal(HttpStatusCode.OK, (await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{second}/finalize", idempotencyKey: Scenario.Key())).StatusCode);
        var r = await s.Clinician.GetAsync($"/api/v1/documents/{first}/download");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("document_superseded", (await ApiClient.Body(r)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await s.Clinician.GetAsync($"/api/v1/documents/{second}/download")).StatusCode);
    }

    [Fact(DisplayName = "SEC-R1-03 another user cannot obtain a stored response by replaying an idempotency key")]
    public async Task Idempotency_replay_bound_to_actor()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        using var hana = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityABole);
        var probeKey = "probe-" + Guid.NewGuid().ToString("N");
        // dr.abebe signs encounter E2 with probeKey; dr.hana replays it.
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var (_, eid) = await Scenario.BookAndCheckInAsync(r, pid);
        await Scenario.RecordNoKnownAllergiesAsync(s.Clinician, pid);
        var v = await Scenario.SaveDraftAsync(s.Clinician, eid, 1);
        var token = await Scenario.AllergyTokenAsync(s.Clinician, eid);
        Assert.Equal(HttpStatusCode.OK, (await Scenario.SignAsync(s.Clinician, eid, v, key: probeKey, token: token)).StatusCode);
        var replay = await hana.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", new { draftVersion = v, allergiesReviewed = true, allergyReviewToken = token }, probeKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("idempotency_key_conflict", (await ApiClient.Body(replay)).GetProperty("code").GetString());
        Assert.DoesNotContain("noteVersionId", await replay.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "QA-R1-D1 a draft save racing with signing is never silently lost")]
    public async Task Draft_save_sign_race()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        using var d = await f.LoginAsync(DemoData.Clinician);
        for (var i = 0; i < 8; i++)
        {
            var pid = await Scenario.RegisterPatientAsync(reception);
            var (_, eid) = await Scenario.BookAndCheckInAsync(reception, pid);
            await Scenario.RecordNoKnownAllergiesAsync(d, pid);
            var v = await Scenario.SaveDraftAsync(d, eid, 1);
            var token = await Scenario.AllergyTokenAsync(d, eid);
            var save = d.SendAsync(HttpMethod.Put, $"/api/v1/encounters/{eid}/note-draft", new { chiefComplaint = "Late edit", history = "", examination = "", assessment = "LATE-EDIT-MARKER", plan = "" }, ifMatch: $"\"v{v}\"");
            var sign = Scenario.SignAsync(d, eid, v, token: token);
            var (saveRes, signRes) = (await save, await sign);
            Assert.False(saveRes.StatusCode == HttpStatusCode.OK && signRes.StatusCode == HttpStatusCode.OK, "Both the save and the sign succeeded: the edit would be lost.");
            if (saveRes.StatusCode == HttpStatusCode.OK)
            {
                continue; // sign lost the race and was refused; the edit is in the draft
            }

            var signed = await d.GetJsonAsync($"/api/v1/encounters/{eid}");
            Assert.Equal("Signed", signed.GetProperty("status").GetString());
        }
    }

    [Fact(DisplayName = "HAZ-009 signing is refused if allergies changed after the clinician reviewed them")]
    public async Task Allergy_change_between_review_and_sign()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        using var d = await f.LoginAsync(DemoData.Clinician);
        using var nurse = await f.LoginAsync(DemoData.Nurse);
        var pid = await Scenario.RegisterPatientAsync(reception);
        var (_, eid) = await Scenario.BookAndCheckInAsync(reception, pid);
        await Scenario.RecordNoKnownAllergiesAsync(d, pid);
        var v = await Scenario.SaveDraftAsync(d, eid, 1);
        var seenToken = await Scenario.AllergyTokenAsync(d, eid);
        Assert.Equal(HttpStatusCode.Created, (await nurse.SendAsync(HttpMethod.Post, $"/api/v1/patients/{pid}/allergies", new { substance = "Latex", severity = "Severe" })).StatusCode);
        var r = await Scenario.SignAsync(d, eid, v, token: seenToken);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("allergies_changed", (await ApiClient.Body(r)).GetProperty("code").GetString());
        var ok = await Scenario.SignAsync(d, eid, v);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var snap = (await d.GetJsonAsync($"/api/v1/encounters/{eid}")).GetProperty("signedVersions")[0].GetProperty("snapshot");
        Assert.Equal("Latex", snap.GetProperty("activeAllergiesAtSigning")[0].GetProperty("substance").GetString());
    }

    [Fact(DisplayName = "HAZ-006 an allergy entered in error can be corrected with a reason; status returns to Unknown")]
    public async Task Allergy_correction()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        using var d = await f.LoginAsync(DemoData.Clinician);
        var pid = await Scenario.RegisterPatientAsync(reception);
        var add = await ApiClient.Body(await d.SendAsync(HttpMethod.Post, $"/api/v1/patients/{pid}/allergies", new { substance = "Wrong drug", severity = "Mild" }));
        var aid = add.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await d.SendAsync(HttpMethod.Post, $"/api/v1/patients/{pid}/allergies/{aid}/status", new { status = "EnteredInError" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await d.SendAsync(HttpMethod.Post, $"/api/v1/patients/{pid}/allergies/{aid}/status", new { status = "EnteredInError", reason = "Recorded on wrong patient" })).StatusCode);
        var p = await d.GetJsonAsync($"/api/v1/patients/{pid}");
        Assert.Equal("Unknown", p.GetProperty("allergyStatus").GetString());
        Assert.Equal("EnteredInError", p.GetProperty("allergies")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(HttpMethod.Post, $"/api/v1/patients/{pid}/allergies/{aid}/status", new { status = "Inactive", reason = "reception attempt" })).StatusCode);
    }

    [Fact(DisplayName = "SEC-R1-06 duplicate candidates disclosed at registration are audited even without a new record")]
    public async Task Duplicate_disclosure_audited()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var nid = "SYN-" + Guid.NewGuid().ToString("N")[..10];
        var first = await ApiClient.Body(await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "Probe", fatherName = Scenario.UniqueName("T"), sex = "Male", birthDate = "1970-01-01", nationalId = nid }, Scenario.Key()));
        var id = first.GetProperty("patient").GetProperty("id").GetGuid();
        var probe = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "Other", fatherName = "Person", sex = "Male", birthDate = "1999-01-01", nationalId = nid }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Conflict, probe.StatusCode);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='patient.duplicate_candidates_disclosed' AND metadata->>'candidateIds' LIKE @p", ("p", "%" + id + "%")) >= 1);
    }

    [Fact(DisplayName = "SEC-R1-02 public verification works without a superuser function owner")]
    public async Task Verifier_function_not_superuser_owned()
    {
        Assert.False(await f.OwnerScalarAsync<bool>("SELECT r.rolsuper FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner WHERE p.proname = 'verify_reference'"));
        Assert.False(await f.OwnerScalarAsync<bool>("SELECT r.rolbypassrls FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner WHERE p.proname = 'verify_reference'"));
        var s = await Scenario.SignedEncounterAsync(f);
        var doc = await CreateDocAsync(s.Clinician, s.NoteVersionId);
        var fin = await ApiClient.Body(await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{doc}/finalize", idempotencyKey: Scenario.Key()));
        using var anon = f.Client();
        Assert.True((await anon.GetJsonAsync($"/api/v1/verify/{fin.GetProperty("verificationRef").GetString()}")).GetProperty("valid").GetBoolean());
        // Drafts are not verifiable.
        var draft = await CreateDocAsync(s.Clinician, s.NoteVersionId);
        var draftRef = await f.OwnerScalarAsync<string>("SELECT verification_ref FROM documents.document_versions WHERE id=@d", ("d", draft));
        Assert.False((await anon.GetJsonAsync($"/api/v1/verify/{draftRef}")).GetProperty("valid").GetBoolean());
    }

    [Fact(DisplayName = "SEC-R1-07 encounter identity (patient) cannot be re-pointed after creation, even by the owner")]
    public async Task Encounter_patient_cannot_be_repointed()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        await using var owner = new NpgsqlConnection(f.OwnerConnectionString);
        await owner.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE clinical.encounters SET patient_id = @p WHERE id = @e", owner);
        cmd.Parameters.AddWithValue("p", DemoData.PatientSelam);
        cmd.Parameters.AddWithValue("e", s.EncounterId);
        Assert.Contains("serk_immutable", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
    }
}
