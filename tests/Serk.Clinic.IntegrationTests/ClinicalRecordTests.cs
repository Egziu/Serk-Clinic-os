using System.Net;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ClinicalRecordTests(SerkApiFixture f)
{
    private async Task<(Guid PatientId, Guid EncounterId, ApiClient Clinician)> OpenEncounterAsync(Guid? practitioner = null, string clinician = DemoData.Clinician)
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var d = await f.LoginAsync(clinician);
        var pid = await Scenario.RegisterPatientAsync(r);
        var (_, eid) = await Scenario.BookAndCheckInAsync(r, pid, practitioner);
        return (pid, eid, d);
    }

    [Fact(DisplayName = "EMR-001 draft autosave returns new ETag and persists content")]
    public async Task Draft_autosave_with_etag()
    {
        var (_, eid, d) = await OpenEncounterAsync();
        var get = await d.GetAsync($"/api/v1/encounters/{eid}");
        Assert.Equal("\"v1\"", get.Headers.ETag!.Tag);
        var v2 = await Scenario.SaveDraftAsync(d, eid, 1, "First thoughts");
        var v3 = await Scenario.SaveDraftAsync(d, eid, v2, "Refined thoughts");
        Assert.Equal(3, v3);
        var enc = await d.GetJsonAsync($"/api/v1/encounters/{eid}");
        Assert.Equal("Refined thoughts", enc.GetProperty("draft").GetProperty("content").GetProperty("assessment").GetString());
    }

    [Fact(DisplayName = "EMR-002 concurrent draft edits: stale writer gets 412 and no silent overwrite")]
    public async Task Draft_conflict_detected()
    {
        var (_, eid, d) = await OpenEncounterAsync();
        var results = await Task.WhenAll(
            d.SendAsync(HttpMethod.Put, $"/api/v1/encounters/{eid}/note-draft", new { chiefComplaint = "A", history = "", examination = "", assessment = "Writer A", plan = "" }, ifMatch: "\"v1\""),
            d.SendAsync(HttpMethod.Put, $"/api/v1/encounters/{eid}/note-draft", new { chiefComplaint = "B", history = "", examination = "", assessment = "Writer B", plan = "" }, ifMatch: "\"v1\""));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
        var winner = results.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winnerAssessment = (await ApiClient.Body(winner)).GetProperty("content").GetProperty("assessment").GetString();
        var stored = await d.GetJsonAsync($"/api/v1/encounters/{eid}");
        Assert.Equal(winnerAssessment, stored.GetProperty("draft").GetProperty("content").GetProperty("assessment").GetString());
        Assert.Equal(2, stored.GetProperty("draft").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await d.SendAsync(HttpMethod.Put, $"/api/v1/encounters/{eid}/note-draft", new { chiefComplaint = "C", history = "", examination = "", assessment = "", plan = "" })).StatusCode);
    }

    [Fact(DisplayName = "EMR-003 attending clinician signs; hash recorded; audit and outbox written atomically")]
    public async Task Authorized_sign()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var enc = await s.Clinician.GetJsonAsync($"/api/v1/encounters/{s.EncounterId}");
        Assert.Equal("Signed", enc.GetProperty("status").GetString());
        var v = enc.GetProperty("signedVersions")[0];
        Assert.Equal(s.ContentSha256, v.GetProperty("contentSha256").GetString());
        Assert.True(v.GetProperty("integrityVerified").GetBoolean());
        Assert.Equal("NoKnownAllergies", v.GetProperty("snapshot").GetProperty("allergyStatusAtSigning").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, enc.GetProperty("draft").ValueKind);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='note.sign' AND resource_id=@v", ("v", s.NoteVersionId)));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages WHERE payload->>'noteVersionId' = @v", ("v", s.NoteVersionId.ToString())));
    }

    [Fact(DisplayName = "EMR-003b unauthorized signers are refused: reception, nurse, non-attending clinician, expired license, other tenant")]
    public async Task Unauthorized_sign_refused()
    {
        var (pid, eid, d) = await OpenEncounterAsync();
        await Scenario.RecordNoKnownAllergiesAsync(d, pid);
        var v = await Scenario.SaveDraftAsync(d, eid, 1);
        var body = new { draftVersion = v, allergiesReviewed = true };

        foreach (var (user, expected) in new[]
                 {
                     (DemoData.Reception, HttpStatusCode.Forbidden),
                     (DemoData.Nurse, HttpStatusCode.Forbidden),
                     (DemoData.Clinician2, HttpStatusCode.Forbidden),
                     (DemoData.ClinicianB, HttpStatusCode.NotFound),
                 })
        {
            using var c = await f.LoginAsync(user, user == DemoData.Clinician2 ? DemoData.FacilityABole : null);
            var r = await c.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", body, Scenario.Key());
            Assert.True(r.StatusCode == expected, $"{user}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        }

        Assert.Equal("InProgress", (await d.GetJsonAsync($"/api/v1/encounters/{eid}")).GetProperty("status").GetString());
        Assert.Equal(0L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM clinical.note_versions WHERE encounter_id=@e", ("e", eid)));
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type='authorization.denied'") >= 2);
    }

    [Fact(DisplayName = "Expired license blocks signing even for the attending clinician")]
    public async Task Expired_license_blocks_sign()
    {
        var (pid, eid, d) = await OpenEncounterAsync(DemoData.PractitionerExpired, DemoData.ClinicianExpired);
        await Scenario.RecordNoKnownAllergiesAsync(d, pid);
        var v = await Scenario.SaveDraftAsync(d, eid, 1);
        var r = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", new { draftVersion = v, allergiesReviewed = true }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("license_expired", (await ApiClient.Body(r)).GetProperty("code").GetString());
    }

    [Fact(DisplayName = "Signing is blocked until allergy status is recorded and reviewed")]
    public async Task Allergy_gate_on_sign()
    {
        var (pid, eid, d) = await OpenEncounterAsync();
        var v = await Scenario.SaveDraftAsync(d, eid, 1);
        var r1 = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", new { draftVersion = v, allergiesReviewed = true }, Scenario.Key());
        Assert.Equal("allergy_status_unknown", (await ApiClient.Body(r1)).GetProperty("code").GetString());
        await Scenario.RecordNoKnownAllergiesAsync(d, pid);
        var r2 = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", new { draftVersion = v, allergiesReviewed = false }, Scenario.Key());
        Assert.Equal("allergies_not_reviewed", (await ApiClient.Body(r2)).GetProperty("code").GetString());
    }

    [Fact(DisplayName = "EMR-004 signed note cannot be modified via API or directly in the database")]
    public async Task Signed_note_is_immutable()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var edit = await s.Clinician.SendAsync(HttpMethod.Put, $"/api/v1/encounters/{s.EncounterId}/note-draft", new { chiefComplaint = "tamper", history = "", examination = "", assessment = "tamper", plan = "" }, ifMatch: "\"v2\"");
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        var resign = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{s.EncounterId}/sign", new { draftVersion = 2, allergiesReviewed = true }, Scenario.Key());
        Assert.Equal(HttpStatusCode.Conflict, resign.StatusCode);

        // Runtime role: no UPDATE/DELETE privilege on signed versions.
        await using (var app = new NpgsqlConnection(f.AppConnectionString))
        {
            await app.OpenAsync();
            await using var tx = await app.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DemoData.TenantA}', true)", app, tx))
            {
                await set.ExecuteNonQueryAsync();
            }

            await using var upd = new NpgsqlCommand("UPDATE clinical.note_versions SET signer_display_name = 'x' WHERE id = @v", app, tx);
            upd.Parameters.AddWithValue("v", s.NoteVersionId);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync())).SqlState);
        }

        // Even the schema owner is blocked by the immutability trigger.
        await using (var owner = new NpgsqlConnection(f.OwnerConnectionString))
        {
            await owner.OpenAsync();
            foreach (var sql in new[] { "UPDATE clinical.note_versions SET content = '{}'::jsonb WHERE id = @v", "DELETE FROM clinical.note_versions WHERE id = @v" })
            {
                await using var cmd = new NpgsqlCommand(sql, owner);
                cmd.Parameters.AddWithValue("v", s.NoteVersionId);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Contains("serk_immutable", ex.MessageText, StringComparison.Ordinal);
            }
        }

        Assert.Equal(s.ContentSha256, await f.OwnerScalarAsync<string>("SELECT content_sha256 FROM clinical.note_versions WHERE id=@v", ("v", s.NoteVersionId)));
    }

    [Fact(DisplayName = "EMR-005 addendum is attributed, references the original and leaves it unchanged")]
    public async Task Addendum_preserves_original()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        using var hana = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityABole);
        var r = await hana.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{s.EncounterId}/addenda", new { text = "Lab result reviewed: normal (synthetic).", reason = "Result available after signing" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var enc = await s.Clinician.GetJsonAsync($"/api/v1/encounters/{s.EncounterId}");
        var versions = enc.GetProperty("signedVersions").Items().ToList();
        Assert.Equal(2, versions.Count);
        Assert.Equal(s.ContentSha256, versions[0].GetProperty("contentSha256").GetString());
        Assert.Equal(versions[0].GetProperty("id").GetGuid(), versions[1].GetProperty("amendsVersionId").GetGuid());
        Assert.Contains("Hana", versions[1].GetProperty("signerDisplayName").GetString());
        Assert.All(versions, v => Assert.True(v.GetProperty("integrityVerified").GetBoolean()));
        var noReason = await hana.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{s.EncounterId}/addenda", new { text = "x" }, Scenario.Key());
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
    }

    [Fact(DisplayName = "EMR-006 sensitive reads are audited with actor, patient and correlation id")]
    public async Task Sensitive_read_audited()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var r = await s.Clinician.GetAsync($"/api/v1/encounters/{s.EncounterId}");
        var corr = r.Headers.GetValues("X-Correlation-ID").Single();
        Assert.Equal(1L, await f.OwnerScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE action='encounter.read' AND resource_id=@e AND patient_id=@p AND actor_user_id=@u AND correlation_id=@c",
            ("e", s.EncounterId), ("p", s.PatientId), ("u", DemoData.UserId(DemoData.Clinician)), ("c", corr)));
        await using var owner = new NpgsqlConnection(f.OwnerConnectionString);
        await owner.OpenAsync();
        await using var del = new NpgsqlCommand("DELETE FROM audit.audit_events WHERE resource_id=@e", owner);
        del.Parameters.AddWithValue("e", s.EncounterId);
        Assert.Contains("serk_immutable", (await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Sign is idempotent on replay and only one of two concurrent signs succeeds")]
    public async Task Sign_idempotency_and_race()
    {
        var (pid, eid, d) = await OpenEncounterAsync();
        await Scenario.RecordNoKnownAllergiesAsync(d, pid);
        var v = await Scenario.SaveDraftAsync(d, eid, 1);
        var body = new { draftVersion = v, allergiesReviewed = true };
        var raced = await Task.WhenAll(
            d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", body, "race-a-" + eid),
            d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", body, "race-b-" + eid));
        Assert.Equal(1, raced.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM clinical.note_versions WHERE encounter_id=@e", ("e", eid)));
        var winnerKey = raced[0].StatusCode == HttpStatusCode.OK ? "race-a-" + eid : "race-b-" + eid;
        var first = await ApiClient.Body(raced.Single(r => r.StatusCode == HttpStatusCode.OK));
        var replay = await d.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{eid}/sign", body, winnerKey);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first.GetProperty("noteVersionId").GetGuid(), (await ApiClient.Body(replay)).GetProperty("noteVersionId").GetGuid());
    }
}
