using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Serk.Clinic.Platform.Migrations;

/// <summary>
/// Applies ordered, module-owned SQL migration scripts (<c>NNNN_name.sql</c>) with the schema-owner role.
/// Each script runs in its own transaction; applied checksums are verified so an edited historical migration fails fast.
/// A PostgreSQL advisory lock prevents concurrent migrators. Scripts must be additive; destructive changes need approval.
/// </summary>
public sealed class DatabaseMigrator(ILogger<DatabaseMigrator> logger)
{
    private const long AdvisoryLockKey = 0x5345524B_4D494752; // "SERKMIGR"

    public async Task<IReadOnlyList<string>> MigrateAsync(string ownerConnectionString, string scriptsDirectory, CancellationToken ct = default)
    {
        var scripts = Directory.GetFiles(scriptsDirectory, "*.sql")
            .Select(p => new { Path = p, Name = Path.GetFileName(p) })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No migration scripts found.");
        }

        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await ExecAsync(conn, null,
                """
                CREATE SCHEMA IF NOT EXISTS platform;
                CREATE TABLE IF NOT EXISTS platform.schema_migrations (
                  script_name text PRIMARY KEY,
                  checksum_sha256 text NOT NULL,
                  applied_at timestamptz NOT NULL DEFAULT now());
                """,
                ct);

            var applied = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT script_name, checksum_sha256 FROM platform.schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    applied[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var newlyApplied = new List<string>();
            foreach (var script in scripts)
            {
                var sql = await File.ReadAllTextAsync(script.Path, ct);
                var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));
                if (applied.TryGetValue(script.Name, out var existing))
                {
                    if (!string.Equals(existing, checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Migration {script.Name} was modified after being applied. Create a new migration instead.");
                    }

                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await ExecAsync(conn, tx, sql, ct);
                await using (var record = new NpgsqlCommand("INSERT INTO platform.schema_migrations (script_name, checksum_sha256) VALUES (@n, @c)", conn, tx))
                {
                    record.Parameters.AddWithValue("n", script.Name);
                    record.Parameters.AddWithValue("c", checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                newlyApplied.Add(script.Name);
                logger.LogInformation("Applied migration {Script}.", script.Name);
            }

            return newlyApplied;
        }
        finally
        {
            await ExecAsync(conn, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
