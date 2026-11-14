using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;
using Serk.Clinic.Platform.Backup;

namespace Serk.Clinic.IntegrationTests;

/// <summary>Owner setup: clinic profile, roles matrix, exports, encrypted backups and insights.</summary>
[Collection(ApiCollection.Name)]
public class OwnerToolsTests(SerkApiFixture f)
{
    private const string Passphrase = "Synthetic backup passphrase 2026!";

    private static object Profile(string motto, string email = "info@bole.example") => new
    {
        displayName = "Bole Smile Dental",
        legalName = "Bole Smile Dental PLC",
        motto,
        addressLine1 = "Africa Avenue, House 12",
        addressLine2 = "",
        city = "Addis Ababa",
        region = "Addis Ababa",
        country = "Ethiopia",
        postalCode = "1000",
        phone = "+251 11 555 0100",
        altPhone = "",
        email,
        website = "https://bole.example",
        taxId = "0012345678",
        registrationNo = "AA/123/2026",
        licenceNo = "MOH-DEN-0042",
        openingHours = "Mon–Sat 08:00–18:00",
        receiptFooter = "Thank you for choosing us.",
        documentFooter = "Synthetic test clinic — not a real facility.",
        defaultLanguage = "am",
        calendarDisplay = "Both",
        currency = "ETB",
    };

    [Fact(DisplayName = "PROFILE-001 owner sets the clinic profile with optimistic concurrency; validation and permissions enforced")]
    public async Task Clinic_profile()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var current = await owner.GetAsync("/api/v1/facility/profile");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var etag = current.Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await owner.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("Gentle care"))).StatusCode); // no If-Match
        var bad = await owner.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("Gentle care", email: "nope"), ifMatch: etag);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("email", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var ok = await owner.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("Gentle care, bright smiles"), ifMatch: etag);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ApiClient.Body(ok);
        Assert.Equal("Gentle care, bright smiles", body.GetProperty("motto").GetString());
        Assert.Equal(100, body.GetProperty("completeness").GetInt32());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await owner.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("Stale"), ifMatch: etag)).StatusCode);

        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal("Bole Smile Dental", (await reception.GetJsonAsync("/api/v1/facility/profile")).GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("x"), ifMatch: ok.Headers.ETag!.Tag)).StatusCode);

        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'clinic_profile.update' AND metadata->>'changedFields' LIKE '%motto%'") >= 1);
    }

    [Fact(DisplayName = "PROFILE-002 a prepared document freezes the letterhead; the profile changing later does not alter it")]
    public async Task Letterhead_is_frozen_into_documents()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var current = await owner.GetAsync("/api/v1/facility/profile");
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("Motto at preparation"), ifMatch: current.Headers.ETag!.Tag)).StatusCode);

        var signed = await Scenario.SignedEncounterAsync(f);
        var clinician = signed.Clinician;
        var doc = await clinician.SendAsync(HttpMethod.Post, "/api/v1/documents", new { noteVersionId = signed.NoteVersionId });
        Assert.True(doc.IsSuccessStatusCode, await doc.Content.ReadAsStringAsync());
        var docId = (await ApiClient.Body(doc)).GetProperty("id").GetGuid();

        var again = await owner.GetAsync("/api/v1/facility/profile");
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(HttpMethod.Put, "/api/v1/facility/profile", Profile("Motto changed afterwards"), ifMatch: again.Headers.ETag!.Tag)).StatusCode);

        Assert.Equal("Motto at preparation", await f.OwnerScalarAsync<string>(
            "SELECT snapshot->'branding'->'letterhead'->>'motto' FROM documents.document_versions WHERE id = @id", ("id", docId)));
        var preview = await clinician.GetAsync($"/api/v1/documents/{docId}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await preview.Content.ReadAsByteArrayAsync())[..4]), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ROLES-001 everyone sees roles and responsibilities; only staff administrators see the team")]
    public async Task Roles_matrix_and_team()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var matrix = await reception.GetJsonAsync("/api/v1/organisation/roles");
        var ownerRole = matrix.GetProperty("roles").Items().Single(r => r.GetProperty("code").GetString() == "Owner");
        Assert.NotEmpty(ownerRole.GetProperty("responsibilities").Items());
        Assert.Contains(ownerRole.GetProperty("capabilities").Items(), c => c.GetString() == "backup.manage");
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/v1/organisation/team")).StatusCode);

        using var owner = await f.LoginAsync(DemoData.Owner);
        var team = await owner.GetAsync("/api/v1/organisation/team");
        var text = await team.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);
        Assert.Contains("reception.a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reception.b", text, StringComparison.Ordinal); // other organisation
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "EXPORT-001 the catalogue lists only permitted datasets; running others is refused and recorded")]
    public async Task Export_permissions()
    {
        using var cashier = await f.LoginAsync(DemoData.Cashier);
        Assert.Empty((await cashier.GetJsonAsync("/api/v1/exports")).Items());
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.SendAsync(HttpMethod.Post, "/api/v1/exports/finance.payments", new { format = "csv" })).StatusCode);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE outcome = 'denied:export:finance.payments'") >= 1);

        using var owner = await f.LoginAsync(DemoData.Owner);
        var codes = (await owner.GetJsonAsync("/api/v1/exports")).Items().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.Contains("finance.journal", codes);
        Assert.Contains("patients.register", codes);
        Assert.Contains("staff.access", codes);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/no.such", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/finance.journal", new { from = "2026-01-01", to = "2027-06-01" })).StatusCode);
    }

    [Fact(DisplayName = "EXPORT-002 operational export: CSV with fingerprint, logged and audited; the log is append-only")]
    public async Task Operational_export_is_logged()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var r = await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/staff.access", new { format = "csv" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        var sha = r.Headers.GetValues("X-Serk-Export-Sha256").Single();
        Assert.Equal(sha, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("﻿username,display_name", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("reception.b", csv, StringComparison.Ordinal);
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());

        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM platform.export_log WHERE sha256 = @s AND dataset = 'staff.access'", ("s", sha)));
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'data.export' AND metadata->>'sha256' = @s", ("s", sha)) == 1);
        await using var conn = new NpgsqlConnection(f.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE platform.export_log SET row_count = 0 WHERE sha256 = @s", conn);
        cmd.Parameters.AddWithValue("s", sha);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("serk_immutable", ex.MessageText, StringComparison.Ordinal);

        var json = await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/finance.trial-balance", new { format = "json" });
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        using var doc = JsonDocument.Parse(await json.Content.ReadAsByteArrayAsync());
        Assert.Equal(doc.RootElement.GetProperty("rows").Items().Sum(x => x.GetProperty("debit").GetDecimal()),
            doc.RootElement.GetProperty("rows").Items().Sum(x => x.GetProperty("credit").GetDecimal()));
    }

    [Fact(DisplayName = "EXPORT-003 patient data export needs a purpose and the user's password; stays within the organisation")]
    public async Task Patient_export_step_up()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        var mrnName = Scenario.UniqueName("Exp");
        await Scenario.RegisterPatientAsync(reception, given: mrnName);

        using var owner = await f.LoginAsync(DemoData.Owner);
        var noPurpose = await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/patients.register", new { format = "csv", password = SerkApiFixture.DemoPassword });
        Assert.Equal(HttpStatusCode.BadRequest, noPurpose.StatusCode);
        var wrong = await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/patients.register", new { format = "csv", purpose = "Quarterly register check", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type = 'step_up.failed' AND outcome = 'export:patients.register'") >= 1);

        var ok = await owner.SendAsync(HttpMethod.Post, "/api/v1/exports/patients.register", new { format = "csv", purpose = "Quarterly register check", password = SerkApiFixture.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var csv = await ok.Content.ReadAsStringAsync();
        Assert.Contains(mrnName, csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Tenant B", csv, StringComparison.Ordinal);
        Assert.Equal(0, await f.OwnerScalarAsync<int>("SELECT failed_login_count FROM identity.staff_users WHERE username = @u", ("u", DemoData.Owner)));
        Assert.Equal("Quarterly register check", await f.OwnerScalarAsync<string>(
            "SELECT purpose FROM platform.export_log WHERE dataset = 'patients.register' ORDER BY created_at DESC LIMIT 1"));
    }

    [Fact(DisplayName = "BACKUP-001 every organisation table is either backed up or excluded with a reason")]
    public async Task Backup_catalogue_covers_all_tables()
    {
        await using var conn = new NpgsqlConnection(f.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT c.table_schema || '.' || c.table_name FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.column_name = 'tenant_id' AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
            """, conn);
        var tables = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                tables.Add(r.GetString(0));
            }
        }

        var covered = BackupCatalog.Tables.Select(t => t.Name).Concat(BackupCatalog.Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(tables, t => !covered.Contains(t));
    }

    [Fact(DisplayName = "BACKUP-002 encrypted backup: step-up, own organisation only, no secrets, verifiable; tampering and wrong passphrase detected")]
    public async Task Backup_create_and_verify()
    {
        using var reception = await f.LoginAsync(DemoData.Reception);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(HttpMethod.Post, "/api/v1/backups", new { passphrase = Passphrase, password = SerkApiFixture.DemoPassword, purpose = "Integration test backup" })).StatusCode);

        using var owner = await f.LoginAsync(DemoData.Owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/backups", new { passphrase = "short", password = SerkApiFixture.DemoPassword, purpose = "Integration test backup" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/backups", new { passphrase = SerkApiFixture.DemoPassword, password = SerkApiFixture.DemoPassword, purpose = "Integration test backup" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(HttpMethod.Post, "/api/v1/backups", new { passphrase = Passphrase, password = "wrong", purpose = "Integration test backup" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, "/api/v1/backups", new { passphrase = Passphrase, password = SerkApiFixture.DemoPassword })).StatusCode); // purpose required
        var created = await owner.SendAsync(HttpMethod.Post, "/api/v1/backups", new { passphrase = Passphrase, password = SerkApiFixture.DemoPassword, purpose = "Integration test backup" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var file = await created.Content.ReadAsByteArrayAsync();
        Assert.Equal(created.Headers.GetValues("X-Serk-Backup-Sha256").Single(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)));
        Assert.DoesNotContain("Selam", Encoding.UTF8.GetString(file), StringComparison.Ordinal); // encrypted at rest

        var plain = BackupArchive.TryDecrypt(file, Passphrase, out var problem);
        Assert.True(plain is not null, problem);
        using var zip = new ZipArchive(new MemoryStream(plain!), ZipArchiveMode.Read);
        var all = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("tables/", StringComparison.Ordinal)).Select(e =>
        {
            using var s = new StreamReader(e.Open());
            return s.ReadToEnd();
        }));
        Assert.Contains(DemoData.PatientSelam.ToString(), all, StringComparison.Ordinal);
        Assert.DoesNotContain(DemoData.PatientTenantB.ToString(), all, StringComparison.Ordinal);
        Assert.DoesNotContain(DemoData.TenantB.ToString(), all, StringComparison.Ordinal);
        Assert.DoesNotContain("password_hash", all, StringComparison.Ordinal);
        Assert.DoesNotContain("AQAAAA", all, StringComparison.Ordinal); // ASP.NET password hash prefix
        Assert.DoesNotContain("challenge_sha256", all, StringComparison.Ordinal); // recipient verification secrets
        Assert.Equal(1L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'backup.create' AND reason = 'Integration test backup' AND metadata->>'sha256' = @s",
            ("s", created.Headers.GetValues("X-Serk-Backup-Sha256").Single())));

        var verified = await owner.UploadFormAsync("/api/v1/backups/verify", file, "b.serkbak", new Dictionary<string, string> { ["passphrase"] = Passphrase });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var report = await ApiClient.Body(verified);
        Assert.True(report.GetProperty("valid").GetBoolean(), report.ToString());
        Assert.True(report.GetProperty("sameOrganisation").GetBoolean());
        Assert.Equal(BackupCatalog.Tables.Count, report.GetProperty("tables").GetArrayLength());

        var wrongPass = await ApiClient.Body(await owner.UploadFormAsync("/api/v1/backups/verify", file, "b.serkbak", new Dictionary<string, string> { ["passphrase"] = "not the passphrase" }));
        Assert.False(wrongPass.GetProperty("valid").GetBoolean());
        var tampered = (byte[])file.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        Assert.False((await ApiClient.Body(await owner.UploadFormAsync("/api/v1/backups/verify", tampered, "b.serkbak", new Dictionary<string, string> { ["passphrase"] = Passphrase }))).GetProperty("valid").GetBoolean());

        // Uploads above the global 1 MB request limit are accepted for verification only (3 MB of junk: a clear report, not 413).
        var junk = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(junk);
        var big = await owner.UploadFormAsync("/api/v1/backups/verify", junk, "big.serkbak", new Dictionary<string, string> { ["passphrase"] = Passphrase });
        Assert.Equal(HttpStatusCode.OK, big.StatusCode);
        Assert.False((await ApiClient.Body(big)).GetProperty("valid").GetBoolean());
        var bigElsewhere = await owner.UploadAsync("/api/v1/facility/branding/logo", junk, "big.png", "image/png");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigElsewhere.StatusCode);

        var history = (await owner.GetJsonAsync("/api/v1/backups")).Items().Select(h => h.GetProperty("action").GetString()).ToList();
        Assert.Contains("Created", history);
        Assert.Contains("Verified", history);
        Assert.Contains("VerifyFailed", history);
    }

    [Fact(DisplayName = "INSIGHT-001 insights are explained and limited to the caller's permissions")]
    public async Task Insights()
    {
        using var owner = await f.LoginAsync(DemoData.Owner);
        var o = await owner.GetJsonAsync("/api/v1/insights");
        Assert.NotEmpty(o.GetProperty("setup").Items());
        Assert.All(o.GetProperty("insights").Items(), i => Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("rule").GetString())));

        using var reception = await f.LoginAsync(DemoData.Reception);
        var r = await reception.GetJsonAsync("/api/v1/insights");
        Assert.Empty(r.GetProperty("setup").Items());
        Assert.DoesNotContain(r.GetProperty("insights").Items(), i => i.GetProperty("area").GetString() is "Money" or "Data safety" or "Setup");
    }
}
