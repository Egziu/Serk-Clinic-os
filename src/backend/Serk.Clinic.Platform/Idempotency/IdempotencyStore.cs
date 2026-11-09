using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Idempotency;

/// <summary>
/// Durable idempotency for critical commands (sign, finalize, register, book). The key row is inserted inside the
/// business transaction; a concurrent duplicate blocks on the unique index until the first commits, then replays its
/// stored result. Reusing a key with a different request fingerprint is rejected.
/// </summary>
public sealed class IdempotencyStore(DbSession session, RequestContext context, TimeProvider clock)
{
    public const int MaxKeyLength = 100;

    public async Task<Result<T>> ExecuteAsync<T>(
        string scope,
        string? key,
        object requestFingerprint,
        Func<CancellationToken, Task<Result<T>>> action,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Error.Validation("idempotency_key_required", "An Idempotency-Key header is required for this command.");
        }

        if (key.Length > MaxKeyLength)
        {
            return Error.Validation("idempotency_key_invalid", "Idempotency-Key is too long.");
        }

        var actor = context.RequireActor();
        var requestHash = CanonicalJson.Sha256Hex(CanonicalJson.Serialize(requestFingerprint));

        await using (var insert = await session.CommandAsync(
            """
            INSERT INTO platform.idempotency_records (tenant_id, scope, idempotency_key, request_hash, actor_user_id, status, created_at)
            VALUES (@tenant, @scope, @key, @hash, @actor, 'InProgress', @now)
            ON CONFLICT (tenant_id, scope, idempotency_key) DO NOTHING
            """,
            ct))
        {
            insert.Parameters.AddWithValue("tenant", actor.TenantId);
            insert.Parameters.AddWithValue("scope", scope);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("hash", requestHash);
            insert.Parameters.AddWithValue("actor", actor.UserId);
            insert.Parameters.AddWithValue("now", clock.GetUtcNow());
            var inserted = await insert.ExecuteNonQueryAsync(ct);
            if (inserted == 0)
            {
                return await ReplayAsync<T>(actor.TenantId, scope, key, requestHash, ct);
            }
        }

        var result = await action(ct);
        if (!result.IsSuccess)
        {
            // Business failure: the caller does not commit, so the key row is rolled back and the key may be retried.
            return result;
        }

        await using var complete = await session.CommandAsync(
            "UPDATE platform.idempotency_records SET status = 'Completed', response_json = @json::jsonb WHERE tenant_id = @tenant AND scope = @scope AND idempotency_key = @key",
            ct);
        complete.Parameters.AddWithValue("json", CanonicalJson.Serialize(result.Value));
        complete.Parameters.AddWithValue("tenant", actor.TenantId);
        complete.Parameters.AddWithValue("scope", scope);
        complete.Parameters.AddWithValue("key", key);
        await complete.ExecuteNonQueryAsync(ct);
        return result;
    }

    private async Task<Result<T>> ReplayAsync<T>(Guid tenant, string scope, string key, string requestHash, CancellationToken ct)
    {
        await using var select = await session.CommandAsync(
            "SELECT request_hash, status, response_json::text FROM platform.idempotency_records WHERE tenant_id = @tenant AND scope = @scope AND idempotency_key = @key",
            ct);
        select.Parameters.AddWithValue("tenant", tenant);
        select.Parameters.AddWithValue("scope", scope);
        select.Parameters.AddWithValue("key", key);
        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Error.Conflict("idempotency_conflict", "The idempotent request could not be resolved; retry.");
        }

        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
        {
            return Error.Validation("idempotency_key_reused", "This Idempotency-Key was already used with a different request.");
        }

        if (reader.GetString(1) != "Completed" || reader.IsDBNull(2))
        {
            return Error.Conflict("idempotency_in_progress", "A request with this Idempotency-Key is still in progress.");
        }

        return CanonicalJson.Deserialize<T>(reader.GetString(2));
    }
}
