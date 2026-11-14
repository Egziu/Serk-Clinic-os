using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Export;
using Serk.Clinic.Platform.Storage;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Backup;

public sealed record CreateBackupRequest(string? Passphrase, string? Password, string? Purpose);

public sealed record BackupTableReport(string Name, long Rows, bool Ok);

public sealed record BackupVerification(
    bool Valid,
    string? Format,
    DateTimeOffset? CreatedAt,
    string? OrganisationName,
    bool SameOrganisation,
    IReadOnlyList<BackupTableReport> Tables,
    int Objects,
    int ObjectsOk,
    bool? LedgerBalanced,
    IReadOnlyList<string> Problems);

public sealed record BackupLogDto(Guid Id, string Action, string? ArchiveSha256, long SizeBytes, int TableCount, long RowCount, bool? LedgerBalanced, string Detail, Guid UserId, DateTimeOffset CreatedAt);

public sealed record BackupFile(byte[] Bytes, string FileName, string Sha256, int Tables, long Rows);

/// <summary>
/// Organisation backup: every row of this organisation (see <see cref="BackupCatalog"/>) plus its stored files (logos,
/// stamps, signatures, issued PDFs), packed as a ZIP with a manifest of SHA-256 fingerprints and encrypted with the
/// owner's passphrase. The server never keeps the file or the passphrase. Creating one needs <c>backup.manage</c> and the
/// user's password; verifying one checks every fingerprint, the row counts and that the ledger balances.
/// </summary>
public sealed class BackupService(DbSession session, RequestContext context, AuditTrail audit, IStepUpVerifier stepUp, IObjectStore objects, TimeProvider clock)
{
    /// <summary>Largest uncompressed backup the app produces or will inspect (bigger organisations use operator backups).</summary>
    public const long MaxPlainBytes = 64L * 1024 * 1024;

    /// <summary>Largest encrypted file accepted for verification (the only route allowed above the global 1 MB limit).</summary>
    public const long MaxUploadBytes = 70L * 1024 * 1024;

    public const string VerifyPath = "/api/v1/backups/verify";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<Result<BackupFile>> CreateAsync(CreateBackupRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var passphrase = request.Passphrase ?? "";
        if (passphrase.Length < BackupArchive.MinPassphraseLength || passphrase.Length > 200 || passphrase.Distinct().Count() < 6)
        {
            return Error.Validation("weak_passphrase", $"Use a backup passphrase of at least {BackupArchive.MinPassphraseLength} characters with variety (it cannot be recovered — store it safely).",
                new Dictionary<string, string[]> { ["passphrase"] = ["At least 12 varied characters."] });
        }

        var purpose = request.Purpose?.Trim();
        if (purpose is null || purpose.Length is < 10 or > 300)
        {
            return Error.Validation("purpose_required", "State why this backup is taken (10–300 characters), e.g. 'Weekly backup to the office safe'. A backup contains every clinical record.",
                new Dictionary<string, string[]> { ["purpose"] = ["10–300 characters."] });
        }

        if (string.Equals(passphrase, request.Password, StringComparison.Ordinal))
        {
            return Error.Validation("passphrase_is_password", "Do not reuse your sign-in password as the backup passphrase.",
                new Dictionary<string, string[]> { ["passphrase"] = ["Must differ from your password."] });
        }

        if (!await stepUp.VerifyAsync(actor.UserId, request.Password, "backup.create", ct))
        {
            return Error.Forbidden("step_up_failed", "Confirm with your own current password to create a backup.");
        }

        var now = clock.GetUtcNow();
        using var zipStream = new MemoryStream();
        var manifestTables = new List<object>();
        long totalRows = 0;
        decimal debits = 0, credits = 0;
        var objectEntries = new List<object>();
        string organisation = "";
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var table in BackupCatalog.Tables)
            {
                var (bytes, rows) = await DumpAsync(table, actor.TenantId, ct);
                totalRows += rows;
                if (zipStream.Length > MaxPlainBytes)
                {
                    return Error.Rule("backup_too_large", "This organisation's data is too large for an in-app backup; ask the operator for a database backup.");
                }

                Write(zip, $"tables/{table.Name}.jsonl", bytes);
                manifestTables.Add(new { name = table.Name, file = $"tables/{table.Name}.jsonl", rows, sha256 = Sha(bytes) });
                if (table.Name == "finance.journal_lines")
                {
                    (debits, credits) = SumLedger(bytes);
                }
                else if (table.Name == "identity.tenants")
                {
                    organisation = ReadString(bytes, "name");
                }
            }

            foreach (var (key, sha) in await ObjectKeysAsync(ct))
            {
                var content = await objects.GetVerifiedAsync(key, sha, ct);
                Write(zip, "objects/" + key, content);
                if (zipStream.Length > MaxPlainBytes)
                {
                    return Error.Rule("backup_too_large", "This organisation's data is too large for an in-app backup; ask the operator for a database backup.");
                }

                objectEntries.Add(new { key, file = "objects/" + key, sha256 = sha, bytes = content.Length });
            }

            var manifest = new
            {
                format = BackupCatalog.Format,
                organisationId = actor.TenantId,
                organisationName = organisation,
                createdAt = now,
                createdBy = actor.UserId,
                migrations = await MigrationsAsync(ct),
                tables = manifestTables,
                excluded = BackupCatalog.Excluded,
                objects = objectEntries,
                ledger = new { debits, credits, balanced = debits == credits },
                restore = "Restore into a fresh environment only (see docs/operations/BACKUP_RESTORE.md). Never import over live data.",
            };
            Write(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        }

        var encrypted = BackupArchive.Encrypt(zipStream.ToArray(), passphrase);
        var archiveSha = Sha(encrypted);
        await LogAsync("Created", archiveSha, encrypted.LongLength, manifestTables.Count, totalRows, debits == credits, $"{objectEntries.Count} files. Purpose: {purpose}", ct);
        await audit.RecordAsync(new AuditEntry("backup.create", "backup", Guid.CreateVersion7(), Reason: purpose, Metadata: new Dictionary<string, string>
        {
            ["sha256"] = archiveSha,
            ["rows"] = totalRows.ToString(CultureInfo.InvariantCulture),
            ["tables"] = manifestTables.Count.ToString(CultureInfo.InvariantCulture),
        }), ct);
        await session.CommitAsync(ct);
        return new BackupFile(encrypted, $"serk-backup-{now:yyyyMMdd-HHmm}.serkbak", archiveSha, manifestTables.Count, totalRows);
    }

    public async Task<Result<BackupVerification>> VerifyAsync(byte[] file, string? passphrase, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var problems = new List<string>();
        var plain = BackupArchive.TryDecrypt(file, passphrase ?? "", out var problem);
        BackupVerification result;
        if (plain is null)
        {
            result = new BackupVerification(false, null, null, null, false, [], 0, 0, null, [problem]);
        }
        else
        {
            result = Inspect(plain, actor.TenantId, problems);
        }

        await LogAsync(result.Valid ? "Verified" : "VerifyFailed", Sha(file), file.LongLength, result.Tables.Count, result.Tables.Sum(t => t.Rows), result.LedgerBalanced,
            string.Join("; ", result.Problems).Length > 480 ? string.Join("; ", result.Problems)[..480] : string.Join("; ", result.Problems), ct);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<BackupLogDto>> HistoryAsync(CancellationToken ct)
    {
        context.RequireActor();
        await using var cmd = await session.CommandAsync(
            "SELECT id, action, archive_sha256, size_bytes, table_count, row_count, ledger_balanced, detail, user_id, created_at FROM platform.backup_log ORDER BY created_at DESC LIMIT 50", ct);
        var list = new List<BackupLogDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new BackupLogDto(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetBoolean(6), r.GetString(7), r.GetGuid(8), r.GetFieldValue<DateTimeOffset>(9)));
        }

        return list;
    }

    /// <summary>Checks a decrypted archive: manifest present, every file's fingerprint and row count, ledger balance.</summary>
    public static BackupVerification Inspect(byte[] zipBytes, Guid expectedTenant, List<string> problems)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                return new BackupVerification(false, null, null, null, false, [], 0, 0, null, ["The backup has no manifest."]);
            }

            using var manifest = JsonDocument.Parse(ReadAll(manifestEntry, new ReadBudget(8 * 1024 * 1024)));
            var root = manifest.RootElement;
            var format = root.GetProperty("format").GetString();
            if (format != BackupCatalog.Format)
            {
                problems.Add($"Unknown backup format '{format}'.");
            }

            var sameOrg = root.GetProperty("organisationId").GetGuid() == expectedTenant;
            if (!sameOrg)
            {
                problems.Add("This backup belongs to a different organisation.");
            }

            var tables = new List<BackupTableReport>();
            bool? ledger = null;
            var budget = new ReadBudget(MaxPlainBytes);
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var tableArray = root.GetProperty("tables");
            if (tableArray.GetArrayLength() > BackupCatalog.Tables.Count * 2 || zip.Entries.Count > 100_000)
            {
                return new BackupVerification(false, format, null, null, sameOrg, [], 0, 0, null, ["The backup lists an implausible number of entries."]);
            }

            foreach (var t in tableArray.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString()!;
                var fileName = t.GetProperty("file").GetString()!;
                if (!seenFiles.Add(fileName))
                {
                    return new BackupVerification(false, format, null, null, sameOrg, [], 0, 0, null, ["The backup manifest refers to the same file twice."]);
                }

                var entry = zip.GetEntry(fileName);
                var bytes = entry is null ? null : ReadAll(entry, budget);
                var rows = t.GetProperty("rows").GetInt64();
                var ok = bytes is not null && Sha(bytes) == t.GetProperty("sha256").GetString() && CountLines(bytes) == rows;
                if (!ok)
                {
                    problems.Add($"Table {name} is missing or does not match its fingerprint.");
                }

                if (ok && name == "finance.journal_lines")
                {
                    var (d, c) = SumLedger(bytes!);
                    ledger = d == c && BalancedPerEntry(bytes!);
                    if (ledger == false)
                    {
                        problems.Add("The ledger in this backup does not balance.");
                    }
                }

                tables.Add(new BackupTableReport(name, rows, ok));
            }

            int objectCount = 0, objectsOk = 0;
            foreach (var o in root.GetProperty("objects").EnumerateArray())
            {
                objectCount++;
                var fileName = o.GetProperty("file").GetString()!;
                var entry = seenFiles.Add(fileName) ? zip.GetEntry(fileName) : null;
                if (entry is not null && Sha(ReadAll(entry, budget)) == o.GetProperty("sha256").GetString())
                {
                    objectsOk++;
                }
                else
                {
                    problems.Add($"Stored file {o.GetProperty("key").GetString()} is missing or altered.");
                }
            }

            var missingTables = BackupCatalog.Tables.Select(t => t.Name).Except(tables.Select(t => t.Name), StringComparer.Ordinal).ToList();
            if (missingTables.Count > 0)
            {
                problems.Add("Tables missing from this backup: " + string.Join(", ", missingTables) + ".");
            }

            return new BackupVerification(problems.Count == 0, format, root.GetProperty("createdAt").GetDateTimeOffset(),
                root.TryGetProperty("organisationName", out var on) ? on.GetString() : null, sameOrg, tables, objectCount, objectsOk, ledger, problems);
        }
        catch (BudgetExceededException)
        {
            problems.Add($"The backup expands to more than {MaxPlainBytes / (1024 * 1024)} MB and was not inspected.");
            return new BackupVerification(false, null, null, null, false, [], 0, 0, null, problems);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            problems.Add("The backup content is damaged.");
            return new BackupVerification(false, null, null, null, false, [], 0, 0, null, problems);
        }
    }

    private async Task<(byte[] Bytes, long Rows)> DumpAsync(BackupTable table, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = await session.CommandAsync(table.Sql, ct);
        cmd.Parameters.AddWithValue("t", tenantId);
        using var buffer = new MemoryStream();
        long rows = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var line = Encoding.UTF8.GetBytes(r.GetString(0));
                buffer.Write(line);
                buffer.WriteByte((byte)'\n');
                rows++;
            }
        }

        return (buffer.ToArray(), rows);
    }

    private async Task<List<(string Key, string Sha)>> ObjectKeysAsync(CancellationToken ct)
    {
        await using var cmd = await session.CommandAsync(
            """
            SELECT object_key, sha256 FROM configuration.branding_assets
            UNION ALL
            SELECT pdf_object_key, pdf_sha256 FROM documents.document_versions WHERE pdf_object_key IS NOT NULL AND pdf_sha256 IS NOT NULL
            ORDER BY 1
            """, ct);
        var list = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add((r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    private async Task<string[]> MigrationsAsync(CancellationToken ct)
    {
        await using var cmd = await session.CommandAsync("SELECT coalesce(array_agg(script ORDER BY script), '{}') FROM platform.v_applied_migrations", ct);
        return (string[])(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task LogAsync(string action, string? sha, long size, int tables, long rows, bool? ledger, string detail, CancellationToken ct)
    {
        var actor = context.RequireActor();
        await using var cmd = await session.CommandAsync(
            """
            INSERT INTO platform.backup_log (id, tenant_id, user_id, action, archive_sha256, size_bytes, table_count, row_count, ledger_balanced, detail, created_at, correlation_id)
            VALUES (@id, @t, @u, @a, @sha, @size, @tables, @rows, @ledger, @detail, @at, @corr)
            """, ct);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("t", actor.TenantId);
        cmd.Parameters.AddWithValue("u", actor.UserId);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("sha", (object?)sha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("size", size);
        cmd.Parameters.AddWithValue("tables", tables);
        cmd.Parameters.AddWithValue("rows", rows);
        cmd.Parameters.AddWithValue("ledger", (object?)ledger ?? DBNull.Value);
        cmd.Parameters.AddWithValue("detail", detail);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("corr", context.CorrelationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes);
    }

    /// <summary>Reads an entry while counting real decompressed bytes against a shared budget (zip-bomb guard).</summary>
    private static byte[] ReadAll(ZipArchiveEntry entry, ReadBudget budget)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            budget.Spend(n);
            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }

    private sealed class BudgetExceededException : Exception;

    private sealed class ReadBudget(long bytes)
    {
        private long _left = bytes;

        public void Spend(int n)
        {
            _left -= n;
            if (_left < 0)
            {
                throw new BudgetExceededException();
            }
        }
    }

    private static long CountLines(byte[] bytes) => bytes.LongCount(b => b == (byte)'\n');

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static IEnumerable<JsonElement> Lines(byte[] bytes)
    {
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    private static (decimal Debits, decimal Credits) SumLedger(byte[] bytes)
    {
        decimal d = 0, c = 0;
        foreach (var e in Lines(bytes))
        {
            d += e.GetProperty("debit").GetDecimal();
            c += e.GetProperty("credit").GetDecimal();
        }

        return (d, c);
    }

    private static bool BalancedPerEntry(byte[] bytes) =>
        Lines(bytes).GroupBy(e => e.GetProperty("entry_id").GetGuid())
            .All(g => g.Sum(e => e.GetProperty("debit").GetDecimal()) == g.Sum(e => e.GetProperty("credit").GetDecimal()));

    private static string ReadString(byte[] bytes, string property) =>
        Lines(bytes).Select(e => e.TryGetProperty(property, out var v) ? v.GetString() ?? "" : "").FirstOrDefault() ?? "";
}
