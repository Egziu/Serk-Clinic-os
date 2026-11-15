using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Serk.Clinic.Care.Domain;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Application;

public sealed record AutomationRun(int Planned, int Sent, int Failed, int Skipped);

/// <summary>
/// Plans and sends patient messages, tenant by tenant under row-level security:
/// confirmation on booking, reminder 24 h before, follow-up 24 h after the visit, re-booking invitation after a no-show,
/// and queued health-tip campaigns. Only for patients whose consent for that purpose is recorded; never 20:00–08:00
/// (facility time). Exactly one message per purpose, appointment/campaign and patient (unique key). Consent is re-checked
/// immediately before sending. Delivery is at-least-once: a provider timeout after acceptance can cause a repeat.
/// </summary>
public sealed class CareAutomation(IServiceScopeFactory scopes, CareChannels channels, TimeProvider clock, ILogger<CareAutomation> logger)
{
    public static readonly Guid AutomationUserId = Guid.Parse("00000000-0000-7000-8000-00000000ca7e");
    public const int MaxAttempts = 3;

    public async Task<AutomationRun> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IStaffDirectory>().ListTenantIdsAsync(ct);
        }

        var total = new AutomationRun(0, 0, 0, 0);
        foreach (var tenant in tenants)
        {
            try
            {
                // Recalls and staff notices run in their own transactions: a fault there never blocks appointment messages.
                var recalls = await GuardedAsync(() => InTenantAsync(tenant, RecallStepAsync, ct), "recalls");
                await GuardedAsync(() => InTenantAsync(tenant, NoticeStepAsync, ct), "staff notices");
                var planned = await InTenantAsync(tenant, PlanAsync, ct);
                var (sent, failed, skipped) = await InTenantAsync(tenant, DispatchAsync, ct);
                total = new AutomationRun(total.Planned + planned + recalls, total.Sent + sent, total.Failed + failed, total.Skipped + skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Care+ automation failed for one organisation; continuing with the others.");
            }
        }

        return total;
    }

    private async Task<int> GuardedAsync(Func<Task<int>> step, string name)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Care+ automation step '{Step}' failed for one organisation; other steps continue.", name);
            return 0;
        }
    }

    private async Task<int> RecallStepAsync(DbSession session, Guid tenant, CancellationToken ct)
    {
        var settings = await AutomationStore.ForTenantAsync(session, ct);
        var templates = (await session.QueryAsync("SELECT kind, language, body, is_enabled FROM care.message_templates",
            r => (Kind: r.GetString(0), Lang: r.GetString(1), Body: r.GetString(2), Enabled: r.GetBoolean(3)), ct)).ToDictionary(x => (x.Kind, x.Lang));
        return await ProcessRecallsAsync(session, tenant, clock.GetUtcNow(), settings, templates, ct);
    }

    private async Task<int> NoticeStepAsync(DbSession session, Guid tenant, CancellationToken ct)
    {
        await PostSystemNoticesAsync(session, tenant, clock.GetUtcNow(), await AutomationStore.ForTenantAsync(session, ct), ct);
        return 0;
    }

    private async Task<T> InTenantAsync<T>(Guid tenant, Func<DbSession, Guid, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SystemTenantId = tenant;
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        var result = await work(session, tenant, ct);
        await session.CommitAsync(ct);
        return result;
    }

    private async Task<int> PlanAsync(DbSession session, Guid tenant, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var settings = await AutomationStore.ForTenantAsync(session, ct);
        var templates = (await session.QueryAsync("SELECT kind, language, body, is_enabled FROM care.message_templates",
            r => (Kind: r.GetString(0), Lang: r.GetString(1), Body: r.GetString(2), Enabled: r.GetBoolean(3)), ct))
            .ToDictionary(x => (x.Kind, x.Lang));
        var candidates = await session.QueryAsync(
            """
            SELECT a.id, a.facility_id, a.patient_id, a.starts_at, a.status, a.checked_in_at, a.created_at,
                   c.preferred_channel, c.sms_phone, c.telegram_chat_id, c.language,
                   coalesce(pc.given_name, ''), coalesce(s.display_name, ''), coalesce(ci.clinic_name, ''), coalesce(ci.clinic_phone, ''), coalesce(ci.time_zone, 'Africa/Addis_Ababa')
            FROM scheduling.v_appointment_care a
            JOIN care.contact_preferences c ON c.patient_id = a.patient_id AND c.consent_reminders AND c.preferred_channel <> 'None'
            LEFT JOIN patients.v_patient_contact pc ON pc.patient_id = a.patient_id
            LEFT JOIN (SELECT DISTINCT practitioner_id, display_name FROM identity.v_staff_reference WHERE practitioner_id IS NOT NULL) s ON s.practitioner_id = a.practitioner_id
            LEFT JOIN configuration.v_clinic_identity ci ON ci.facility_id = a.facility_id
            WHERE a.tenant_id = @t AND (a.created_at > @now - interval '3 days' OR a.starts_at BETWEEN @now - interval '4 days' AND @now + interval '2 days'
                                       OR a.checked_in_at > @now - interval '3 days')
            """,
            r => new Candidate(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.Time(5), r.GetFieldValue<DateTimeOffset>(6),
                r.GetString(7), r.GetString(8), r.Str(9), r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13), r.GetString(14), r.GetString(15)),
            ct, ("t", tenant), ("now", now));

        var planned = 0;
        foreach (var c in candidates)
        {
            var address = c.Channel == "Telegram" ? c.ChatId : c.SmsPhone;
            if (string.IsNullOrEmpty(address))
            {
                continue;
            }

            var zone = BusinessTime.Resolve(c.TimeZone);
            var s = settings.GetValueOrDefault(c.FacilityId, AutomationSettings.Default);
            var plan = new List<(string Kind, DateTimeOffset Due)>();
            void Add(string kind, DateTimeOffset? due, bool beforeVisit = false)
            {
                if (due is not { } d || d > now.AddDays(4))
                {
                    return;
                }

                var at = CareSchedule.RespectQuietHours(d < now ? now : d, zone, s.QuietStart, s.QuietEnd);
                if (beforeVisit && at > c.StartsAt.AddMinutes(-15))
                {
                    return; // quiet hours would push a reminder past the visit: skip it
                }

                plan.Add((kind, at));
            }

            if (s.ConfirmationsEnabled)
            {
                Add(MessageKinds.Confirmation, CareSchedule.ConfirmationDue(now, c.CreatedAt, c.StartsAt, c.Status), beforeVisit: true);
            }

            if (s.ReminderEnabled)
            {
                Add(MessageKinds.Reminder, CareSchedule.ReminderDue(now, c.CreatedAt, c.StartsAt, c.Status, s.ReminderLeadHours), beforeVisit: true);
            }

            if (s.SameDayReminderEnabled)
            {
                Add(MessageKinds.SameDayReminder, CareSchedule.SameDayDue(now, c.CreatedAt, c.StartsAt, c.Status, s.SameDayLeadHours), beforeVisit: true);
            }

            if (s.FollowUpEnabled)
            {
                Add(MessageKinds.FollowUp, CareSchedule.FollowUpDue(now, c.CheckedInAt, c.Status, s.FollowUpDelayHours));
            }

            if (s.RebookEnabled)
            {
                Add(MessageKinds.Rebook, CareSchedule.RebookDue(now, c.StartsAt, c.Status));
            }
            foreach (var (kind, due) in plan)
            {
                var template = templates.TryGetValue((kind, c.Language), out var t) ? t : default;
                if (template.Body is not null && !template.Enabled)
                {
                    continue;
                }

                var body = Templates.Render(template.Body ?? Templates.Default(kind, c.Language),
                    new TemplateValues(c.Clinic, c.ClinicPhone, c.FirstName, TimeZoneInfo.ConvertTime(c.StartsAt, zone), c.Clinician));
                planned += await session.ExecAsync(
                    """
                    INSERT INTO care.messages (id, tenant_id, facility_id, patient_id, kind, source_id, channel, address, body, due_at, status, created_at)
                    VALUES (@id, @t, @f, @p, @k, @s, @ch, @addr, @b, @due, 'Scheduled', @now)
                    ON CONFLICT (tenant_id, kind, source_id, patient_id) DO NOTHING
                    """, ct, ("id", Guid.CreateVersion7()), ("t", tenant), ("f", c.FacilityId), ("p", c.PatientId), ("k", kind), ("s", c.AppointmentId),
                    ("ch", c.Channel), ("addr", address), ("b", body), ("due", due), ("now", now));
            }
        }

        // A booking cancelled or completed before its reminder went out must not be reminded.
        await session.ExecAsync(
            """
            UPDATE care.messages m SET status = 'Cancelled', last_error = 'Appointment no longer booked'
            FROM scheduling.v_appointment_care a
            WHERE m.source_id = a.id AND m.kind IN ('Confirmation', 'Reminder', 'SameDayReminder') AND m.status = 'Scheduled' AND a.status <> 'Booked'
            """, ct);

        planned += await QueueCampaignsAsync(session, tenant, now, ct);
        return planned;
    }

    /// <summary>
    /// Recalls: create the automatic check-up recall after a visit (when the patient has nothing booked), mark recalls
    /// booked/done/reopened from the appointment book, invite consenting patients ahead of the due date, and escalate
    /// overdue recalls to the care desk.
    /// </summary>
    private static async Task<int> ProcessRecallsAsync(DbSession session, Guid tenant, DateTimeOffset now, Dictionary<Guid, AutomationSettings> settings,
        Dictionary<(string Kind, string Lang), (string Kind, string Lang, string Body, bool Enabled)> templates, CancellationToken ct)
    {
        var facilities = await session.QueryAsync("SELECT facility_id, clinic_name, clinic_phone, time_zone FROM configuration.v_clinic_identity",
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Phone: r.GetString(2), Zone: r.GetString(3)), ct);
        var created = 0;
        foreach (var f in facilities)
        {
            var s = settings.GetValueOrDefault(f.Id, AutomationSettings.Default);
            var zone = BusinessTime.Resolve(f.Zone);
            var today = BusinessTime.LocalDate(now, zone);
            if (s.AutoCheckupRecallEnabled)
            {
                await session.ExecAsync(
                    """
                    INSERT INTO care.recalls (id, tenant_id, facility_id, patient_id, kind, note, due_on, status, source, source_appointment_id, created_by, created_at)
                    SELECT gen_random_uuid(), @t, a.facility_id, a.patient_id, 'CheckUp', '', ((a.checked_in_at AT TIME ZONE @tz)::date + make_interval(months => @m))::date,
                           'Due', 'Automatic', a.id, @auto, @now
                    FROM scheduling.v_appointment_care a
                    WHERE a.facility_id = @f AND a.status IN ('CheckedIn', 'Completed') AND a.checked_in_at > @now - interval '3 days'
                      AND NOT EXISTS (SELECT 1 FROM care.recalls r WHERE r.patient_id = a.patient_id AND r.status IN ('Due', 'Booked'))
                      AND NOT EXISTS (SELECT 1 FROM scheduling.v_appointment_care x WHERE x.patient_id = a.patient_id AND x.status = 'Booked' AND x.starts_at > @now)
                    ON CONFLICT (tenant_id, source_appointment_id) WHERE source = 'Automatic' DO NOTHING
                    """, ct, ("t", tenant), ("f", f.Id), ("tz", f.Zone), ("m", s.AutoCheckupMonths), ("auto", AutomationUserId), ("now", now));
            }

            // Booked: a new appointment at this facility, made after the recall, not more than 60 days before it is due.
            await session.ExecAsync(
                """
                UPDATE care.recalls r SET status = 'Booked', booked_appointment_id = sub.appt, version = r.version + 1
                FROM (SELECT r2.id AS recall_id,
                             (SELECT a.id FROM scheduling.v_appointment_care a
                              WHERE a.patient_id = r2.patient_id AND a.facility_id = r2.facility_id AND a.status = 'Booked' AND a.created_at > r2.created_at
                                AND (a.starts_at AT TIME ZONE @tz)::date >= r2.due_on - 60 AND a.starts_at > @now
                              ORDER BY a.starts_at LIMIT 1) AS appt
                      FROM care.recalls r2 WHERE r2.facility_id = @f AND r2.status = 'Due') sub
                WHERE r.id = sub.recall_id AND sub.appt IS NOT NULL
                """, ct, ("f", f.Id), ("tz", f.Zone), ("now", now));
            // Visit happened -> done; booking cancelled or missed -> due again.
            await session.ExecAsync(
                """
                UPDATE care.recalls r SET status = CASE WHEN a.status IN ('CheckedIn', 'Completed') THEN 'Done' ELSE 'Due' END,
                       booked_appointment_id = CASE WHEN a.status IN ('CheckedIn', 'Completed') THEN r.booked_appointment_id END,
                       closed_at = CASE WHEN a.status IN ('CheckedIn', 'Completed') THEN @now END,
                       close_reason = CASE WHEN a.status IN ('CheckedIn', 'Completed') THEN 'Patient attended' END,
                       version = r.version + 1
                FROM scheduling.v_appointment_care a
                WHERE a.id = r.booked_appointment_id AND r.facility_id = @f AND r.status = 'Booked' AND a.status IN ('CheckedIn', 'Completed', 'Cancelled', 'NoShow')
                """, ct, ("f", f.Id), ("now", now));

            // Invitations to consenting patients.
            var invite = await session.QueryAsync(
                """
                SELECT r.id, r.patient_id, r.due_on, c.preferred_channel, CASE c.preferred_channel WHEN 'Telegram' THEN c.telegram_chat_id ELSE c.sms_phone END, c.language,
                       coalesce(p.given_name, '')
                FROM care.recalls r
                JOIN care.contact_preferences c ON c.patient_id = r.patient_id AND c.consent_recalls AND c.preferred_channel <> 'None'
                LEFT JOIN patients.v_patient_contact p ON p.patient_id = r.patient_id
                WHERE r.facility_id = @f AND r.status = 'Due' AND r.invited_at IS NULL AND r.due_on - @lead <= @today
                """,
                r => (Id: r.GetGuid(0), Patient: r.GetGuid(1), Due: r.GetFieldValue<DateOnly>(2), Channel: r.GetString(3), Address: r.Str(4), Lang: r.GetString(5), First: r.GetString(6)),
                ct, ("f", f.Id), ("lead", s.RecallInviteLeadDays), ("today", today));
            var template = MessageKinds.Recall;
            foreach (var i in invite.Where(i => !string.IsNullOrEmpty(i.Address)))
            {
                var t = templates.TryGetValue((template, i.Lang), out var tpl) ? tpl : default;
                if (t.Body is not null && !t.Enabled)
                {
                    continue;
                }

                var dueLocal = new DateTimeOffset(i.Due.ToDateTime(new TimeOnly(9, 0)), zone.GetUtcOffset(i.Due.ToDateTime(new TimeOnly(9, 0))));
                var body = Templates.Render(t.Body ?? Templates.Default(template, i.Lang), new TemplateValues(f.Name, f.Phone, i.First, dueLocal, ""));
                var at = CareSchedule.RespectQuietHours(now, zone, s.QuietStart, s.QuietEnd);
                created += await session.ExecAsync(
                    """
                    INSERT INTO care.messages (id, tenant_id, facility_id, patient_id, kind, source_id, channel, address, body, due_at, status, created_at)
                    VALUES (@id, @t, @f, @p, 'Recall', @r, @ch, @a, @b, @due, 'Scheduled', @now)
                    ON CONFLICT (tenant_id, kind, source_id, patient_id) DO NOTHING
                    """, ct, ("id", Guid.CreateVersion7()), ("t", tenant), ("f", f.Id), ("p", i.Patient), ("r", i.Id), ("ch", i.Channel), ("a", i.Address!), ("b", body), ("due", at), ("now", now));
                await session.ExecAsync("UPDATE care.recalls SET invited_at = @now WHERE id = @id", ct, ("id", i.Id), ("now", now));
            }

            // Overdue: a person calls the patient.
            await session.ExecAsync(
                """
                WITH overdue AS (
                  UPDATE care.recalls SET escalated_at = @now, version = version + 1
                  WHERE facility_id = @f AND status = 'Due' AND escalated_at IS NULL AND due_on + @days < @today
                  RETURNING id, patient_id, kind, due_on)
                INSERT INTO care.tasks (id, tenant_id, facility_id, patient_id, source, priority, summary, status, due_at, created_at)
                SELECT gen_random_uuid(), @t, @f, o.patient_id, 'Recall', 'Normal',
                       'Recall overdue (' || o.kind || ', due ' || to_char(o.due_on, 'YYYY-MM-DD') || ') — call to book', 'Open', @now, @now
                FROM overdue o
                """, ct, ("t", tenant), ("f", f.Id), ("days", s.RecallOverdueDays), ("today", today), ("now", now));
        }

        return created;
    }

    /// <summary>
    /// Automatic staff notices (one per topic, facility and day; updated in place, archived when resolved): urgent
    /// call-backs, overdue recalls, yesterday's day close not locked, and a stale or missing backup.
    /// </summary>
    private static async Task PostSystemNoticesAsync(DbSession session, Guid tenant, DateTimeOffset now, Dictionary<Guid, AutomationSettings> settings, CancellationToken ct)
    {
        var facilities = await session.QueryAsync("SELECT facility_id, clinic_name, time_zone FROM configuration.v_clinic_identity",
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Zone: r.GetString(2)), ct);
        foreach (var f in facilities.Where(x => settings.GetValueOrDefault(x.Id, AutomationSettings.Default).StaffNoticesEnabled))
        {
            var zone = BusinessTime.Resolve(f.Zone);
            var today = BusinessTime.LocalDate(now, zone);
            var endOfDay = BusinessTime.DayRangeUtc(today, zone).EndUtc;
            var urgent = await session.ScalarAsync<long>("SELECT count(*) FROM care.tasks WHERE facility_id = @f AND status = 'Open' AND priority = 'High'", ct, ("f", f.Id));
            await UpsertNoticeAsync(session, tenant, f.Id, $"urgent-tasks:{f.Id:N}:{today:yyyyMMdd}", urgent > 0, "Urgent",
                "Patients waiting for a call back", $"{urgent} patient(s) replied with a worrying message or a low rating. Open Care+ → Care desk and call them today.",
                ["Receptionist", "Nurse", "Clinician", "Dentist"], endOfDay, now, ct);
            var overdue = await session.ScalarAsync<long>("SELECT count(*) FROM care.recalls WHERE facility_id = @f AND status = 'Due' AND due_on < @today", ct, ("f", f.Id), ("today", today));
            await UpsertNoticeAsync(session, tenant, f.Id, $"recalls:{f.Id:N}:{today:yyyyMMdd}", overdue > 0, "Important",
                "Recalls to book", $"{overdue} patient(s) are past their recall date. Open Care+ → Recalls to book them.", ["Receptionist"], endOfDay, now, ct);
            var yesterday = today.AddDays(-1);
            var unclosed = await session.ScalarAsync<long>(
                """
                SELECT count(*) FROM finance.v_payment_facts p WHERE p.facility_id = @f AND p.business_date = @d AND p.status = 'Verified'
                  AND NOT EXISTS (SELECT 1 FROM finance.v_close_facts c WHERE c.facility_id = @f AND c.business_date = @d AND c.status = 'Locked')
                """, ct, ("f", f.Id), ("d", yesterday));
            await UpsertNoticeAsync(session, tenant, f.Id, $"close:{f.Id:N}:{yesterday:yyyyMMdd}", unclosed > 0, "Important",
                "Yesterday's day close is not locked", $"There are verified payments on {yesterday:yyyy-MM-dd} but no locked daily close. Open Finance → Day close.",
                ["FinanceSupervisor", "Owner"], endOfDay, now, ct);
        }

        // Npgsql returns timestamptz as a UTC DateTime; read it as such (a DateTimeOffset cast throws once any backup exists).
        var lastBackupUtc = await session.ScalarAsync<DateTime?>("SELECT max(created_at) FROM platform.backup_log WHERE action = 'Created'", ct);
        DateTimeOffset? lastBackup = lastBackupUtc is { } b ? new DateTimeOffset(DateTime.SpecifyKind(b, DateTimeKind.Utc)) : null;
        var week = System.Globalization.ISOWeek.GetWeekOfYear(now.UtcDateTime);
        await UpsertNoticeAsync(session, tenant, null, $"backup:{now.UtcDateTime:yyyy}w{week}", lastBackup is null || now - lastBackup.Value > TimeSpan.FromDays(7), "Important",
            "Take an encrypted backup", lastBackup is null ? "No backup has been taken yet. Open Data & backups → Backups." : $"The last backup is {(int)(now - lastBackup.Value).TotalDays} days old. Open Data & backups → Backups.",
            ["Owner"], now.AddDays(7), now, ct);
    }

    private static async Task UpsertNoticeAsync(DbSession session, Guid tenant, Guid? facility, string key, bool active, string priority, string title, string body,
        string[] roles, DateTimeOffset expires, DateTimeOffset now, CancellationToken ct)
    {
        if (active)
        {
            await session.ExecAsync(
                """
                INSERT INTO comms.notices (id, tenant_id, facility_id, title, body, priority, audience_roles, pinned, requires_ack, expires_at, source, system_key, status, created_at, updated_at)
                VALUES (@id, @t, @f, @ti, @b, @p, @roles, false, false, @exp, 'System', @k, 'Active', @now, @now)
                ON CONFLICT (tenant_id, system_key) WHERE system_key IS NOT NULL
                DO UPDATE SET body = EXCLUDED.body, updated_at = CASE WHEN comms.notices.body <> EXCLUDED.body THEN EXCLUDED.updated_at ELSE comms.notices.updated_at END
                WHERE comms.notices.status = 'Active'
                """, ct, ("id", Guid.CreateVersion7()), ("t", tenant), ("f", facility), ("ti", title), ("b", body), ("p", priority), ("roles", roles), ("exp", expires),
                ("k", key), ("now", now));
        }
        else
        {
            await session.ExecAsync("UPDATE comms.notices SET status = 'Archived', archived_at = @now, system_key = system_key || ':closed:' || id::text, version = version + 1 WHERE tenant_id = @t AND system_key = @k AND status = 'Active'",
                ct, ("t", tenant), ("k", key), ("now", now));
        }
    }

    private static async Task<int> QueueCampaignsAsync(DbSession session, Guid tenant, DateTimeOffset now, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            SELECT c.id, c.facility_id, c.audience, c.months, t.title, t.body, t.language, coalesce(ci.clinic_name, ''), coalesce(ci.time_zone, 'Africa/Addis_Ababa')
            FROM care.campaigns c JOIN care.health_tips t ON t.id = c.tip_id AND t.status = 'Approved'
            LEFT JOIN configuration.v_clinic_identity ci ON ci.facility_id = c.facility_id
            WHERE c.status = 'Scheduled' AND c.scheduled_at <= @now
            FOR UPDATE OF c SKIP LOCKED
            """,
            r => (Id: r.GetGuid(0), Facility: r.GetGuid(1), Audience: r.GetString(2), Months: r.IsDBNull(3) ? (int?)null : r.GetInt32(3), Title: r.GetString(4), Body: r.GetString(5),
                Lang: r.GetString(6), Clinic: r.GetString(7), Zone: r.GetString(8)), ct, ("now", now));
        var total = 0;
        foreach (var c in due)
        {
            var text = $"{c.Clinic} — {c.Title}: {c.Body} (Reply STOP to opt out.)";
            var sendAt = CareSchedule.RespectQuietHours(now, BusinessTime.Resolve(c.Zone));
            var n = await session.ExecAsync(
                """
                INSERT INTO care.messages (id, tenant_id, facility_id, patient_id, kind, source_id, channel, address, body, due_at, status, created_at)
                SELECT gen_random_uuid(), @t, @f, cp.patient_id, 'Tip', @c, cp.preferred_channel,
                       CASE cp.preferred_channel WHEN 'Telegram' THEN cp.telegram_chat_id ELSE cp.sms_phone END, @b, @due, 'Scheduled', @now
                FROM care.contact_preferences cp
                JOIN patients.v_patient_contact pc ON pc.patient_id = cp.patient_id
                WHERE cp.consent_tips AND cp.preferred_channel <> 'None'
                  AND coalesce(CASE cp.preferred_channel WHEN 'Telegram' THEN cp.telegram_chat_id ELSE cp.sms_phone END, '') <> ''
                  -- only this facility's patients (registered here or seen here)
                  AND (pc.registered_at_facility_id = @f OR EXISTS (SELECT 1 FROM scheduling.v_appointment_care y WHERE y.patient_id = cp.patient_id AND y.facility_id = @f))
                  -- at most one health tip per patient per week, however many campaigns are scheduled
                  AND NOT EXISTS (SELECT 1 FROM care.messages t WHERE t.patient_id = cp.patient_id AND t.kind = 'Tip' AND t.created_at > @now - interval '7 days')
                  AND (@a = 'AllConsented' OR EXISTS (SELECT 1 FROM scheduling.v_appointment_care x WHERE x.patient_id = cp.patient_id AND x.facility_id = @f
                        AND x.checked_in_at > @now - make_interval(months => @m)))
                ON CONFLICT (tenant_id, kind, source_id, patient_id) DO NOTHING
                """, ct, ("t", tenant), ("f", c.Facility), ("c", c.Id), ("b", text), ("due", sendAt), ("now", now), ("a", c.Audience), ("m", c.Months ?? 0));
            await session.ExecAsync("UPDATE care.campaigns SET status = 'Queued', recipients = @n, queued_at = @now, version = version + 1 WHERE id = @id",
                ct, ("id", c.Id), ("n", n), ("now", now));
            total += n;
        }

        return total;
    }

    /// <summary>
    /// Sends due messages one per transaction, so a failure (of any kind) affects only that message and never rolls back
    /// others that were already handed to the provider.
    /// </summary>
    private async Task<(int Sent, int Failed, int Skipped)> DispatchAsync(DbSession unused, Guid tenant, CancellationToken ct)
    {
        int sent = 0, failed = 0, skipped = 0;
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            var outcome = await InTenantAsync(tenant, (session, _, token) => SendOneAsync(session, token), ct);
            if (outcome is null)
            {
                break;
            }

            switch (outcome)
            {
                case "Sent": sent++; break;
                case "Failed": failed++; break;
                case "Skipped": skipped++; break;
            }
        }

        return (sent, failed, skipped);
    }

    private async Task<string?> SendOneAsync(DbSession session, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await session.QueryAsync(
            """
            SELECT m.id, m.channel, m.address, m.body, m.kind, m.attempts, m.patient_id,
                   coalesce(c.consent_reminders, false), coalesce(c.consent_tips, false), coalesce(c.preferred_channel, 'None'),
                   CASE m.channel WHEN 'Telegram' THEN c.telegram_chat_id ELSE c.sms_phone END, coalesce(c.consent_recalls, false)
            FROM care.messages m LEFT JOIN care.contact_preferences c ON c.patient_id = m.patient_id
            WHERE m.status = 'Scheduled' AND m.due_at <= @now
            ORDER BY m.due_at LIMIT 1
            FOR UPDATE OF m SKIP LOCKED
            """,
            r => (Id: r.GetGuid(0), Channel: r.GetString(1), Address: r.GetString(2), Body: r.GetString(3), Kind: r.GetString(4), Attempts: r.GetInt32(5), Patient: r.GetGuid(6),
                Reminders: r.GetBoolean(7), Tips: r.GetBoolean(8), Preferred: r.GetString(9), CurrentAddress: r.Str(10), Recalls: r.GetBoolean(11)),
            ct, ("now", now));
        if (due.Count == 0)
        {
            return null;
        }

        var m = due[0];
        string? skip = null;
        if (!(m.Kind == MessageKinds.Tip ? m.Tips : m.Kind == MessageKinds.Recall ? m.Recalls : m.Reminders))
        {
            skip = "No consent at send time";
        }
        else if (m.Preferred != m.Channel || m.CurrentAddress != m.Address)
        {
            skip = "Contact details changed since planning";
        }
        else if (m.Channel == "Telegram"
            && await session.ScalarAsync<Guid?>("SELECT patient_id FROM care.resolve_telegram_chat(@c)", ct, ("c", m.Address)) != m.Patient)
        {
            skip = "Telegram chat is now linked to someone else";
        }

        if (skip is not null)
        {
            await session.ExecAsync("UPDATE care.messages SET status = 'Skipped', last_error = @e WHERE id = @id", ct, ("id", m.Id), ("e", skip));
            return "Skipped";
        }

        try
        {
            var providerId = await channels.For(m.Channel).SendAsync(m.Channel, m.Address, m.Body, ct);
            await session.ExecAsync("UPDATE care.messages SET status = 'Sent', attempts = attempts + 1, provider_message_id = @pid, sent_at = @now, last_error = NULL WHERE id = @id",
                ct, ("id", m.Id), ("pid", providerId.Length > 100 ? providerId[..100] : providerId), ("now", now));
            return "Sent";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempts = m.Attempts + 1;
            var final = attempts >= MaxAttempts;
            var error = ex is HttpRequestException or TaskCanceledException ? ex.Message : "Provider error (" + ex.GetType().Name + ")";
            await session.ExecAsync("UPDATE care.messages SET status = @s, attempts = @a, last_error = @e, due_at = @next WHERE id = @id",
                ct, ("id", m.Id), ("s", final ? "Failed" : "Scheduled"), ("a", attempts), ("e", error.Length > 300 ? error[..300] : error), ("next", now.AddMinutes(5 * attempts)));
            return final ? "Failed" : "Retry";
        }
    }

    private sealed record Candidate(
        Guid AppointmentId, Guid FacilityId, Guid PatientId, DateTimeOffset StartsAt, string Status, DateTimeOffset? CheckedInAt, DateTimeOffset CreatedAt,
        string Channel, string SmsPhone, string? ChatId, string Language, string FirstName, string Clinician, string Clinic, string ClinicPhone, string TimeZone);
}

public sealed class CareAutomationService(CareAutomation automation, IOptions<CareOptions> options, ILogger<CareAutomationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutomationEnabled)
        {
            return;
        }

        var interval = options.Value.Interval < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : options.Value.Interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await automation.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Care+ automation run failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

/// <summary>
/// Telegram bot webhook: links a chat with a reception code ("/start CODE") and records patient replies (triaged into care
/// tasks or opt-out). Accepted only when Telegram is enabled and the request carries the configured secret token.
/// </summary>
public sealed class TelegramInbound(NpgsqlDataSource dataSource, IServiceScopeFactory scopes, CareChannels channels, TimeProvider clock, ILogger<TelegramInbound> logger)
{
    public async Task HandleAsync(JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("chat", out var chat) || !message.TryGetProperty("text", out var textEl))
        {
            return; // edits, stickers, channel posts: ignored
        }

        if (!chat.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var chatId = idEl.GetRawText();
        var text = (textEl.GetString() ?? "").Trim();
        if (text.Length is 0 or > 1000 || chatId.Length > 20)
        {
            return;
        }

        var updateId = update.TryGetProperty("update_id", out var u) ? u.GetRawText() : null;
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var code = text.Length > 6 ? text[6..].Trim() : "";
            var linked = code.Length == 8 ? await ResolveAsync("SELECT tenant_id, patient_id FROM care.claim_telegram_link(@a, @b)", CareService.Hash(code), chatId, ct) : null;
            if (linked is null)
            {
                await ReplyAsync(chatId, "Please send the 8-character code you received at reception, e.g. /start ABCD2345.", ct);
                return;
            }

            await InTenantAsync(linked.Value.Tenant, async session =>
            {
                await session.ExecAsync(
                    """
                    INSERT INTO care.contact_preferences (patient_id, tenant_id, telegram_chat_id, preferred_channel, updated_by, updated_at)
                    VALUES (@p, @t, @c, 'Telegram', @a, @now)
                    ON CONFLICT (patient_id) DO UPDATE SET telegram_chat_id = EXCLUDED.telegram_chat_id,
                      preferred_channel = CASE WHEN care.contact_preferences.preferred_channel = 'None' THEN 'Telegram' ELSE care.contact_preferences.preferred_channel END,
                      updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at, version = care.contact_preferences.version + 1
                    """, ct, ("p", linked.Value.Patient), ("t", linked.Value.Tenant), ("c", chatId), ("a", CareAutomation.AutomationUserId), ("now", clock.GetUtcNow()));
            }, ct);
            await ReplyAsync(chatId, "Linked. You will receive the messages you agreed to at the clinic. Reply STOP at any time to stop them.", ct);
            return;
        }

        var who = await ResolveAsync("SELECT tenant_id, patient_id FROM care.resolve_telegram_chat(@a)", chatId, null, ct);
        if (who is null)
        {
            return; // unlinked chats get no answer (no amplification of spam)
        }

        ReplyDto? reply = null;
        await InTenantAsync(who.Value.Tenant, async session =>
        {
            var last = await session.QueryAsync(
                """
                SELECT m.id, m.facility_id FROM care.messages m WHERE m.patient_id = @p AND m.status = 'Sent' ORDER BY m.sent_at DESC LIMIT 1
                """, r => (Id: r.GetGuid(0), Facility: r.GetGuid(1)), ct, ("p", who.Value.Patient));
            var facility = last.Count == 1 ? last[0].Facility
                : await session.ScalarAsync<Guid>("SELECT registered_at_facility_id FROM patients.v_patient_contact WHERE patient_id = @p", ct, ("p", who.Value.Patient));
            if (updateId is not null && await session.ScalarAsync<int?>("SELECT 1 FROM care.replies WHERE channel = 'Telegram' AND external_id = @e", ct, ("e", updateId)) == 1)
            {
                return; // Telegram retried the same update
            }

            reply = await CareReplies.RecordAsync(session, who.Value.Tenant, facility, who.Value.Patient, last.Count == 1 ? last[0].Id : null, "Telegram", text, updateId,
                CareAutomation.AutomationUserId, clock.GetUtcNow(), ct);
        }, ct);
        if (reply is { OptOut: true })
        {
            await ReplyAsync(chatId, "Done — you will not receive further messages. Thank you.", ct);
        }
        else if (reply is { NeedsAttention: true })
        {
            await ReplyAsync(chatId, "Thank you. Our team will contact you during clinic hours. If this is an emergency or you feel very unwell, go to the nearest emergency department or call 907 now.", ct);
        }
        else if (reply is not null)
        {
            await ReplyAsync(chatId, "Thank you for your reply.", ct);
        }
    }

    private async Task<(Guid Tenant, Guid Patient)?> ResolveAsync(string sql, string a, string? b, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("a", a);
        if (b is not null)
        {
            cmd.Parameters.AddWithValue("b", b);
        }

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetGuid(1)) : null;
    }

    private async Task InTenantAsync(Guid tenant, Func<DbSession, Task> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SystemTenantId = tenant;
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        await work(session);
        await session.CommitAsync(ct);
    }

    private async Task ReplyAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            await channels.For("Telegram").SendAsync("Telegram", chatId, text, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Telegram acknowledgement could not be sent.");
        }
    }
}
