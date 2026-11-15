using Npgsql;
using Serk.Clinic.Care.Domain;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Application;

public sealed record AutomationSettingsDto(
    bool ConfirmationsEnabled, bool ReminderEnabled, int ReminderLeadHours, bool SameDayReminderEnabled, int SameDayLeadHours,
    bool FollowUpEnabled, int FollowUpDelayHours, bool RebookEnabled, string QuietStart, string QuietEnd,
    int RecallInviteLeadDays, int RecallOverdueDays, bool AutoCheckupRecallEnabled, int AutoCheckupMonths, bool StaffNoticesEnabled,
    bool IsDefault, DateTimeOffset? UpdatedAt, int Version);

public sealed record RecallDto(Guid Id, Guid PatientId, string Mrn, string PatientName, string Phone, string Kind, string Note, DateOnly DueOn, string Status, string Source,
    bool Overdue, DateTimeOffset? InvitedAt, DateTimeOffset? EscalatedAt, DateTimeOffset CreatedAt, string? CloseReason, int Version);

public sealed record CreateRecallRequest(Guid PatientId, string? Kind, DateOnly? DueOn, int? InWeeks, int? InMonths, string? Note);

public sealed record CloseRecallRequest(string? Status, string? Reason);

/// <summary>Loads automation settings (defaults when a facility never changed them).</summary>
internal static class AutomationStore
{
    private const string Columns =
        "confirmations_enabled, reminder_enabled, reminder_lead_hours, same_day_reminder_enabled, same_day_lead_hours, follow_up_enabled, follow_up_delay_hours, rebook_enabled, quiet_start, quiet_end, recall_invite_lead_days, recall_overdue_days, auto_checkup_recall_enabled, auto_checkup_months, staff_notices_enabled";

    public static async Task<Dictionary<Guid, AutomationSettings>> ForTenantAsync(DbSession session, CancellationToken ct) =>
        (await session.QueryAsync($"SELECT facility_id, {Columns} FROM care.automation_settings", r => (r.GetGuid(0), Read(r, 1)), ct)).ToDictionary(x => x.Item1, x => x.Item2);

    public static async Task<(AutomationSettings Settings, int Version, DateTimeOffset? UpdatedAt)> ForFacilityAsync(DbSession session, Guid facility, CancellationToken ct)
    {
        var rows = await session.QueryAsync($"SELECT {Columns}, version, updated_at FROM care.automation_settings WHERE facility_id = @f",
            r => (Read(r, 0), r.GetInt32(15), (DateTimeOffset?)r.GetFieldValue<DateTimeOffset>(16)), ct, ("f", facility));
        return rows.Count == 1 ? rows[0] : (AutomationSettings.Default, 0, null);
    }

    private static AutomationSettings Read(NpgsqlDataReader r, int i) => new(
        r.GetBoolean(i), r.GetBoolean(i + 1), r.GetInt32(i + 2), r.GetBoolean(i + 3), r.GetInt32(i + 4), r.GetBoolean(i + 5), r.GetInt32(i + 6), r.GetBoolean(i + 7),
        TimeOnly.FromTimeSpan(r.GetFieldValue<TimeSpan>(i + 8)), TimeOnly.FromTimeSpan(r.GetFieldValue<TimeSpan>(i + 9)), r.GetInt32(i + 10), r.GetInt32(i + 11), r.GetBoolean(i + 12),
        r.GetInt32(i + 13), r.GetBoolean(i + 14));
}

/// <summary>Automation settings and dental recalls ("see this patient again by …").</summary>
public sealed class RecallService(DbSession session, RequestContext context, AuditTrail audit, TimeProvider clock)
{
    public async Task<AutomationSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var (s, version, at) = await AutomationStore.ForFacilityAsync(session, actor.FacilityId, ct);
        await session.CommitAsync(ct);
        return ToDto(s, version, at);
    }

    public async Task<Result<AutomationSettingsDto>> UpdateSettingsAsync(AutomationSettingsDto body, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (!TimeOnly.TryParse(body.QuietStart, System.Globalization.CultureInfo.InvariantCulture, out var qs) || !TimeOnly.TryParse(body.QuietEnd, System.Globalization.CultureInfo.InvariantCulture, out var qe))
        {
            return Error.Validation("invalid_automation", "Quiet hours must be times like 20:00.", new Dictionary<string, string[]> { ["quietStart"] = ["HH:mm"] });
        }

        var s = new AutomationSettings(body.ConfirmationsEnabled, body.ReminderEnabled, body.ReminderLeadHours, body.SameDayReminderEnabled, body.SameDayLeadHours,
            body.FollowUpEnabled, body.FollowUpDelayHours, body.RebookEnabled, qs, qe, body.RecallInviteLeadDays, body.RecallOverdueDays, body.AutoCheckupRecallEnabled,
            body.AutoCheckupMonths, body.StaffNoticesEnabled).Validate();
        if (!s.IsSuccess)
        {
            return s.Error!;
        }

        var (_, version, _) = await AutomationStore.ForFacilityAsync(session, actor.FacilityId, ct);
        if (ifMatch is null || ifMatch != version)
        {
            return Error.Precondition("settings_changed", "Automation settings changed meanwhile (or If-Match missing). Reload and retry.");
        }

        var v = s.Value;
        var now = clock.GetUtcNow();
        var n = await session.ExecAsync(
            """
            INSERT INTO care.automation_settings (facility_id, tenant_id, confirmations_enabled, reminder_enabled, reminder_lead_hours, same_day_reminder_enabled, same_day_lead_hours,
              follow_up_enabled, follow_up_delay_hours, rebook_enabled, quiet_start, quiet_end, recall_invite_lead_days, recall_overdue_days, auto_checkup_recall_enabled,
              auto_checkup_months, staff_notices_enabled, updated_by, updated_at, version)
            VALUES (@f, @t, @a, @b, @c, @d, @e, @g, @h, @i, @qs, @qe, @j, @k, @l, @m, @n, @u, @now, 1)
            ON CONFLICT (facility_id) DO UPDATE SET confirmations_enabled = EXCLUDED.confirmations_enabled, reminder_enabled = EXCLUDED.reminder_enabled,
              reminder_lead_hours = EXCLUDED.reminder_lead_hours, same_day_reminder_enabled = EXCLUDED.same_day_reminder_enabled, same_day_lead_hours = EXCLUDED.same_day_lead_hours,
              follow_up_enabled = EXCLUDED.follow_up_enabled, follow_up_delay_hours = EXCLUDED.follow_up_delay_hours, rebook_enabled = EXCLUDED.rebook_enabled,
              quiet_start = EXCLUDED.quiet_start, quiet_end = EXCLUDED.quiet_end, recall_invite_lead_days = EXCLUDED.recall_invite_lead_days,
              recall_overdue_days = EXCLUDED.recall_overdue_days, auto_checkup_recall_enabled = EXCLUDED.auto_checkup_recall_enabled, auto_checkup_months = EXCLUDED.auto_checkup_months,
              staff_notices_enabled = EXCLUDED.staff_notices_enabled, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at, version = care.automation_settings.version + 1
            WHERE care.automation_settings.version = @v
            """, ct, ("f", actor.FacilityId), ("t", actor.TenantId), ("a", v.ConfirmationsEnabled), ("b", v.ReminderEnabled), ("c", v.ReminderLeadHours), ("d", v.SameDayReminderEnabled),
            ("e", v.SameDayLeadHours), ("g", v.FollowUpEnabled), ("h", v.FollowUpDelayHours), ("i", v.RebookEnabled), ("qs", v.QuietStart.ToTimeSpan()), ("qe", v.QuietEnd.ToTimeSpan()),
            ("j", v.RecallInviteLeadDays), ("k", v.RecallOverdueDays), ("l", v.AutoCheckupRecallEnabled), ("m", v.AutoCheckupMonths), ("n", v.StaffNoticesEnabled),
            ("u", actor.UserId), ("now", now), ("v", version));
        if (n == 0)
        {
            return Error.Precondition("settings_changed", "Automation settings changed meanwhile. Reload and retry.");
        }

        await audit.RecordAsync(new AuditEntry("care.automation.update", "automation_settings", actor.FacilityId, Metadata: new Dictionary<string, string>
        {
            ["reminderLeadHours"] = v.ReminderLeadHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sameDay"] = v.SameDayReminderEnabled.ToString(),
            ["autoRecall"] = v.AutoCheckupRecallEnabled.ToString(),
        }), ct);
        var (saved, newVersion, at) = await AutomationStore.ForFacilityAsync(session, actor.FacilityId, ct);
        await session.CommitAsync(ct);
        return ToDto(saved, newVersion, at);
    }

    public async Task<IReadOnlyList<RecallDto>> ListAsync(string? filter, Guid? patientId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var (s, _, _) = await AutomationStore.ForFacilityAsync(session, actor.FacilityId, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var list = await session.QueryAsync(
            """
            SELECT r.id, r.patient_id, coalesce(p.mrn, ''), coalesce(p.display_name, ''), coalesce(nullif(c.sms_phone, ''), p.phone, ''), r.kind, r.note, r.due_on, r.status, r.source,
                   r.invited_at, r.escalated_at, r.created_at, r.close_reason, r.version
            FROM care.recalls r
            LEFT JOIN patients.v_patient_contact p ON p.patient_id = r.patient_id
            LEFT JOIN care.contact_preferences c ON c.patient_id = r.patient_id
            WHERE r.facility_id = @f AND (@p::uuid IS NULL OR r.patient_id = @p)
              AND (@filter = 'all' OR (@filter = 'open' AND r.status = 'Due') OR (@filter = 'booked' AND r.status = 'Booked')
                   OR (@filter = 'upcoming' AND r.status = 'Due' AND r.due_on <= @soon))
            ORDER BY r.due_on LIMIT 500
            """,
            r => new RecallDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetFieldValue<DateOnly>(7), r.GetString(8),
                r.GetString(9), r.GetString(8) == "Due" && RecallRules.Overdue(today, r.GetFieldValue<DateOnly>(7), 0), r.Time(10), r.Time(11), r.GetFieldValue<DateTimeOffset>(12),
                r.Str(13), r.GetInt32(14)),
            ct, ("f", actor.FacilityId), ("p", patientId), ("filter", filter ?? "open"), ("soon", today.AddDays(s.RecallInviteLeadDays + 30)));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<Guid>> CreateAsync(CreateRecallRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var kind = r.Kind ?? "CheckUp";
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var due = r.DueOn ?? (r.InWeeks is int w ? today.AddDays(7 * w) : r.InMonths is int m ? today.AddMonths(m) : (DateOnly?)null);
        var note = (r.Note ?? "").Trim();
        if (!RecallRules.Kinds.Contains(kind) || due is null || due < today || due > today.AddYears(3) || note.Length > 200)
        {
            return Error.Validation("invalid_recall", "Choose a type and a due date between today and 3 years ahead (note up to 200 characters).",
                new Dictionary<string, string[]> { ["dueOn"] = ["Today to 3 years ahead."] });
        }

        if ((await session.QueryAsync("SELECT 1 FROM patients.v_patient_contact WHERE patient_id = @p", _ => 1, ct, ("p", r.PatientId))).Count == 0)
        {
            return Error.NotFound();
        }

        var id = Guid.CreateVersion7();
        var source = actor.PractitionerId is not null ? "Clinician" : "Reception";
        await session.ExecAsync(
            """
            INSERT INTO care.recalls (id, tenant_id, facility_id, patient_id, kind, note, due_on, status, source, created_by, created_at)
            VALUES (@id, @t, @f, @p, @k, @n, @d, 'Due', @s, @u, @now)
            """, ct, ("id", id), ("t", actor.TenantId), ("f", actor.FacilityId), ("p", r.PatientId), ("k", kind), ("n", note), ("d", due.Value), ("s", source),
            ("u", actor.UserId), ("now", clock.GetUtcNow()));
        await audit.RecordAsync(new AuditEntry("care.recall.create", "recall", id, r.PatientId, Metadata: new Dictionary<string, string> { ["kind"] = kind, ["dueOn"] = due.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }), ct);
        await session.CommitAsync(ct);
        return id;
    }

    public async Task<Result<bool>> CloseAsync(Guid id, CloseRecallRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var status = r.Status ?? "Cancelled";
        var reason = r.Reason?.Trim();
        if (status is not ("Done" or "Cancelled") || reason is null || reason.Length is < 3 or > 200)
        {
            return Error.Validation("reason_required", "Choose Done or Cancelled and give a short reason (3–200 characters).");
        }

        var patient = await session.ScalarAsync<Guid?>(
            """
            UPDATE care.recalls SET status = @s, close_reason = @r, closed_by = @u, closed_at = @now, version = version + 1
            WHERE id = @id AND facility_id = @f AND status IN ('Due', 'Booked') RETURNING patient_id
            """, ct, ("id", id), ("f", actor.FacilityId), ("s", status), ("r", reason), ("u", actor.UserId), ("now", clock.GetUtcNow()));
        if (patient is null)
        {
            var exists = await session.ScalarAsync<long>("SELECT count(*) FROM care.recalls WHERE id = @id AND facility_id = @f", ct, ("id", id), ("f", actor.FacilityId));
            return exists == 0 ? Error.NotFound() : Error.Conflict("recall_closed", "This recall is already closed.");
        }

        await session.ExecAsync("UPDATE care.messages SET status = 'Cancelled', last_error = 'Recall closed' WHERE kind = 'Recall' AND source_id = @id AND status = 'Scheduled'", ct, ("id", id));
        await audit.RecordAsync(new AuditEntry("care.recall.close", "recall", id, patient, Reason: reason), ct);
        await session.CommitAsync(ct);
        return true;
    }

    private static AutomationSettingsDto ToDto(AutomationSettings s, int version, DateTimeOffset? at) => new(
        s.ConfirmationsEnabled, s.ReminderEnabled, s.ReminderLeadHours, s.SameDayReminderEnabled, s.SameDayLeadHours, s.FollowUpEnabled, s.FollowUpDelayHours, s.RebookEnabled,
        s.QuietStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), s.QuietEnd.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        s.RecallInviteLeadDays, s.RecallOverdueDays, s.AutoCheckupRecallEnabled, s.AutoCheckupMonths, s.StaffNoticesEnabled, version == 0, at, version);
}
