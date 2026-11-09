using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;
using Serk.Clinic.Platform.Outbox;
using Serk.Clinic.Scheduling.Application;
using UglyToad.PdfPig;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class BrandingPrintingAutomationTests(SerkApiFixture f)
{
    /// <summary>Bytes the server stores for a fixture: the metadata-free canonical re-serialisation.</summary>
    private static byte[] Canonical(string fixture) => Serk.Clinic.Configuration.Domain.ImageInspector.Inspect(ApiClient.Fixture(fixture)).Value.Canonical;

    private static async Task<int> SettingsVersion(ApiClient c) =>
        (await c.GetJsonAsync("/api/v1/facility/settings")).GetProperty("version").GetInt32();

    private async Task<(ApiClient Admin, Guid LogoId)> UploadBrandingAsync()
    {
        var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        var logo = await admin.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("logo.png"), "logo.png", "image/png");
        Assert.True(logo.StatusCode == HttpStatusCode.Created, await logo.Content.ReadAsStringAsync());
        var stamp = await admin.UploadAsync("/api/v1/facility/branding/stamp", ApiClient.Fixture("stamp.png"), "stamp.png", "image/png");
        Assert.Equal(HttpStatusCode.Created, stamp.StatusCode);
        return (admin, (await ApiClient.Body(logo)).GetProperty("id").GetGuid());
    }

    [Fact(DisplayName = "BRAND-001 admin uploads logo/stamp; staff can view them; previous logo retired; cross-tenant 404")]
    public async Task Upload_and_view_branding()
    {
        var (admin, logoId) = await UploadBrandingAsync();
        var settings = await admin.GetJsonAsync("/api/v1/facility/settings");
        Assert.Equal(logoId, settings.GetProperty("logo").GetProperty("id").GetGuid());
        Assert.Equal("image/png", settings.GetProperty("logo").GetProperty("contentType").GetString());

        using var reception = await f.LoginAsync(DemoData.Reception);
        var content = await reception.GetAsync($"/api/v1/branding-assets/{logoId}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Canonical("logo.png"), await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", content.Headers.CacheControl!.ToString());

        var second = await ApiClient.Body(await admin.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("stamp.png"), "x.png", "image/png"));
        Assert.Equal("Retired", await f.OwnerScalarAsync<string>("SELECT status FROM configuration.branding_assets WHERE id=@i", ("i", logoId)));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM configuration.branding_assets WHERE facility_id=@f AND kind='Logo' AND status='Active'", ("f", DemoData.FacilityABole)));
        Assert.NotEqual(logoId, second.GetProperty("id").GetGuid());

        using var b = await f.LoginAsync(DemoData.ClinicAdminB);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/branding-assets/{logoId}/content")).StatusCode);
    }

    [Theory(DisplayName = "BRAND-002 malicious or unsupported uploads are rejected and logged")]
    [InlineData("polyglot.png", "image/png", "trailing_data")]
    [InlineData("vector.svg", "image/svg+xml", "unsupported_format")]
    [InlineData("palette.png", "image/png", "unsupported_png")]
    [InlineData("badcrc.png", "image/png", "malformed_image")]
    public async Task Bad_uploads_rejected(string file, string type, string code)
    {
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        var r = await admin.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture(file), file, type);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(code, (await ApiClient.Body(r)).GetProperty("code").GetString());
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type='branding.upload_rejected' AND outcome=@o", ("o", code)) >= 1);
    }

    [Fact(DisplayName = "BRAND-003 upload authorization and CSRF")]
    public async Task Upload_authorization()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("logo.png"), "l.png", "image/png")).StatusCode);
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("logo.png"), "l.png", "image/png", includeCsrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.UploadAsync("/api/v1/facility/branding/signature", ApiClient.Fixture("logo.png"), "l.png", "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.UploadAsync("/api/v1/me/signature", ApiClient.Fixture("signature.jpg"), "s.jpg", "image/jpeg")).StatusCode); // admin is not a practitioner/signer
    }

    [Fact(DisplayName = "BRAND-004 practitioner signature: own upload; other clinicians cannot fetch it")]
    public async Task Signature_privacy()
    {
        using var d = await f.LoginAsync(DemoData.Clinician);
        var up = await d.UploadAsync("/api/v1/me/signature", ApiClient.Fixture("signature.jpg"), "sig.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var id = (await ApiClient.Body(up)).GetProperty("id").GetGuid();
        Assert.Equal(id, (await d.GetJsonAsync("/api/v1/me/signature")).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await d.GetAsync($"/api/v1/branding-assets/{id}/content")).StatusCode);
        using var clin = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityABole);
        Assert.Equal(HttpStatusCode.NotFound, (await clin.GetAsync($"/api/v1/branding-assets/{id}/content")).StatusCode);
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.NotFound, (await reception.GetAsync($"/api/v1/branding-assets/{id}/content")).StatusCode);
        // Branding managers cannot download a practitioner's signature either (forgery risk).
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/branding-assets/{id}/content")).StatusCode);
        // Signature bytes are never cached by the browser, and the signature upload requires the CSRF header.
        Assert.Equal("no-store", (await d.GetAsync($"/api/v1/branding-assets/{id}/content")).Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await d.UploadAsync("/api/v1/me/signature", ApiClient.Fixture("signature.jpg"), "s.jpg", "image/jpeg", includeCsrf: false)).StatusCode);
    }

    [Fact(DisplayName = "BRAND-005 a signature uploaded after the note was signed is not applied to that note's documents")]
    public async Task Late_signature_not_applied()
    {
        using var clin = await f.LoginAsync(DemoData.Clinician); // the scenario books this attending clinician
        var s = await Scenario.SignedEncounterAsync(f, clin);
        var late = await ApiClient.Body(await clin.UploadAsync("/api/v1/me/signature", ApiClient.Fixture("signature-progressive.jpg"), "late.jpg", "image/jpeg"));
        var lateId = late.GetProperty("id").GetGuid().ToString();
        var doc = await ApiClient.Body(await clin.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = s.NoteVersionId }));
        var snapshotSigs = await f.OwnerScalarAsync<string>("SELECT coalesce(snapshot->'branding'->>'signaturesByPractitioner', '') FROM documents.document_versions WHERE id=@d", ("d", doc.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(lateId, snapshotSigs, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "BRAND-006 concurrent uploads of the same slot are serialised: one active logo, no 500")]
    public async Task Concurrent_logo_uploads()
    {
        using var a1 = await f.LoginAsync(DemoData.ClinicAdmin);
        using var a2 = await f.LoginAsync(DemoData.ClinicAdmin);
        var results = await Task.WhenAll(
            a1.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("logo.png"), "a.png", "image/png"),
            a2.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("stamp.png"), "b.png", "image/png"));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM configuration.branding_assets WHERE facility_id=@f AND kind='Logo' AND status='Active'", ("f", DemoData.FacilityABole)));
    }

    [Fact(DisplayName = "BRAND-007 stored images carry no metadata: EXIF/comment payloads are stripped on upload")]
    public async Task Upload_strips_metadata()
    {
        using var d = await f.LoginAsync(DemoData.Clinician);
        var jpeg = ApiClient.Fixture("signature.jpg");
        var payload = System.Text.Encoding.ASCII.GetBytes("Exif\0\0GPSLatitude 9.03 <script>x</script>");
        byte[] app1 = [0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];
        var up = await d.UploadAsync("/api/v1/me/signature", [.. jpeg[..2], .. app1, .. jpeg[2..]], "s.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var stored = await (await d.GetAsync($"/api/v1/branding-assets/{(await ApiClient.Body(up)).GetProperty("id").GetGuid()}/content")).Content.ReadAsByteArrayAsync();
        var text = System.Text.Encoding.Latin1.GetString(stored);
        Assert.DoesNotContain("GPSLatitude", text);
        Assert.DoesNotContain("<script>", text);
        Assert.Equal(Canonical("signature.jpg"), stored);
    }

    [Fact(DisplayName = "HARDEN-001 bad inputs give 400 not 500; sign-in and verify responses carry no Server-Timing")]
    public async Task Input_hardening()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/analytics/overview?from=0001-01-01&to=0001-01-05")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/analytics/overview?from=9999-12-25&to=9999-12-31")).StatusCode);
        Assert.True((await owner.GetAsync("/api/v1/analytics/overview")).Headers.Contains("Server-Timing"));
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        var r = await admin.SendAsync(HttpMethod.Post, "/api/v1/facility/printers", new { name = "Bad", paper = 99, purpose = "AppointmentSlips" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var anon = f.Client();
        var login = await anon.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { username = "nobody", password = "wrong-password-123" });
        Assert.False(login.Headers.Contains("Server-Timing"));
        var verify = await anon.GetAsync("/api/v1/verify/NOPE-0000");
        Assert.False(verify.Headers.Contains("Server-Timing"));
    }

    [Fact(DisplayName = "THEME-001 accent colour validated for contrast; optimistic concurrency on settings")]
    public async Task Accent_colour_settings()
    {
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        var v = await SettingsVersion(admin);
        var tooLight = await admin.SendAsync(HttpMethod.Put, "/api/v1/facility/settings", new { accentColor = "#ffcc00" }, ifMatch: $"\"v{v}\"");
        Assert.Equal(HttpStatusCode.BadRequest, tooLight.StatusCode);
        Assert.Equal("insufficient_contrast", (await ApiClient.Body(tooLight)).GetProperty("code").GetString());
        var ok = await admin.SendAsync(HttpMethod.Put, "/api/v1/facility/settings", new { accentColor = "#0F5132" }, ifMatch: $"\"v{v}\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("#0f5132", (await ApiClient.Body(ok)).GetProperty("accentColor").GetString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(HttpMethod.Put, "/api/v1/facility/settings", new { accentColor = "#123456" }, ifMatch: $"\"v{v}\"")).StatusCode);
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(HttpMethod.Put, "/api/v1/facility/settings", new { accentColor = "#123456" }, ifMatch: $"\"v{v + 1}\"")).StatusCode);
        Assert.Equal("#0f5132", (await reception.GetJsonAsync("/api/v1/facility/settings")).GetProperty("accentColor").GetString());
    }

    [Fact(DisplayName = "PRINT-001 printer profiles: thermal only for slips, one default per purpose")]
    public async Task Printer_profiles()
    {
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        var clinicalThermal = await admin.SendAsync(HttpMethod.Post, "/api/v1/facility/printers", new { name = "Front desk roll", paper = "Thermal80", purpose = "ClinicalDocuments", isDefault = true });
        Assert.Equal(HttpStatusCode.BadRequest, clinicalThermal.StatusCode);
        Assert.Equal("thermal_not_allowed", (await ApiClient.Body(clinicalThermal)).GetProperty("code").GetString());
        var t1 = await ApiClient.Body(await admin.SendAsync(HttpMethod.Post, "/api/v1/facility/printers", new { name = "Reception thermal 80", paper = "Thermal80", purpose = "AppointmentSlips", isDefault = true }));
        Assert.Equal("Thermal", t1.GetProperty("kind").GetString());
        Assert.Equal(80, t1.GetProperty("widthMm").GetDouble());
        var t2 = await ApiClient.Body(await admin.SendAsync(HttpMethod.Post, "/api/v1/facility/printers", new { name = "Mini thermal 58", paper = "Thermal58", purpose = "AppointmentSlips", isDefault = true }));
        var a3 = await ApiClient.Body(await admin.SendAsync(HttpMethod.Post, "/api/v1/facility/printers", new { name = "Office laser A3", paper = "A3", purpose = "ClinicalDocuments", isDefault = false }));
        Assert.Equal("Standard", a3.GetProperty("kind").GetString());
        var list = (await admin.GetJsonAsync("/api/v1/facility/printers")).Items().ToList();
        Assert.Single(list, p => p.GetProperty("purpose").GetString() == "AppointmentSlips" && p.GetProperty("isDefault").GetBoolean());
        Assert.True(list.Single(p => p.GetProperty("id").GetGuid() == t2.GetProperty("id").GetGuid()).GetProperty("isDefault").GetBoolean());
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.NotEmpty((await reception.GetJsonAsync("/api/v1/facility/printers")).Items());
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(HttpMethod.Post, "/api/v1/facility/printers", new { name = "x", paper = "A4", purpose = "ClinicalDocuments" })).StatusCode);
    }

    [Fact(DisplayName = "DOC-BRAND-001 branded final carries frozen logo/stamp/signature; later logo change does not alter it; default paper A3")]
    public async Task Branded_document_is_frozen()
    {
        var (admin, _) = await UploadBrandingAsync();
        using var d = await f.LoginAsync(DemoData.Clinician);
        var sigUp = await d.UploadAsync("/api/v1/me/signature", ApiClient.Fixture("signature.jpg"), "sig.jpg", "image/jpeg");
        Assert.True(sigUp.StatusCode == HttpStatusCode.Created, await sigUp.Content.ReadAsStringAsync());
        var v = await SettingsVersion(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(HttpMethod.Put, "/api/v1/facility/settings", new { defaultClinicalPaper = "A3" }, ifMatch: $"\"v{v}\"")).StatusCode);
        try
        {
            var s = await Scenario.SignedEncounterAsync(f, d);
            var doc = await ApiClient.Body(await d.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = s.NoteVersionId }));
            Assert.Equal("A3", doc.GetProperty("paperSize").GetString());
            var docId = doc.GetProperty("id").GetGuid();
            var frozenLogoSha = await f.OwnerScalarAsync<string>("SELECT snapshot->'branding'->'logo'->>'sha256' FROM documents.document_versions WHERE id=@d", ("d", docId));

            // Replace the logo after the snapshot was frozen.
            await admin.UploadAsync("/api/v1/facility/branding/logo", ApiClient.Fixture("stamp.png"), "new.png", "image/png");

            var preview = await (await d.GetAsync($"/api/v1/documents/{docId}/preview")).Content.ReadAsByteArrayAsync();
            Assert.Equal(HttpStatusCode.OK, (await d.SendAsync(HttpMethod.Post, $"/api/v1/documents/{docId}/finalize", idempotencyKey: Scenario.Key())).StatusCode);
            var final = await (await d.GetAsync($"/api/v1/documents/{docId}/download")).Content.ReadAsByteArrayAsync();
            using var fp = PdfDocument.Open(final);
            using var pp = PdfDocument.Open(preview);
            Assert.Equal(841.9, fp.GetPage(1).Width, 0); // A3
            Assert.Equal(4, fp.GetPages().Sum(p => p.GetImages().Count()));  // logo + signature + stamp + QR
            Assert.Equal(2, pp.GetPages().Sum(p => p.GetImages().Count()));  // logo + QR (no signature/stamp on preview)
            Assert.Equal(frozenLogoSha, await f.OwnerScalarAsync<string>("SELECT snapshot->'branding'->'logo'->>'sha256' FROM documents.document_versions WHERE id=@d", ("d", docId)));
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Canonical("logo.png"))), frozenLogoSha);
        }
        finally
        {
            var v2 = await SettingsVersion(admin);
            await admin.SendAsync(HttpMethod.Put, "/api/v1/facility/settings", new { defaultClinicalPaper = "A4" }, ifMatch: $"\"v{v2}\"");
            admin.Dispose();
        }
    }

    [Fact(DisplayName = "PRINT-002 appointment slip renders on thermal 58/80 and A5; audited; other tenant 404")]
    public async Task Appointment_slips()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var book = await r.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerAbebe, startsAt = Scenario.NextSlot(), durationMinutes = 15 }, Scenario.Key());
        var apptId = (await ApiClient.Body(book)).GetProperty("id").GetGuid();
        foreach (var (paper, width) in new[] { ("Thermal80", 226.8), ("Thermal58", 164.4), ("A5", 419.5) })
        {
            var res = await r.GetAsync($"/api/v1/appointments/{apptId}/slip?paper={paper}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var pdf = PdfDocument.Open(await res.Content.ReadAsByteArrayAsync());
            Assert.Equal(width, pdf.GetPage(1).Width, 0);
        }

        Assert.Equal(3L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='appointment.slip.render' AND resource_id=@a", ("a", apptId)));
        using var b = await f.LoginAsync(DemoData.ReceptionB);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/appointments/{apptId}/slip")).StatusCode);
    }

    [Fact(DisplayName = "AUTO-001 signing triggers an automatically prepared draft summary exactly once (idempotent redelivery)")]
    public async Task Auto_prepare_on_sign()
    {
        var s = await Scenario.SignedEncounterAsync(f);
        var messageId = await f.OwnerScalarAsync<Guid>("SELECT id FROM platform.outbox_messages WHERE payload->>'noteVersionId' = @v", ("v", s.NoteVersionId.ToString()));
        async Task ProcessAsync()
        {
            using var scope = f.Factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
            for (var i = 0; i < 20 && await f.OwnerScalarAsync<string>("SELECT status FROM platform.outbox_messages WHERE id=@m", ("m", messageId)) == "Pending"; i++)
            {
                await processor.ProcessBatchAsync(CancellationToken.None);
            }
        }

        await ProcessAsync();
        Assert.Equal("Processed", await f.OwnerScalarAsync<string>("SELECT status FROM platform.outbox_messages WHERE id=@m", ("m", messageId)));
        var docs = (await s.Clinician.GetJsonAsync($"/api/v1/documents?encounterId={s.EncounterId}")).Items().ToList();
        var auto = Assert.Single(docs);
        Assert.Equal("Draft", auto.GetProperty("status").GetString());
        Assert.Equal(s.NoteVersionId, auto.GetProperty("noteVersionId").GetGuid());

        // Simulate at-least-once redelivery: no duplicate draft.
        await f.OwnerScalarAsync<object>("UPDATE platform.outbox_messages SET status='Pending', next_attempt_at=now() - interval '1 second' WHERE id=@m", ("m", messageId));
        await ProcessAsync();
        Assert.Single((await s.Clinician.GetJsonAsync($"/api/v1/documents?encounterId={s.EncounterId}")).Items());
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='document.create' AND actor_user_id='00000000-0000-7000-8000-00000000a070'") >= 1);
    }

    [Fact(DisplayName = "AUTO-002 no-show sweeper marks only long-overdue booked appointments, audited, idempotent")]
    public async Task No_show_sweep()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var pid = await Scenario.RegisterPatientAsync(r);
        var overdue = (await ApiClient.Body(await r.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerHana, startsAt = Scenario.NextSlot(), durationMinutes = 15 }, Scenario.Key()))).GetProperty("id").GetGuid();
        var recent = (await ApiClient.Body(await r.SendAsync(HttpMethod.Post, "/api/v1/appointments", new { patientId = pid, practitionerId = DemoData.PractitionerHana, startsAt = Scenario.NextSlot(), durationMinutes = 15 }, Scenario.Key()))).GetProperty("id").GetGuid();
        await f.OwnerScalarAsync<object>("UPDATE scheduling.appointments SET starts_at = now() - interval '5 hours', ends_at = now() - interval '4 hours 45 minutes' WHERE id=@a", ("a", overdue));
        await f.OwnerScalarAsync<object>("UPDATE scheduling.appointments SET starts_at = now() - interval '61 minutes', ends_at = now() - interval '46 minutes' WHERE id=@a", ("a", recent));

        var sweeper = f.Factory.Services.GetRequiredService<NoShowSweeper>();
        Assert.True(await sweeper.SweepOnceAsync(CancellationToken.None) >= 1);
        Assert.Equal("NoShow", await f.OwnerScalarAsync<string>("SELECT status FROM scheduling.appointments WHERE id=@a", ("a", overdue)));
        Assert.Equal("Booked", await f.OwnerScalarAsync<string>("SELECT status FROM scheduling.appointments WHERE id=@a", ("a", recent)));
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='appointment.no_show.auto' AND resource_id=@a", ("a", overdue)));
        await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action='appointment.no_show.auto' AND resource_id=@a", ("a", overdue)));
    }
}
