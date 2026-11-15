namespace Serk.Clinic.Platform.Backup;

/// <summary>One table in the organisation backup and the query that selects exactly this organisation's rows.</summary>
public sealed record BackupTable(string Name, string Sql);

/// <summary>
/// What an organisation backup contains. Every table holding organisation data must be listed here or in
/// <see cref="Excluded"/> with a reason; an integration test fails when a new table is in neither list, so data can never
/// be silently left out of backups. Tables under row-level security are read with the tenant set on the session; the
/// identity tables (no RLS) are filtered explicitly by <c>@t</c>. Password hashes and session tokens are never exported.
/// </summary>
public static class BackupCatalog
{
    public const string Format = "serk-org-backup/1";

    private static BackupTable Rls(string name) => new(name, $"SELECT row_to_json(x)::text FROM {name} x WHERE x.tenant_id = @t");

    public static readonly IReadOnlyList<BackupTable> Tables =
    [
        new("identity.tenants", "SELECT row_to_json(x)::text FROM identity.tenants x WHERE x.id = @t"),
        new("identity.facilities", "SELECT row_to_json(x)::text FROM identity.facilities x WHERE x.tenant_id = @t"),
        new("identity.staff_users", """
            SELECT row_to_json(x)::text FROM (
              SELECT u.id, u.username, u.display_name, u.is_active, u.last_login_at, u.created_at, u.must_change_password
              FROM identity.staff_users u WHERE u.id IN (SELECT user_id FROM identity.affiliations WHERE tenant_id = @t)) x
            """),
        new("identity.practitioners", "SELECT row_to_json(x)::text FROM identity.practitioners x WHERE x.tenant_id = @t"),
        new("identity.affiliations", "SELECT row_to_json(x)::text FROM identity.affiliations x WHERE x.tenant_id = @t"),
        new("identity.role_definitions", "SELECT row_to_json(x)::text FROM identity.role_definitions x WHERE x.tenant_id = @t"),
        new("identity.admin_changes", "SELECT row_to_json(x)::text FROM identity.admin_changes x WHERE x.tenant_id = @t"),
        Rls("patients.patients"), Rls("patients.allergies"), Rls("patients.mrn_counters"),
        Rls("scheduling.appointments"),
        Rls("clinical.encounters"), Rls("clinical.note_drafts"), Rls("clinical.note_versions"),
        Rls("dental.findings"),
        Rls("documents.document_versions"),
        Rls("configuration.facility_settings"), Rls("configuration.facility_profiles"), Rls("configuration.branding_assets"), Rls("configuration.printer_profiles"),
        Rls("finance.accounts"), Rls("finance.posting_rules"), Rls("finance.services"), Rls("finance.document_sequences"),
        Rls("finance.invoices"), Rls("finance.invoice_lines"), Rls("finance.cashier_sessions"), Rls("finance.settlements"),
        Rls("finance.payments"), Rls("finance.refunds"), Rls("finance.journal_entries"), Rls("finance.journal_lines"),
        Rls("finance.daily_closes"), Rls("finance.close_checks"), new("finance.owner_subscriptions", """
            SELECT row_to_json(x)::text FROM (
              SELECT id, tenant_id, facility_id, user_id, channel, address, status, created_at, verified_at, revoked_at
              FROM finance.owner_subscriptions WHERE tenant_id = @t) x
            """), // verification challenge hashes are short-lived secrets and are never exported
        Rls("finance.delivery_attempts"),
        Rls("care.contact_preferences"), Rls("care.consent_events"), Rls("care.message_templates"), Rls("care.messages"), Rls("care.replies"),
        Rls("care.tasks"), Rls("care.health_tips"), Rls("care.campaigns"), Rls("care.social_posts"), Rls("care.social_metrics"),
        Rls("audit.audit_events"),
        Rls("platform.export_log"), Rls("platform.backup_log"),
    ];

    /// <summary>Tables deliberately not in the organisation backup, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["platform.idempotency_records"] = "Short-lived replay cache of API responses; rebuilt automatically.",
        ["platform.outbox_messages"] = "Delivery queue; processed messages are reflected in the module tables.",
        ["platform.schema_migrations"] = "Database structure, not organisation data (the backup records the applied list).",
        ["audit.security_events"] = "Security telemetry kept by the platform operator (may relate to unauthenticated requests).",
        ["identity.sessions"] = "Session tokens are secrets and expire within hours.",
        ["care.telegram_links"] = "One-time Telegram link codes (hashed, 30-minute lifetime); patients can be re-linked.",
        ["care.telegram_chats"] = "Telegram chat bindings used only by the bot webhook; the chat id is also kept in care.contact_preferences.",
        ["identity.platform_admins"] = "Platform-wide super administrators, not part of any one organisation.",
    };
}
