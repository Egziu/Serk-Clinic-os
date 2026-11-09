using System.Net;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DocumentTests(SerkApiFixture f)
{
    private static string Body(byte[] pdf, string footer)
    {
        using var doc = PdfDocument.Open(pdf);
        var text = string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).Select(w => w.Text))));
        return footer.Length == 0 ? text : text.Replace(footer, "<FOOTER>", StringComparison.Ordinal);
    }

    private async Task<(SignedEncounter S, Guid DocId)> DraftDocumentAsync()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var create = await s.Clinician.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = s.NoteVersionId, paperSize = "A4" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (s, (await ApiClient.Body(create)).GetProperty("id").GetGuid());
    }

    [Fact(DisplayName = "DOC-001 preview requires authentication and document capability; access is audited")]
    public async Task Preview_requires_authorization()
    {
        var (s, id) = await DraftDocumentAsync();
        using var anon = f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/documents/{id}/preview")).StatusCode);
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/v1/documents/{id}/preview")).StatusCode);
        var ok = await s.Clinician.GetAsync($"/api/v1/documents/{id}/preview");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/pdf", ok.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", ok.Headers.CacheControl!.ToString());
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='document.preview' AND resource_id=@d", ("d", id)) >= 1);
    }

    [Fact(DisplayName = "DOC-002/003 final PDF equals preview content from the same snapshot and template; only preview is watermarked")]
    public async Task Final_matches_preview()
    {
        var (s, id) = await DraftDocumentAsync();
        var preview = await s.Clinician.GetAsync($"/api/v1/documents/{id}/preview");
        var previewBytes = await preview.Content.ReadAsByteArrayAsync();
        var fin = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: Scenario.Key());
        Assert.Equal(HttpStatusCode.OK, fin.StatusCode);
        var finJson = await ApiClient.Body(fin);
        Assert.Equal("Final", finJson.GetProperty("status").GetString());

        var download = await s.Clinician.GetAsync($"/api/v1/documents/{id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var finalBytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(preview.Headers.GetValues("X-Snapshot-Sha256").Single(), download.Headers.GetValues("X-Snapshot-Sha256").Single());
        Assert.Equal(preview.Headers.GetValues("X-Template-Version").Single(), download.Headers.GetValues("X-Template-Version").Single());
        Assert.Equal(finJson.GetProperty("pdfSha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(finalBytes)));
        Assert.Equal(Body(previewBytes, "DRAFT PREVIEW - NOT VALID FOR CLINICAL OR LEGAL USE"), Body(finalBytes, "Final document"));
        Assert.Contains(s.ContentSha256[..16], Body(finalBytes, ""));

        using var pv = PdfDocument.Open(previewBytes);
        using var fd = PdfDocument.Open(finalBytes);
        Assert.Contains(pv.GetPages().SelectMany(p => p.Letters), l => l.TextOrientation != TextOrientation.Horizontal);
        Assert.DoesNotContain(fd.GetPages().SelectMany(p => p.Letters), l => l.TextOrientation != TextOrientation.Horizontal);

        // Repeated downloads return byte-identical stored output.
        Assert.Equal(finalBytes, await (await s.Clinician.GetAsync($"/api/v1/documents/{id}/download")).Content.ReadAsByteArrayAsync());
    }

    [Fact(DisplayName = "DOC-004 document download denied across tenant and to roles without document access")]
    public async Task Download_denied_across_tenant()
    {
        var (s, id) = await DraftDocumentAsync();
        await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: Scenario.Key());
        using var b = await f.LoginAsync(DemoData.ClinicianB);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/documents/{id}/download")).StatusCode);
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/v1/documents/{id}/download")).StatusCode);
    }

    [Fact(DisplayName = "DOC-005 only print-dialog/failure events are accepted; physical printing cannot be claimed")]
    public async Task Print_status_is_truthful()
    {
        var (s, id) = await DraftDocumentAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/print-events", new { kind = "PrintDialogOpened" })).StatusCode); // draft
        await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: Scenario.Key());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/print-events", new { kind = "PrintedPhysically" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/print-events", new { kind = "PrintDialogOpened" })).StatusCode);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='document.print.PrintDialogOpened' AND resource_id=@d", ("d", id)));
    }

    [Fact]
    public async Task Draft_cannot_be_downloaded_and_final_cannot_be_refinalized()
    {
        var (s, id) = await DraftDocumentAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Clinician.GetAsync($"/api/v1/documents/{id}/download")).StatusCode);
        var key = Scenario.Key();
        var a = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: key);
        var replay = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal((await ApiClient.Body(a)).GetProperty("pdfSha256").GetString(), (await ApiClient.Body(replay)).GetProperty("pdfSha256").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: Scenario.Key())).StatusCode);
    }

    [Fact(DisplayName = "Final documents are frozen in the database; snapshot binding cannot be altered even by the owner")]
    public async Task Final_document_row_is_frozen()
    {
        var (s, id) = await DraftDocumentAsync();
        await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: Scenario.Key());
        await using var owner = new Npgsql.NpgsqlConnection(f.OwnerConnectionString);
        await owner.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE documents.document_versions SET pdf_sha256 = repeat('0', 64) WHERE id=@d",
                     "UPDATE documents.document_versions SET snapshot = '{}'::jsonb WHERE id=@d",
                     "DELETE FROM documents.document_versions WHERE id=@d",
                 })
        {
            await using var cmd = new Npgsql.NpgsqlCommand(sql, owner);
            cmd.Parameters.AddWithValue("d", id);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
    }

    [Fact(DisplayName = "Tampered stored PDF is detected and never served")]
    public async Task Tampered_object_is_not_served()
    {
        var (s, id) = await DraftDocumentAsync();
        await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{id}/finalize", idempotencyKey: Scenario.Key());
        var key = await f.OwnerScalarAsync<string>("SELECT pdf_object_key FROM documents.document_versions WHERE id=@d", ("d", id));
        var path = Path.Combine(f.ObjectRoot, key!);
        File.SetAttributes(path, FileAttributes.Normal);
        await File.AppendAllTextAsync(path, "tampered");
        var r = await s.Clinician.GetAsync($"/api/v1/documents/{id}/download");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("document_integrity_failure", (await ApiClient.Body(r)).GetProperty("code").GetString());
    }

    [Fact(DisplayName = "A newer final for an addendum supersedes the previous final; QR verification exposes no PHI")]
    public async Task Supersede_and_verify()
    {
        var (s, first) = await DraftDocumentAsync();
        await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{first}/finalize", idempotencyKey: Scenario.Key());
        var add = await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/encounters/{s.EncounterId}/addenda", new { text = "Correction: follow-up in 2 weeks.", reason = "Follow-up interval corrected" }, Scenario.Key());
        var addId = (await ApiClient.Body(add)).GetProperty("noteVersionId").GetGuid();
        var second = (await ApiClient.Body(await s.Clinician.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = addId }))).GetProperty("id").GetGuid();
        var fin = await ApiClient.Body(await s.Clinician.SendAsync(HttpMethod.Post, $"/api/v1/documents/{second}/finalize", idempotencyKey: Scenario.Key()));
        Assert.Equal(first, fin.GetProperty("supersedesId").GetGuid());

        var list = await s.Clinician.GetJsonAsync($"/api/v1/documents?encounterId={s.EncounterId}");
        Assert.Equal("Superseded", list.Items().Single(d => d.GetProperty("id").GetGuid() == first).GetProperty("status").GetString());
        var text = Body(await (await s.Clinician.GetAsync($"/api/v1/documents/{second}/download")).Content.ReadAsByteArrayAsync(), "");
        Assert.Contains("Addendum 1", text);
        Assert.Contains("Follow-up interval corrected", text);

        using var anon = f.Client();
        var verify = await anon.GetJsonAsync($"/api/v1/verify/{fin.GetProperty("verificationRef").GetString()}");
        Assert.True(verify.GetProperty("valid").GetBoolean());
        var raw = verify.GetRawText();
        Assert.DoesNotContain("Synth", raw);
        Assert.DoesNotContain("MRN", raw);
        Assert.False((await anon.GetJsonAsync("/api/v1/verify/AAAAAAAAAAAAAAAAAAAAAA")).GetProperty("valid").GetBoolean());
        Assert.False((await anon.GetJsonAsync($"/api/v1/verify/{list.Items().Single(d => d.GetProperty("id").GetGuid() == first).GetProperty("verificationRef").GetString()}x")).GetProperty("valid").GetBoolean());
    }
}
