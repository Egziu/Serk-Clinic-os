using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using Serk.Clinic.Care.Domain;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Application;

public sealed record ConsentEventDto(bool Reminders, bool Tips, string Method, DateTimeOffset RecordedAt, bool Recalls = false);

public sealed record ContactDto(
    Guid PatientId, string SmsPhone, string RegisteredPhone, bool TelegramLinked, string PreferredChannel, string Language,
    bool ConsentReminders, bool ConsentTips, string? ConsentMethod, DateTimeOffset? UpdatedAt, int Version, IReadOnlyList<ConsentEventDto> History, bool ConsentRecalls = false);

public sealed record UpdateContactRequest(string? SmsPhone, string? PreferredChannel, string? Language, bool ConsentReminders, bool ConsentTips, string? ConsentMethod, bool ConsentRecalls = false);

public sealed record TelegramLinkDto(string Code, DateTimeOffset ExpiresAt, string? DeepLink, bool BotLive);

public sealed record CareMessageDto(Guid Id, string Kind, string Channel, string Status, DateTimeOffset DueAt, DateTimeOffset? SentAt, int Attempts, string? LastError,
    Guid PatientId, string Mrn, string PatientName, string Body, int Segments, bool Simulated);

public sealed record ReplyDto(Guid Id, Guid PatientId, string Mrn, string PatientName, string Channel, string Text, int? Rating, bool NeedsAttention, bool OptOut, DateTimeOffset ReceivedAt);

public sealed record CareTaskDto(Guid Id, Guid PatientId, string Mrn, string PatientName, string Phone, string Source, string Priority, string Summary, string Status,
    DateTimeOffset DueAt, DateTimeOffset CreatedAt, DateTimeOffset? DoneAt, string? Outcome, string? ReplyText, int Version);

public sealed record CompleteTaskRequest(string? Outcome);

public sealed record ManualTaskRequest(Guid PatientId, string? Summary, string? Priority);

public sealed record PhoneReplyRequest(Guid PatientId, string? Text);

public sealed record SimulateReplyRequest(string? Text);

public sealed record TemplateDto(string Kind, string Language, string Body, bool IsEnabled, bool IsDefault, int Version, string Sample, int Segments);

public sealed record UpdateTemplateRequest(string? Body, bool IsEnabled);

public sealed record TipDto(Guid Id, string Title, string Body, string Topic, string Language, string Status, Guid AuthorId, string AuthorName,
    string? ApprovedByName, DateTimeOffset? ApprovedAt, DateTimeOffset CreatedAt, int Version, int Segments);

public sealed record TipRequest(string? Title, string? Body, string? Topic, string? Language);

public sealed record CampaignDto(Guid Id, Guid TipId, string TipTitle, string Audience, int? Months, DateTimeOffset ScheduledAt, string Status,
    int Recipients, int Sent, int Failed, int Skipped, DateTimeOffset CreatedAt);

public sealed record CampaignRequest(Guid TipId, string? Audience, int? Months, DateTimeOffset? ScheduledAt);

public sealed record CareSummaryDto(int Days, int Scheduled, int Sent, int Failed, int Skipped, int Replies, double? AverageRating, int OpenTasks, int UrgentTasks,
    int PatientsConsentingReminders, int PatientsConsentingTips, int TelegramLinked, bool TelegramLive, bool SmsLive, IReadOnlyList<KindCount> ByKind);

public sealed record KindCount(string Kind, int Sent, int Failed);

/// <summary>
/// Staff-facing Care+: consent and contact channel per patient, the message log, replies and care tasks, templates, health
/// tips (four-eyes clinical approval) and tip campaigns. Every change is audited; patient texts never include clinical detail.
/// </summary>
public sealed class CareService(DbSession session, RequestContext context, AuditTrail audit, CareChannels channels, IOptions<CareOptions> options, TimeProvider clock)
{
    private static readonly string[] Methods = ["Verbal", "Written", "Telegram"];

    // ---------------- contact & consent ----------------
    public async Task<Result<ContactDto>> GetContactAsync(Guid patientId, CancellationToken ct)
    {
        context.RequireActor();
        var patient = (await session.QueryAsync("SELECT phone FROM patients.v_patient_contact WHERE patient_id = @p", r => r.Str(0) ?? "", ct, ("p", patientId))).ToList();
        if (patient.Count == 0)
        {
            return Error.NotFound();
        }

        var dto = await ContactAsync(patientId, patient[0], ct);
        await session.CommitAsync(ct);
        return dto;
    }

    public async Task<Result<ContactDto>> UpdateContactAsync(Guid patientId, UpdateContactRequest r, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var registered = (await session.QueryAsync("SELECT phone FROM patients.v_patient_contact WHERE patient_id = @p", x => x.Str(0) ?? "", ct, ("p", patientId))).ToList();
        if (registered.Count == 0)
        {
            return Error.NotFound();
        }

        var phone = SocialRules.NormalisePhone(r.SmsPhone);
        var channel = r.PreferredChannel ?? "None";
        var language = r.Language ?? "en";
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (phone.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+?[0-9]{9,15}$"))
        {
            errors["smsPhone"] = ["Use an international mobile number, e.g. +251911234567."];
        }

        if (channel is not ("None" or "Sms" or "Telegram"))
        {
            errors["preferredChannel"] = ["Choose None, SMS or Telegram."];
        }

        if (language is not ("en" or "am" or "ti"))
        {
            errors["language"] = ["Choose English, Amharic or Tigrinya."];
        }

        var anyConsent = r.ConsentReminders || r.ConsentTips || r.ConsentRecalls;
        if (anyConsent && (r.ConsentMethod is null || !Methods.Contains(r.ConsentMethod)))
        {
            errors["consentMethod"] = ["Record how the patient agreed (verbal, written or via Telegram)."];
        }

        var current = await session.QueryAsync(
            "SELECT telegram_chat_id, version, consent_reminders, consent_tips, consent_recalls FROM care.contact_preferences WHERE patient_id = @p",
            x => (Chat: x.Str(0), Version: x.GetInt32(1), Reminders: x.GetBoolean(2), Tips: x.GetBoolean(3), Recalls: x.GetBoolean(4)), ct, ("p", patientId));
        var existing = current.Count == 1 ? current[0] : ((string?)null, 0, false, false, false);
        if (channel == "Sms" && phone.Length == 0)
        {
            errors["smsPhone"] = ["SMS needs a mobile number."];
        }

        if (channel == "Telegram" && existing.Item1 is null)
        {
            errors["preferredChannel"] = ["Link Telegram first (give the patient the link code)."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("invalid_contact", "Some contact details need attention.", errors);
        }

        if (ifMatch is null || ifMatch != existing.Item2)
        {
            return Error.Precondition("contact_changed", "These contact details changed meanwhile (or If-Match missing). Reload and retry.");
        }

        var now = clock.GetUtcNow();
        var changed = await session.ExecAsync(
            """
            INSERT INTO care.contact_preferences (patient_id, tenant_id, sms_phone, preferred_channel, language, consent_reminders, consent_tips, consent_recalls, consent_method, updated_by, updated_at, version)
            VALUES (@p, @t, @phone, @ch, @lang, @rem, @tips, @recalls, @method, @u, @now, 1)
            ON CONFLICT (patient_id) DO UPDATE SET sms_phone = EXCLUDED.sms_phone, preferred_channel = EXCLUDED.preferred_channel, language = EXCLUDED.language,
              consent_reminders = EXCLUDED.consent_reminders, consent_tips = EXCLUDED.consent_tips, consent_recalls = EXCLUDED.consent_recalls,
              consent_method = coalesce(EXCLUDED.consent_method, care.contact_preferences.consent_method),
              updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at, version = care.contact_preferences.version + 1
            WHERE care.contact_preferences.version = @v
            """, ct, ("p", patientId), ("t", actor.TenantId), ("phone", phone), ("ch", channel), ("lang", language), ("rem", r.ConsentReminders),
            ("tips", r.ConsentTips), ("recalls", r.ConsentRecalls), ("method", anyConsent ? r.ConsentMethod : null), ("u", actor.UserId), ("now", now), ("v", existing.Item2));
        if (changed == 0)
        {
            return Error.Precondition("contact_changed", "These contact details changed meanwhile. Reload and retry.");
        }

        if (existing.Item3 != r.ConsentReminders || existing.Item4 != r.ConsentTips || existing.Item5 != r.ConsentRecalls || current.Count == 0)
        {
            await session.ExecAsync(
                "INSERT INTO care.consent_events (id, tenant_id, patient_id, consent_reminders, consent_tips, consent_recalls, method, recorded_by, recorded_at) VALUES (@id, @t, @p, @rem, @tips, @recalls, @m, @u, @now)",
                ct, ("id", Guid.CreateVersion7()), ("t", actor.TenantId), ("p", patientId), ("rem", r.ConsentReminders), ("tips", r.ConsentTips), ("recalls", r.ConsentRecalls),
                ("m", anyConsent ? r.ConsentMethod : "StaffWithdrawal"), ("u", actor.UserId), ("now", now));
            if (!r.ConsentReminders || !r.ConsentTips || !r.ConsentRecalls)
            {
                // Withdrawn consent stops anything still queued for that purpose.
                await CancelQueuedAsync(patientId, !r.ConsentReminders, !r.ConsentTips, !r.ConsentRecalls, ct);
            }
        }

        await audit.RecordAsync(new AuditEntry("care.contact.update", "patient_contact", patientId, patientId, Metadata: new Dictionary<string, string>
        {
            ["channel"] = channel,
            ["consentReminders"] = r.ConsentReminders.ToString(),
            ["consentTips"] = r.ConsentTips.ToString(),
            ["consentRecalls"] = r.ConsentRecalls.ToString(),
            ["method"] = r.ConsentMethod ?? "",
        }), ct);
        var dto = await ContactAsync(patientId, registered[0], ct);
        await session.CommitAsync(ct);
        return dto;
    }

    /// <summary>One-time code (30 minutes) the patient sends to the clinic's Telegram bot; stored only as a hash.</summary>
    public async Task<Result<TelegramLinkDto>> CreateTelegramLinkAsync(Guid patientId, CancellationToken ct)
    {
        context.RequireActor();
        if ((await session.QueryAsync("SELECT 1 FROM patients.v_patient_contact WHERE patient_id = @p", _ => 1, ct, ("p", patientId))).Count == 0)
        {
            return Error.NotFound();
        }

        var code = NewCode();
        var expires = clock.GetUtcNow().AddMinutes(30);
        await session.ExecAsync("SELECT care.create_telegram_link(@h, @p, @e)", ct, ("h", Hash(code)), ("p", patientId), ("e", expires));
        await audit.RecordAsync(new AuditEntry("care.telegram.link_code", "patient_contact", patientId, patientId), ct);
        await session.CommitAsync(ct);
        var bot = options.Value.Telegram.BotUsername;
        return new TelegramLinkDto(code, expires, bot.Length > 0 ? $"https://t.me/{bot}?start={code}" : null, channels.IsLive("Telegram"));
    }

    public async Task<Result<bool>> UnlinkTelegramAsync(Guid patientId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        await session.ExecAsync("SELECT care.unlink_telegram_chat(@p)", ct, ("p", patientId));
        await session.ExecAsync("UPDATE care.messages SET status = 'Cancelled', last_error = 'Telegram unlinked' WHERE patient_id = @p AND channel = 'Telegram' AND status = 'Scheduled'", ct, ("p", patientId));
        await session.ExecAsync(
            "UPDATE care.contact_preferences SET telegram_chat_id = NULL, preferred_channel = CASE WHEN preferred_channel = 'Telegram' THEN 'None' ELSE preferred_channel END, updated_by = @u, updated_at = @now, version = version + 1 WHERE patient_id = @p",
            ct, ("p", patientId), ("u", actor.UserId), ("now", clock.GetUtcNow()));
        await audit.RecordAsync(new AuditEntry("care.telegram.unlink", "patient_contact", patientId, patientId), ct);
        await session.CommitAsync(ct);
        return true;
    }

    // ---------------- desk: messages, replies, tasks ----------------
    public async Task<IReadOnlyList<CareMessageDto>> MessagesAsync(string? status, Guid? patientId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var list = await session.QueryAsync(
            """
            SELECT m.id, m.kind, m.channel, m.status, m.due_at, m.sent_at, m.attempts, m.last_error, m.patient_id, coalesce(p.mrn, ''), coalesce(p.display_name, ''), m.body,
                   m.provider_message_id
            FROM care.messages m LEFT JOIN patients.v_patient_contact p ON p.patient_id = m.patient_id
            WHERE m.facility_id = @f AND (@s::text IS NULL OR m.status = @s) AND (@p::uuid IS NULL OR m.patient_id = @p)
              AND m.created_at > now() - interval '60 days'
            ORDER BY m.due_at DESC LIMIT 300
            """,
            r => new CareMessageDto(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.Time(5), r.GetInt32(6), r.Str(7),
                r.GetGuid(8), r.GetString(9), r.GetString(10), r.GetString(11), Templates.SmsSegments(r.GetString(11)), (r.Str(12) ?? "").StartsWith("sandbox-", StringComparison.Ordinal)),
            ct, ("f", actor.FacilityId), ("s", status), ("p", patientId));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<IReadOnlyList<ReplyDto>> RepliesAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var list = await session.QueryAsync(
            """
            SELECT r.id, r.patient_id, coalesce(p.mrn, ''), coalesce(p.display_name, ''), r.channel, r.text, r.rating, r.needs_attention, r.opt_out, r.received_at
            FROM care.replies r LEFT JOIN patients.v_patient_contact p ON p.patient_id = r.patient_id
            LEFT JOIN care.messages m ON m.id = r.message_id
            WHERE r.received_at > now() - interval '30 days'
              AND coalesce(m.facility_id, p.registered_at_facility_id) = @f   -- replies stay with the facility that cares for them
            ORDER BY r.received_at DESC LIMIT 200
            """,
            r => new ReplyDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetBoolean(7), r.GetBoolean(8), r.GetFieldValue<DateTimeOffset>(9)),
            ct, ("f", actor.FacilityId));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<IReadOnlyList<CareTaskDto>> TasksAsync(bool open, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var list = await session.QueryAsync(
            """
            SELECT t.id, t.patient_id, coalesce(p.mrn, ''), coalesce(p.display_name, ''), coalesce(nullif(c.sms_phone, ''), p.phone, ''), t.source, t.priority, t.summary, t.status,
                   t.due_at, t.created_at, t.done_at, t.outcome, r.text, t.version
            FROM care.tasks t
            LEFT JOIN patients.v_patient_contact p ON p.patient_id = t.patient_id
            LEFT JOIN care.contact_preferences c ON c.patient_id = t.patient_id
            LEFT JOIN care.replies r ON r.id = t.reply_id
            WHERE t.facility_id = @f AND (t.status = 'Open') = @open AND (@open OR t.done_at > now() - interval '14 days')
            ORDER BY CASE t.priority WHEN 'High' THEN 0 ELSE 1 END, t.due_at LIMIT 300
            """,
            r => new CareTaskDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10), r.Time(11), r.Str(12), r.Str(13), r.GetInt32(14)),
            ct, ("f", actor.FacilityId), ("open", open));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<bool>> CompleteTaskAsync(Guid taskId, CompleteTaskRequest r, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var outcome = r.Outcome?.Trim();
        if (outcome is null || outcome.Length is < 3 or > 500)
        {
            return Error.Validation("outcome_required", "Write what was done (3–500 characters), e.g. 'Called; pain mild, advised to come tomorrow at 09:00'.",
                new Dictionary<string, string[]> { ["outcome"] = ["3–500 characters."] });
        }

        var patient = await session.ScalarAsync<Guid?>(
            """
            UPDATE care.tasks SET status = 'Done', done_by = @u, done_at = @now, outcome = @o, version = version + 1
            WHERE id = @id AND facility_id = @f AND status = 'Open' AND (@v::int IS NULL OR version = @v) RETURNING patient_id
            """, ct, ("id", taskId), ("f", actor.FacilityId), ("u", actor.UserId), ("now", clock.GetUtcNow()), ("o", outcome), ("v", ifMatch));
        if (patient is null)
        {
            return Error.Conflict("task_changed", "This task was already closed or changed. Reload.");
        }

        await audit.RecordAsync(new AuditEntry("care.task.complete", "care_task", taskId, patient), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<Result<Guid>> CreateManualTaskAsync(ManualTaskRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var summary = r.Summary?.Trim();
        if (summary is null || summary.Length is < 3 or > 300)
        {
            return Error.Validation("summary_required", "Describe the follow-up (3–300 characters).");
        }

        if ((await session.QueryAsync("SELECT 1 FROM patients.v_patient_contact WHERE patient_id = @p", _ => 1, ct, ("p", r.PatientId))).Count == 0)
        {
            return Error.NotFound();
        }

        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await session.ExecAsync(
            "INSERT INTO care.tasks (id, tenant_id, facility_id, patient_id, source, priority, summary, status, due_at, created_at) VALUES (@id, @t, @f, @p, 'Manual', @prio, @s, 'Open', @now, @now)",
            ct, ("id", id), ("t", actor.TenantId), ("f", actor.FacilityId), ("p", r.PatientId), ("prio", r.Priority == "High" ? "High" : "Normal"), ("s", summary), ("now", now));
        await audit.RecordAsync(new AuditEntry("care.task.create", "care_task", id, r.PatientId), ct);
        await session.CommitAsync(ct);
        return id;
    }

    /// <summary>Records what a patient said on the phone (same triage as written replies).</summary>
    public async Task<Result<ReplyDto>> RecordPhoneReplyAsync(PhoneReplyRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Trim().Length > 1000)
        {
            return Error.Validation("text_required", "Write what the patient said (1–1000 characters).");
        }

        if ((await session.QueryAsync("SELECT 1 FROM patients.v_patient_contact WHERE patient_id = @p", _ => 1, ct, ("p", r.PatientId))).Count == 0)
        {
            return Error.NotFound();
        }

        var reply = await CareReplies.RecordAsync(session, actor.TenantId, actor.FacilityId, r.PatientId, null, "Phone", r.Text.Trim(), null, actor.UserId, clock.GetUtcNow(), ct);
        await audit.RecordAsync(new AuditEntry("care.reply.phone", "care_reply", reply.Id, r.PatientId), ct);
        await session.CommitAsync(ct);
        return reply;
    }

    /// <summary>Sandbox only: pretend the patient answered a message (for demos and training).</summary>
    public async Task<Result<ReplyDto>> SimulateReplyAsync(Guid messageId, SimulateReplyRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Trim().Length > 1000)
        {
            return Error.Validation("text_required", "Write the simulated reply (1–1000 characters).");
        }

        var msg = await session.QueryAsync("SELECT patient_id, channel, facility_id FROM care.messages WHERE id = @id AND facility_id = @f",
            x => (Patient: x.GetGuid(0), Channel: x.GetString(1), Facility: x.GetGuid(2)), ct, ("id", messageId), ("f", actor.FacilityId));
        if (msg.Count == 0)
        {
            return Error.NotFound();
        }

        var reply = await CareReplies.RecordAsync(session, actor.TenantId, msg[0].Facility, msg[0].Patient, messageId, msg[0].Channel, r.Text.Trim(), "sim-" + Guid.NewGuid().ToString("N"), actor.UserId, clock.GetUtcNow(), ct);
        await audit.RecordAsync(new AuditEntry("care.reply.simulated", "care_reply", reply.Id, msg[0].Patient), ct);
        await session.CommitAsync(ct);
        return reply;
    }

    public async Task<Result<bool>> RetryAsync(Guid messageId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var n = await session.ExecAsync("UPDATE care.messages SET status = 'Scheduled', attempts = 0, last_error = NULL, due_at = @now WHERE id = @id AND facility_id = @f AND status = 'Failed'",
            ct, ("id", messageId), ("f", actor.FacilityId), ("now", clock.GetUtcNow()));
        if (n == 0)
        {
            return Error.Conflict("not_failed", "Only failed messages can be retried.");
        }

        await audit.RecordAsync(new AuditEntry("care.message.retry", "care_message", messageId), ct);
        await session.CommitAsync(ct);
        return true;
    }

    // ---------------- templates ----------------
    public async Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken ct)
    {
        context.RequireActor();
        var rows = await session.QueryAsync("SELECT kind, language, body, is_enabled, version FROM care.message_templates",
            r => (Kind: r.GetString(0), Lang: r.GetString(1), Body: r.GetString(2), Enabled: r.GetBoolean(3), Version: r.GetInt32(4)), ct);
        var clinic = await ClinicAsync(ct);
        var sample = new TemplateValues(clinic.Name, clinic.Phone, "Selam", DateTimeOffset.Now.Date.AddDays(1).AddHours(9), "Dr. Abebe");
        var list = new List<TemplateDto>();
        foreach (var kind in MessageKinds.Templated)
        {
            foreach (var lang in new[] { "en", "am", "ti" })
            {
                var row = rows.FirstOrDefault(x => x.Kind == kind && x.Lang == lang);
                var body = row.Body ?? Templates.Default(kind, lang);
                var text = Templates.Render(body, sample);
                list.Add(new TemplateDto(kind, lang, body, row.Body is null || row.Enabled, row.Body is null, row.Version, text, Templates.SmsSegments(text)));
            }
        }

        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<bool>> UpdateTemplateAsync(string kind, string language, UpdateTemplateRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (!MessageKinds.Templated.Contains(kind) || language is not ("en" or "am" or "ti"))
        {
            return Error.NotFound();
        }

        var body = Templates.Validate(r.Body);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        await session.ExecAsync(
            """
            INSERT INTO care.message_templates (id, tenant_id, kind, language, body, is_enabled, updated_by, updated_at)
            VALUES (@id, @t, @k, @l, @b, @e, @u, @now)
            ON CONFLICT (tenant_id, kind, language) DO UPDATE SET body = EXCLUDED.body, is_enabled = EXCLUDED.is_enabled, updated_by = EXCLUDED.updated_by,
              updated_at = EXCLUDED.updated_at, version = care.message_templates.version + 1
            """, ct, ("id", Guid.CreateVersion7()), ("t", actor.TenantId), ("k", kind), ("l", language), ("b", body.Value), ("e", r.IsEnabled), ("u", actor.UserId), ("now", clock.GetUtcNow()));
        await audit.RecordAsync(new AuditEntry("care.template.update", "care_template", Guid.Empty, Metadata: new Dictionary<string, string> { ["kind"] = kind, ["language"] = language, ["enabled"] = r.IsEnabled.ToString() }), ct);
        await session.CommitAsync(ct);
        return true;
    }

    // ---------------- health tips (four-eyes) ----------------
    public async Task<IReadOnlyList<TipDto>> TipsAsync(CancellationToken ct)
    {
        context.RequireActor();
        var list = await session.QueryAsync(
            """
            SELECT t.id, t.title, t.body, t.topic, t.language, t.status, t.author_id, coalesce(a.display_name, ''), b.display_name, t.approved_at, t.created_at, t.version
            FROM care.health_tips t
            LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) a ON a.user_id = t.author_id
            LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) b ON b.user_id = t.approved_by
            ORDER BY CASE t.status WHEN 'Draft' THEN 0 WHEN 'Approved' THEN 1 ELSE 2 END, t.created_at DESC
            """,
            r => new TipDto(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetGuid(6), r.GetString(7), r.Str(8), r.Time(9),
                r.GetFieldValue<DateTimeOffset>(10), r.GetInt32(11), Templates.SmsSegments(r.GetString(2))), ct);
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<Guid>> CreateTipAsync(TipRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var v = ValidateTip(r);
        if (v is not null)
        {
            return v;
        }

        var id = Guid.CreateVersion7();
        await session.ExecAsync(
            "INSERT INTO care.health_tips (id, tenant_id, title, body, topic, language, status, author_id, last_editor_id, created_at) VALUES (@id, @t, @ti, @b, @to, @l, 'Draft', @u, @u, @now)",
            ct, ("id", id), ("t", actor.TenantId), ("ti", r.Title!.Trim()), ("b", r.Body!.Trim()), ("to", r.Topic!.Trim()), ("l", r.Language ?? "en"), ("u", actor.UserId), ("now", clock.GetUtcNow()));
        await audit.RecordAsync(new AuditEntry("care.tip.create", "health_tip", id), ct);
        await session.CommitAsync(ct);
        return id;
    }

    public async Task<Result<bool>> UpdateTipAsync(Guid id, TipRequest r, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var v = ValidateTip(r);
        if (v is not null)
        {
            return v;
        }

        var n = await session.ExecAsync(
            "UPDATE care.health_tips SET title = @ti, body = @b, topic = @to, language = @l, last_editor_id = @u, version = version + 1 WHERE id = @id AND status = 'Draft' AND (@v::int IS NULL OR version = @v)",
            ct, ("id", id), ("u", actor.UserId), ("ti", r.Title!.Trim()), ("b", r.Body!.Trim()), ("to", r.Topic!.Trim()), ("l", r.Language ?? "en"), ("v", ifMatch));
        if (n == 0)
        {
            return Error.Conflict("tip_not_editable", "Only drafts can be edited (approved tips are final; write a new one).");
        }

        await audit.RecordAsync(new AuditEntry("care.tip.update", "health_tip", id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<Result<bool>> ApproveTipAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var people = await session.QueryAsync("SELECT author_id, last_editor_id FROM care.health_tips WHERE id = @id AND status = 'Draft'",
            r => (Author: r.GetGuid(0), Editor: r.GetGuid(1)), ct, ("id", id));
        if (people.Count == 0)
        {
            return Error.Conflict("tip_not_draft", "Only drafts can be approved.");
        }

        if (people[0].Author == actor.UserId || people[0].Editor == actor.UserId)
        {
            return Error.Forbidden("four_eyes", "A different clinician must approve a health tip (never its author or the last person who edited it).");
        }

        await session.ExecAsync("UPDATE care.health_tips SET status = 'Approved', approved_by = @u, approved_at = @now, version = version + 1 WHERE id = @id AND status = 'Draft'",
            ct, ("id", id), ("u", actor.UserId), ("now", clock.GetUtcNow()));
        await audit.RecordAsync(new AuditEntry("care.tip.approve", "health_tip", id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<Result<bool>> RetireTipAsync(Guid id, CancellationToken ct)
    {
        context.RequireActor();
        var n = await session.ExecAsync("UPDATE care.health_tips SET status = 'Retired', version = version + 1 WHERE id = @id AND status = 'Approved'", ct, ("id", id));
        if (n == 0)
        {
            return Error.Conflict("tip_not_approved", "Only approved tips can be retired.");
        }

        await session.ExecAsync("UPDATE care.campaigns SET status = 'Cancelled', version = version + 1 WHERE tip_id = @id AND status = 'Scheduled'", ct, ("id", id));
        await audit.RecordAsync(new AuditEntry("care.tip.retire", "health_tip", id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    // ---------------- campaigns ----------------
    public async Task<IReadOnlyList<CampaignDto>> CampaignsAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var list = await session.QueryAsync(
            """
            SELECT c.id, c.tip_id, t.title, c.audience, c.months, c.scheduled_at, c.status, c.recipients,
                   count(m.id) FILTER (WHERE m.status = 'Sent')::int, count(m.id) FILTER (WHERE m.status = 'Failed')::int,
                   count(m.id) FILTER (WHERE m.status IN ('Skipped', 'Cancelled'))::int, c.created_at
            FROM care.campaigns c JOIN care.health_tips t ON t.id = c.tip_id
            LEFT JOIN care.messages m ON m.kind = 'Tip' AND m.source_id = c.id
            WHERE c.facility_id = @f
            GROUP BY c.id, t.title ORDER BY c.scheduled_at DESC LIMIT 100
            """,
            r => new CampaignDto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6),
                r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetFieldValue<DateTimeOffset>(11)), ct, ("f", actor.FacilityId));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<Guid>> CreateCampaignAsync(CampaignRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var audience = r.Audience ?? "AllConsented";
        if (audience is not ("AllConsented" or "VisitedRecently") || (audience == "VisitedRecently" && r.Months is not (>= 1 and <= 60)))
        {
            return Error.Validation("invalid_audience", "Choose all consenting patients, or those who visited in the last 1–60 months.");
        }

        var status = await session.ScalarAsync<string>("SELECT status FROM care.health_tips WHERE id = @id", ct, ("id", r.TipId));
        if (status != "Approved")
        {
            return Error.Rule("tip_not_approved", "Only clinician-approved tips can be sent.");
        }

        var now = clock.GetUtcNow();
        var at = r.ScheduledAt ?? now;
        if (at < now.AddMinutes(-5) || at > now.AddDays(180))
        {
            return Error.Validation("invalid_time", "Schedule between now and 180 days ahead.");
        }

        var id = Guid.CreateVersion7();
        await session.ExecAsync(
            "INSERT INTO care.campaigns (id, tenant_id, facility_id, tip_id, audience, months, scheduled_at, status, created_by, created_at) VALUES (@id, @t, @f, @tip, @a, @m, @at, 'Scheduled', @u, @now)",
            ct, ("id", id), ("t", actor.TenantId), ("f", actor.FacilityId), ("tip", r.TipId), ("a", audience), ("m", audience == "VisitedRecently" ? r.Months : null), ("at", at), ("u", actor.UserId), ("now", now));
        await audit.RecordAsync(new AuditEntry("care.campaign.create", "care_campaign", id, Metadata: new Dictionary<string, string> { ["tip"] = r.TipId.ToString(), ["audience"] = audience }), ct);
        await session.CommitAsync(ct);
        return id;
    }

    public async Task<Result<bool>> CancelCampaignAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var n = await session.ExecAsync("UPDATE care.campaigns SET status = 'Cancelled', version = version + 1 WHERE id = @id AND facility_id = @f AND status = 'Scheduled'", ct, ("id", id), ("f", actor.FacilityId));
        var queued = await session.ExecAsync("UPDATE care.messages SET status = 'Cancelled' WHERE kind = 'Tip' AND source_id = @id AND facility_id = @f AND status = 'Scheduled'", ct, ("id", id), ("f", actor.FacilityId));
        if (n == 0 && queued == 0)
        {
            return Error.Conflict("not_cancellable", "Nothing left to cancel for this campaign.");
        }

        await audit.RecordAsync(new AuditEntry("care.campaign.cancel", "care_campaign", id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<CareSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var s = (await session.QueryAsync(
            """
            SELECT
              count(*) FILTER (WHERE status = 'Scheduled')::int, count(*) FILTER (WHERE status = 'Sent')::int, count(*) FILTER (WHERE status = 'Failed')::int,
              count(*) FILTER (WHERE status IN ('Skipped', 'Cancelled'))::int,
              (SELECT count(*)::int FROM care.replies r JOIN care.messages rm ON rm.id = r.message_id WHERE rm.facility_id = @f AND r.received_at > now() - interval '30 days'),
              (SELECT avg(r.rating)::float8 FROM care.replies r JOIN care.messages rm ON rm.id = r.message_id WHERE rm.facility_id = @f AND r.rating IS NOT NULL AND r.received_at > now() - interval '30 days'),
              (SELECT count(*)::int FROM care.tasks WHERE facility_id = @f AND status = 'Open'),
              (SELECT count(*)::int FROM care.tasks WHERE facility_id = @f AND status = 'Open' AND priority = 'High'),
              (SELECT count(*)::int FROM care.contact_preferences WHERE consent_reminders),
              (SELECT count(*)::int FROM care.contact_preferences WHERE consent_tips),
              (SELECT count(*)::int FROM care.contact_preferences WHERE telegram_chat_id IS NOT NULL)
            FROM care.messages WHERE facility_id = @f AND created_at > now() - interval '30 days'
            """,
            r => (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.IsDBNull(5) ? (double?)null : Math.Round(r.GetDouble(5), 2), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10)),
            ct, ("f", actor.FacilityId)))[0];
        var byKind = await session.QueryAsync(
            "SELECT kind, count(*) FILTER (WHERE status = 'Sent')::int, count(*) FILTER (WHERE status = 'Failed')::int FROM care.messages WHERE facility_id = @f AND created_at > now() - interval '30 days' GROUP BY kind ORDER BY kind",
            r => new KindCount(r.GetString(0), r.GetInt32(1), r.GetInt32(2)), ct, ("f", actor.FacilityId));
        await session.CommitAsync(ct);
        return new CareSummaryDto(30, s.Item1, s.Item2, s.Item3, s.Item4, s.Item5, s.Item6, s.Item7, s.Item8, s.Item9, s.Item10, s.Item11, channels.IsLive("Telegram"), false, byKind);
    }

    // ---------------- helpers ----------------
    private async Task<ContactDto> ContactAsync(Guid patientId, string registeredPhone, CancellationToken ct)
    {
        var history = await session.QueryAsync(
            "SELECT consent_reminders, consent_tips, method, recorded_at, consent_recalls FROM care.consent_events WHERE patient_id = @p ORDER BY recorded_at DESC LIMIT 20",
            r => new ConsentEventDto(r.GetBoolean(0), r.GetBoolean(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetBoolean(4)), ct, ("p", patientId));
        var rows = await session.QueryAsync(
            "SELECT sms_phone, telegram_chat_id IS NOT NULL, preferred_channel, language, consent_reminders, consent_tips, consent_method, updated_at, version, consent_recalls FROM care.contact_preferences WHERE patient_id = @p",
            r => new ContactDto(patientId, r.GetString(0), registeredPhone, r.GetBoolean(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.Str(6), r.Time(7), r.GetInt32(8), history, r.GetBoolean(9)),
            ct, ("p", patientId));
        return rows.Count == 1 ? rows[0] : new ContactDto(patientId, SocialRules.NormalisePhone(registeredPhone), registeredPhone, false, "None", "en", false, false, null, null, 0, history);
    }

    private async Task CancelQueuedAsync(Guid patientId, bool reminders, bool tips, bool recalls, CancellationToken ct) =>
        await session.ExecAsync(
            """
            UPDATE care.messages SET status = 'Cancelled', last_error = 'Consent withdrawn'
            WHERE patient_id = @p AND status = 'Scheduled' AND ((@rem AND kind NOT IN ('Tip', 'Recall')) OR (@tips AND kind = 'Tip') OR (@recalls AND kind = 'Recall'))
            """, ct, ("p", patientId), ("rem", reminders), ("tips", tips), ("recalls", recalls));

    private async Task<(string Name, string Phone)> ClinicAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var rows = await session.QueryAsync("SELECT clinic_name, clinic_phone FROM configuration.v_clinic_identity WHERE facility_id = @f",
            r => (r.GetString(0), r.GetString(1)), ct, ("f", actor.FacilityId));
        return rows.Count == 1 ? rows[0] : ("Clinic", "");
    }

    private static Error? ValidateTip(TipRequest r)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (r.Title?.Trim().Length is not (>= 3 and <= 120))
        {
            errors["title"] = ["3–120 characters."];
        }

        if (r.Body?.Trim().Length is not (>= 20 and <= 600))
        {
            errors["body"] = ["20–600 characters."];
        }

        if (r.Topic?.Trim().Length is not (>= 2 and <= 60))
        {
            errors["topic"] = ["2–60 characters."];
        }

        if ((r.Language ?? "en") is not ("en" or "am" or "ti"))
        {
            errors["language"] = ["English, Amharic or Tigrinya."];
        }

        return errors.Count == 0 ? null : Error.Validation("invalid_tip", "Some fields need attention.", errors);
    }

    internal static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(8);
        foreach (var b in bytes)
        {
            sb.Append(alphabet[b % alphabet.Length]);
        }

        return sb.ToString();
    }

    internal static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));
}

/// <summary>Records a reply, applies triage (task for a person, or opt-out) — shared by staff entry, simulation and webhooks.</summary>
internal static class CareReplies
{
    public static async Task<ReplyDto> RecordAsync(DbSession session, Guid tenant, Guid facility, Guid patient, Guid? messageId, string channel, string text, string? externalId, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        var triage = ReplyRules.Read(text);
        var id = Guid.CreateVersion7();
        await session.ExecAsync(
            """
            INSERT INTO care.replies (id, tenant_id, patient_id, message_id, channel, text, rating, needs_attention, opt_out, received_at, external_id)
            VALUES (@id, @t, @p, @m, @c, @x, @r, @n, @o, @now, @e)
            """, ct, ("id", id), ("t", tenant), ("p", patient), ("m", messageId), ("c", channel), ("x", text), ("r", triage.Rating), ("n", triage.NeedsAttention), ("o", triage.OptOut), ("now", now), ("e", externalId));
        if (triage.OptOut)
        {
            await session.ExecAsync(
                "UPDATE care.contact_preferences SET consent_reminders = false, consent_tips = false, consent_recalls = false, updated_by = @a, updated_at = @now, version = version + 1 WHERE patient_id = @p",
                ct, ("p", patient), ("a", actor), ("now", now));
            await session.ExecAsync(
                "INSERT INTO care.consent_events (id, tenant_id, patient_id, consent_reminders, consent_tips, consent_recalls, method, recorded_by, recorded_at) VALUES (@id, @t, @p, false, false, false, 'OptOutReply', @a, @now)",
                ct, ("id", Guid.CreateVersion7()), ("t", tenant), ("p", patient), ("a", actor), ("now", now));
            await session.ExecAsync("UPDATE care.messages SET status = 'Cancelled', last_error = 'Patient opted out' WHERE patient_id = @p AND status = 'Scheduled'", ct, ("p", patient));
        }
        else if (triage.NeedsAttention)
        {
            var urgent = triage.Reason.StartsWith("Mentions", StringComparison.Ordinal) || triage.Rating is 1;
            await session.ExecAsync(
                """
                INSERT INTO care.tasks (id, tenant_id, facility_id, patient_id, source, reply_id, priority, summary, status, due_at, created_at)
                VALUES (@id, @t, @f, @p, 'Reply', @r, @prio, @s, 'Open', @due, @now)
                """, ct, ("id", Guid.CreateVersion7()), ("t", tenant), ("f", facility), ("p", patient), ("r", id), ("prio", urgent ? "High" : "Normal"),
                ("s", triage.Reason), ("due", urgent ? now : now.AddHours(4)), ("now", now));
        }

        return new ReplyDto(id, patient, "", "", channel, text, triage.Rating, triage.NeedsAttention, triage.OptOut, now);
    }
}
