using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Serk.Clinic.Analytics.Domain;
using Serk.Clinic.Configuration.Domain;
using Serk.Clinic.Platform.Backup;
using Serk.Clinic.Platform.Export;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.UnitTests;

public class TabularWriterTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("-12.50", "-12.50")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Csv_cells_are_quoted_and_formulas_neutralised(string input, string expected) =>
        Assert.Equal(expected, TabularWriter.Cell(input));

    [Fact]
    public void Csv_has_utf8_bom_header_and_invariant_values()
    {
        var bytes = TabularWriter.Csv(["name", "amount", "day"], [["ሰላም", 1234.5m, new DateOnly(2026, 11, 14)]]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("name,amount,day\r\nሰላም,1234.50,2026-11-14\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public void Json_export_keeps_numbers_as_numbers()
    {
        var bytes = TabularWriter.Json("finance.payments", DateTimeOffset.UnixEpoch, ["amount", "roles"], [[12.30m, new[] { "Owner", "Cashier" }]]);
        using var doc = JsonDocument.Parse(bytes);
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal(12.30m, row.GetProperty("amount").GetDecimal());
        Assert.Equal(2, row.GetProperty("roles").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("rowCount").GetInt32());
    }
}

public class BackupArchiveTests
{
    private const int FastIterations = 100_000;

    [Fact]
    public void Round_trip_with_the_right_passphrase()
    {
        var plain = Encoding.UTF8.GetBytes("synthetic clinic data");
        var file = BackupArchive.Encrypt(plain, "correct horse battery", FastIterations);
        Assert.Equal(plain, BackupArchive.TryDecrypt(file, "correct horse battery", out _));
        Assert.DoesNotContain(Encoding.UTF8.GetString(file), "synthetic", StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_passphrase_or_any_changed_byte_is_refused()
    {
        var file = BackupArchive.Encrypt([1, 2, 3, 4], "correct horse battery", FastIterations);
        Assert.Null(BackupArchive.TryDecrypt(file, "wrong horse battery", out var p1));
        Assert.Contains("passphrase", p1, StringComparison.OrdinalIgnoreCase);
        foreach (var index in new[] { 9, 20, 40, file.Length - 1 })
        {
            var tampered = (byte[])file.Clone();
            tampered[index] ^= 0x01;
            Assert.Null(BackupArchive.TryDecrypt(tampered, "correct horse battery", out _));
        }

        Assert.Null(BackupArchive.TryDecrypt("not a backup at all, definitely"u8.ToArray(), "x", out var p2));
        Assert.Contains("not a SERK", p2, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_backup_uses_a_fresh_salt_and_nonce()
    {
        var a = BackupArchive.Encrypt([1], "correct horse battery", FastIterations);
        var b = BackupArchive.Encrypt([1], "correct horse battery", FastIterations);
        Assert.NotEqual(a[12..40], b[12..40]);
    }

    private static byte[] Archive(Guid tenant, string journalLines, bool corruptTable = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var tables = new List<object>();
            foreach (var t in BackupCatalog.Tables)
            {
                var content = t.Name == "finance.journal_lines" ? journalLines : "";
                var bytes = Encoding.UTF8.GetBytes(content);
                using (var s = zip.CreateEntry($"tables/{t.Name}.jsonl").Open())
                {
                    s.Write(corruptTable && t.Name == "patients.patients" ? "{\"x\":1}\n"u8.ToArray() : bytes);
                }

                tables.Add(new
                {
                    name = t.Name,
                    file = $"tables/{t.Name}.jsonl",
                    rows = content.Count(c => c == '\n'),
                    sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                });
            }

            using var m = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(m, new { format = BackupCatalog.Format, organisationId = tenant, organisationName = "Synthetic", createdAt = DateTimeOffset.UnixEpoch, tables, objects = Array.Empty<object>() });
        }

        return ms.ToArray();
    }

    [Fact]
    public void Inspection_accepts_a_consistent_archive_and_checks_the_ledger()
    {
        var tenant = Guid.NewGuid();
        var e = Guid.NewGuid();
        var ok = BackupService.Inspect(Archive(tenant, $"{{\"entry_id\":\"{e}\",\"debit\":10.00,\"credit\":0}}\n{{\"entry_id\":\"{e}\",\"debit\":0,\"credit\":10.00}}\n"), tenant, []);
        Assert.True(ok.Valid, string.Join(";", ok.Problems));
        Assert.True(ok.LedgerBalanced);

        var unbalanced = BackupService.Inspect(Archive(tenant, $"{{\"entry_id\":\"{e}\",\"debit\":10.00,\"credit\":0}}\n"), tenant, []);
        Assert.False(unbalanced.Valid);
        Assert.False(unbalanced.LedgerBalanced);
    }

    [Fact]
    public void Inspection_detects_altered_tables_and_foreign_organisations()
    {
        var tenant = Guid.NewGuid();
        var altered = BackupService.Inspect(Archive(tenant, "", corruptTable: true), tenant, []);
        Assert.False(altered.Valid);
        Assert.Contains(altered.Problems, p => p.Contains("patients.patients", StringComparison.Ordinal));

        var foreign = BackupService.Inspect(Archive(tenant, ""), Guid.NewGuid(), []);
        Assert.False(foreign.Valid);
        Assert.False(foreign.SameOrganisation);

        Assert.False(BackupService.Inspect("garbage"u8.ToArray(), tenant, []).Valid);
    }

    [Fact]
    public void Inspection_rejects_duplicate_references_and_decompression_bombs()
    {
        var tenant = Guid.NewGuid();
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var s = zip.CreateEntry("tables/a.jsonl").Open())
                {
                    s.Write("\n"u8);
                }

                using var m = zip.CreateEntry("manifest.json").Open();
                var dup = new[] { new { name = "a", file = "tables/a.jsonl", rows = 1, sha256 = "x" }, new { name = "b", file = "tables/a.jsonl", rows = 1, sha256 = "x" } };
                JsonSerializer.Serialize(m, new { format = BackupCatalog.Format, organisationId = tenant, createdAt = DateTimeOffset.UnixEpoch, tables = dup, objects = Array.Empty<object>() });
            }

            var r = BackupService.Inspect(ms.ToArray(), tenant, []);
            Assert.False(r.Valid);
            Assert.Contains(r.Problems, p => p.Contains("twice", StringComparison.Ordinal));
        }

        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var s = zip.CreateEntry("tables/bomb.jsonl", CompressionLevel.SmallestSize).Open())
                {
                    var zeros = new byte[1024 * 1024];
                    for (var i = 0; i < 70; i++)
                    {
                        s.Write(zeros); // 70 MB of zeros compresses to well under 1 MB
                    }
                }

                using var m = zip.CreateEntry("manifest.json").Open();
                JsonSerializer.Serialize(m, new { format = BackupCatalog.Format, organisationId = tenant, createdAt = DateTimeOffset.UnixEpoch, tables = new[] { new { name = "bomb", file = "tables/bomb.jsonl", rows = 0, sha256 = "x" } }, objects = Array.Empty<object>() });
            }

            Assert.True(ms.Length < 1024 * 1024);
            var r = BackupService.Inspect(ms.ToArray(), tenant, []);
            Assert.False(r.Valid);
            Assert.Contains(r.Problems, p => p.Contains("expands to more than", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Catalogue_never_lists_a_table_twice_or_both_included_and_excluded()
    {
        var names = BackupCatalog.Tables.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Empty(names.Intersect(BackupCatalog.Excluded.Keys));
        Assert.DoesNotContain(BackupCatalog.Tables, t => t.Sql.Contains("password_hash", StringComparison.Ordinal));
    }
}

public class ClinicProfileRulesTests
{
    private static Dictionary<string, string> Fields(params (string, string)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    [Fact]
    public void Valid_profile_passes_and_completeness_counts_letterhead_fields()
    {
        var f = Fields(("displayName", "Bole Dental"), ("motto", "Gentle care"), ("email", "info@bole.example"), ("website", "https://bole.example"), ("phone", "+251 11 555 0100"));
        Assert.True(ClinicProfileRules.Validate(f, "am", "Both", "ETB").IsSuccess);
        Assert.Equal(44, ClinicProfileRules.Completeness(f)); // 4 of 9 important fields
    }

    [Theory]
    [InlineData("email", "not-an-email")]
    [InlineData("website", "javascript:alert(1)")]
    [InlineData("website", "bole.example")]
    [InlineData("phone", "call me")]
    [InlineData("motto", "Best \u202Eecivres")]
    [InlineData("displayName", "Clinic\u200B")]
    public void Invalid_contacts_are_rejected(string field, string value)
    {
        var r = ClinicProfileRules.Validate(Fields((field, value)), "en", "Both", "ETB");
        Assert.False(r.IsSuccess);
        Assert.True(r.Error!.Fields!.ContainsKey(field));
    }

    [Fact]
    public void Lengths_language_calendar_and_currency_are_checked()
    {
        var r = ClinicProfileRules.Validate(Fields(("motto", new string('x', 141))), "fr", "Julian", "birr");
        Assert.False(r.IsSuccess);
        Assert.True(r.Error!.Fields!.Keys.ToHashSet().IsSupersetOf(["motto", "defaultLanguage", "calendarDisplay", "currency"]));
    }
}

public class InsightRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private static InsightFacts Base => new() { Today = new DateOnly(2026, 11, 14), Now = Now, Currency = "ETB", LastBackupAt = Now.AddDays(-1), LastVerifiedBackupAt = Now.AddHours(-20) };

    private static IReadOnlyList<string> Codes(InsightFacts f, params string[] caps) => InsightRules.Evaluate(f, caps.Contains).Select(i => i.Code).ToList();

    private static readonly string[] All = [Capabilities.BackupManage, Capabilities.BrandingManage, Capabilities.AnalyticsRead, Capabilities.FinanceRead];

    [Fact]
    public void Quiet_clinic_has_no_insights() => Assert.Empty(Codes(Base, All));

    [Fact]
    public void Backups_never_taken_stale_or_unverified()
    {
        Assert.Contains("backup.never", Codes(Base with { LastBackupAt = null, LastVerifiedBackupAt = null }, All));
        Assert.Contains("backup.stale", Codes(Base with { LastBackupAt = Now.AddDays(-8), LastVerifiedBackupAt = Now.AddDays(-8) }, All));
        Assert.DoesNotContain("backup.stale", Codes(Base with { LastBackupAt = Now.AddDays(-6), LastVerifiedBackupAt = Now.AddDays(-6) }, All));
        Assert.Contains("backup.unverified", Codes(Base with { LastVerifiedBackupAt = null }, All));
        Assert.Empty(Codes(Base with { LastBackupAt = null }, Capabilities.FinanceRead)); // not shown without backup permission
    }

    [Fact]
    public void No_show_rise_needs_sample_floor_and_margin()
    {
        var rising = Base with { RecentAppointments = 20, RecentNoShows = 5, BaselineAppointments = 100, BaselineNoShows = 10 };
        Assert.Contains("schedule.no-shows-rising", Codes(rising, All));
        Assert.DoesNotContain("schedule.no-shows-rising", Codes(rising with { RecentAppointments = 9, RecentNoShows = 5 }, All)); // too few
        Assert.DoesNotContain("schedule.no-shows-rising", Codes(rising with { BaselineNoShows = 20 }, All)); // 25% vs 20%: rise below 10 points
    }

    [Fact]
    public void Money_rules_fire_on_their_thresholds()
    {
        var f = Base with
        {
            StaleOpenDrawers = 1,
            UnclosedDays = [new DateOnly(2026, 11, 12)],
            OverdueInvoices = 2,
            OverdueAmount = 900m,
            FrequentVariances = [("Cashier One", 3)],
            YesterdayRevenue = 600m,
            SameWeekdayAverageRevenue = 1000m,
        };
        var codes = Codes(f, All);
        Assert.Equal(["finance.stale-drawers"], InsightRules.Evaluate(f, All.Contains).Where(i => i.Severity == InsightSeverity.Urgent).Select(i => i.Code));
        Assert.Contains("finance.unclosed-days", codes);
        Assert.Contains("finance.overdue-invoices", codes);
        Assert.Contains("finance.repeated-variance", codes);
        Assert.Contains("finance.revenue-down", codes);
        Assert.DoesNotContain("finance.revenue-down", Codes(f with { YesterdayRevenue = 750m }, All)); // -25%: under threshold
        Assert.DoesNotContain(Codes(f, Capabilities.AnalyticsRead), c => c.StartsWith("finance.", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_insight_explains_its_rule_and_numbers()
    {
        var f = Base with { LastBackupAt = null, StaleOpenDrawers = 2, OverdueInvoices = 1, OverdueAmount = 10m, TomorrowBooked = 12, AverageDailyBooked = 6 };
        var list = InsightRules.Evaluate(f, All.Contains);
        Assert.Contains(list, i => i.Code == "schedule.busy-tomorrow");
        Assert.All(list, i => Assert.False(string.IsNullOrWhiteSpace(i.Rule)));
        Assert.Contains("2 drawer", list.Single(i => i.Code == "finance.stale-drawers").Detail, StringComparison.Ordinal);
        Assert.Equal(InsightSeverity.Urgent, list[0].Severity); // most serious first
    }
}
