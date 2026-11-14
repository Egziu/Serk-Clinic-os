using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Export;

/// <summary>Accountability datasets: the audit trail, the export log and the backup log.</summary>
public static class PlatformExports
{
    public static readonly ExportDefinition AuditEvents = new(
        "audit.events", "Accountability", "Audit trail",
        "Who did what and when in this facility. Rows reference patients by internal id and may carry reasons, so it is treated as patient data.",
        [Capabilities.AuditRead], true, true,
        ["occurred_at", "user", "action", "resource_type", "resource_id", "patient_ref", "outcome", "reason", "correlation_id"],
        """
        SELECT e.occurred_at, coalesce(s.display_name, e.actor_user_id::text), e.action, e.resource_type, e.resource_id, e.patient_id,
               e.outcome, e.reason, e.correlation_id
        FROM audit.audit_events e
        LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = e.actor_user_id
        WHERE e.tenant_id = @t AND e.facility_id = @f AND e.occurred_at >= @from_utc AND e.occurred_at < @to_utc
        ORDER BY e.occurred_at LIMIT @limit
        """);

    public static readonly ExportDefinition ExportLog = new(
        "platform.exports", "Accountability", "Export log",
        "Every export: who, which dataset, range, stated purpose, rows and file fingerprint.",
        [Capabilities.AuditRead], false, true,
        ["created_at", "user", "dataset", "format", "range_from", "range_to", "patient_data", "purpose", "rows", "sha256"],
        """
        SELECT x.created_at, coalesce(s.display_name, x.user_id::text), x.dataset, x.format, x.range_from, x.range_to, x.contains_patient_data,
               x.purpose, x.row_count, x.sha256
        FROM platform.export_log x
        LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = x.user_id
        WHERE x.tenant_id = @t AND x.facility_id = @f AND x.created_at >= @from_utc AND x.created_at < @to_utc
        ORDER BY x.created_at LIMIT @limit
        """);

    public static readonly ExportDefinition BackupLog = new(
        "platform.backups", "Accountability", "Backup log",
        "Backups created and verified: who, when, size, rows and fingerprint.",
        [Capabilities.AuditRead], false, true,
        ["created_at", "user", "action", "archive_sha256", "size_bytes", "tables", "rows", "ledger_balanced", "detail"],
        """
        SELECT b.created_at, coalesce(s.display_name, b.user_id::text), b.action, b.archive_sha256, b.size_bytes, b.table_count, b.row_count,
               b.ledger_balanced, b.detail
        FROM platform.backup_log b
        LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = b.user_id
        WHERE b.tenant_id = @t AND b.created_at >= @from_utc AND b.created_at < @to_utc
        ORDER BY b.created_at LIMIT @limit
        """);

    public static ExportDefinition[] All => [AuditEvents, ExportLog, BackupLog];
}
