using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Platform.Audit;

/// <summary>
/// Audit entry. NEVER put clinical free text or patient names in <see cref="Metadata"/>; identifiers and codes only.
/// </summary>
public sealed record AuditEntry(
    string Action,
    string ResourceType,
    Guid ResourceId,
    Guid? PatientId = null,
    string Outcome = "success",
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Append-only audit trail. Successful actions are written inside the business transaction (they commit or roll back
/// together). Denials are written on a separate connection to <c>audit.security_events</c> so they survive rollback.
/// The database role used by the application has INSERT/SELECT only and a trigger rejects UPDATE/DELETE.
/// </summary>
public sealed class AuditTrail(DbSession session, RequestContext context, NpgsqlDataSource dataSource, TimeProvider clock)
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var actor = context.RequireActor();
        await using var cmd = await session.CommandAsync(
            """
            INSERT INTO audit.audit_events
              (id, tenant_id, facility_id, actor_user_id, actor_session_id, action, resource_type, resource_id, patient_id,
               outcome, reason, correlation_id, occurred_at, metadata)
            VALUES (@id, @tenant, @facility, @actor, @session, @action, @rtype, @rid, @patient, @outcome, @reason, @corr, @at, @meta)
            """,
            ct);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("tenant", actor.TenantId);
        cmd.Parameters.AddWithValue("facility", actor.FacilityId);
        cmd.Parameters.AddWithValue("actor", actor.UserId);
        cmd.Parameters.AddWithValue("session", actor.SessionId);
        cmd.Parameters.AddWithValue("action", entry.Action);
        cmd.Parameters.AddWithValue("rtype", entry.ResourceType);
        cmd.Parameters.AddWithValue("rid", entry.ResourceId);
        cmd.Parameters.AddWithValue("patient", (object?)entry.PatientId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("outcome", entry.Outcome);
        cmd.Parameters.AddWithValue("reason", (object?)entry.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("corr", context.CorrelationId);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        cmd.Parameters.Add(new NpgsqlParameter("meta", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(entry.Metadata ?? new Dictionary<string, string>()) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Writes an audit event on its own connection/transaction so it persists even when the business transaction is not
    /// committed (e.g. duplicate candidates disclosed during a registration that did not complete).
    /// </summary>
    public async Task RecordIndependentAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var actor = context.RequireActor();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", actor.TenantId.ToString());
            await set.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_events
              (id, tenant_id, facility_id, actor_user_id, actor_session_id, action, resource_type, resource_id, patient_id,
               outcome, reason, correlation_id, occurred_at, metadata)
            VALUES (@id, @tenant, @facility, @actor, @session, @action, @rtype, @rid, @patient, @outcome, @reason, @corr, @at, @meta)
            """,
            conn,
            tx))
        {
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenant", actor.TenantId);
            cmd.Parameters.AddWithValue("facility", actor.FacilityId);
            cmd.Parameters.AddWithValue("actor", actor.UserId);
            cmd.Parameters.AddWithValue("session", actor.SessionId);
            cmd.Parameters.AddWithValue("action", entry.Action);
            cmd.Parameters.AddWithValue("rtype", entry.ResourceType);
            cmd.Parameters.AddWithValue("rid", entry.ResourceId);
            cmd.Parameters.AddWithValue("patient", (object?)entry.PatientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("outcome", entry.Outcome);
            cmd.Parameters.AddWithValue("reason", (object?)entry.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("corr", context.CorrelationId);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.Add(new NpgsqlParameter("meta", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(entry.Metadata ?? new Dictionary<string, string>()) });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Security event (login failure, authorization denial). Survives rollback of the business transaction.</summary>
    public Task RecordSecurityEventAsync(string eventType, string outcome, string? resourceType = null, Guid? resourceId = null, CancellationToken ct = default) =>
        WriteSecurityEventAsync(dataSource, clock, eventType, outcome, context.Actor?.UserId, context.Actor?.TenantId, resourceType, resourceId, context.CorrelationId, null, ct);

    public static async Task WriteSecurityEventAsync(
        NpgsqlDataSource dataSource,
        TimeProvider clock,
        string eventType,
        string outcome,
        Guid? userId,
        Guid? tenantId,
        string? resourceType,
        Guid? resourceId,
        string correlationId,
        string? subjectHint,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit.security_events (id, event_type, outcome, user_id, tenant_id, resource_type, resource_id, correlation_id, subject_hint, occurred_at)
            VALUES (@id, @type, @outcome, @user, @tenant, @rtype, @rid, @corr, @hint, @at)
            """,
            conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("type", eventType);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rtype", (object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rid", (object?)resourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("corr", correlationId);
        cmd.Parameters.AddWithValue("hint", (object?)subjectHint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
