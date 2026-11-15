using System.Text.RegularExpressions;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Application;

public sealed record NoticeDto(Guid Id, string Title, string Body, string Priority, IReadOnlyList<string> AudienceRoles, bool AllFacilities, bool Pinned, bool RequiresAck,
    DateTimeOffset? ExpiresAt, string Source, string AuthorName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool Read, bool Acknowledged, bool CanArchive,
    int Acknowledgements, int ReadCount);

public sealed record CreateNoticeRequest(string? Title, string? Body, string? Priority, IReadOnlyList<string>? AudienceRoles, bool AllFacilities, bool Pinned, bool RequiresAck, DateTimeOffset? ExpiresAt);

public sealed record NoticeReceiptDto(string Name, DateTimeOffset ReadAt, DateTimeOffset? AcknowledgedAt);

public sealed record NoticeReceiptsDto(int AudienceSize, IReadOnlyList<NoticeReceiptDto> Receipts, IReadOnlyList<string> NotYetAcknowledged);

/// <summary>
/// Staff notice board: announcements by priority and audience (roles, facility), pinning, expiry, read receipts and
/// required acknowledgements. Never for patient information: text that looks like an MRN or a phone number is refused.
/// Automation posts its own system notices (urgent call-backs, overdue recalls, missing daily close, stale backup).
/// </summary>
public sealed partial class NoticeService(DbSession session, RequestContext context, AuditTrail audit, TimeProvider clock)
{
    private static readonly string[] Priorities = ["Normal", "Important", "Urgent"];
    private static readonly string[] Roles = ["Receptionist", "Nurse", "Clinician", "Dentist", "Cashier", "FinanceSupervisor", "ClinicAdmin", "Owner"];

    private const string Visible =
        """
        n.status = 'Active' AND (n.expires_at IS NULL OR n.expires_at > now())
        AND (n.facility_id IS NULL OR n.facility_id = @f)
        AND (n.author_id = @u OR cardinality(n.audience_roles) = 0 OR n.audience_roles && coalesce((SELECT roles FROM identity.v_assignment_roles WHERE affiliation_id = @aff), '{}'))
        """;

    public async Task<IReadOnlyList<NoticeDto>> ListAsync(bool includeArchived, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var manage = actor.Has(Capabilities.NoticeManage);
        var where = includeArchived && manage ? "(n.facility_id IS NULL OR n.facility_id = @f) AND n.created_at > now() - interval '90 days'" : Visible;
        var list = await session.QueryAsync(
            $"""
            SELECT n.id, n.title, n.body, n.priority, n.audience_roles, n.facility_id IS NULL, n.pinned, n.requires_ack, n.expires_at, n.source,
                   coalesce(s.display_name, CASE n.source WHEN 'System' THEN 'SERK automation' ELSE '' END), n.created_at, n.updated_at,
                   r.read_at IS NOT NULL, r.acknowledged_at IS NOT NULL, n.author_id, n.status,
                   (SELECT count(*)::int FROM comms.notice_reads x WHERE x.notice_id = n.id AND x.acknowledged_at IS NOT NULL),
                   (SELECT count(*)::int FROM comms.notice_reads x WHERE x.notice_id = n.id)
            FROM comms.notices n
            LEFT JOIN comms.notice_reads r ON r.notice_id = n.id AND r.user_id = @u
            LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = n.author_id
            WHERE {where}
            ORDER BY n.status, n.pinned DESC, CASE n.priority WHEN 'Urgent' THEN 0 WHEN 'Important' THEN 1 ELSE 2 END, n.updated_at DESC
            LIMIT 200
            """,
            r => new NoticeDto(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7),
                r.Time(8), r.GetString(9), r.GetString(10), r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<DateTimeOffset>(12), r.GetBoolean(13), r.GetBoolean(14),
                r.GetString(16) == "Active" && (manage || r.Id(15) == actor.UserId), r.GetInt32(17), r.GetInt32(18)),
            ct, ("f", actor.FacilityId), ("aff", actor.AffiliationId), ("u", actor.UserId));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var n = await session.ScalarAsync<long>(
            $"SELECT count(*) FROM comms.notices n WHERE {Visible} AND NOT EXISTS (SELECT 1 FROM comms.notice_reads r WHERE r.notice_id = n.id AND r.user_id = @u AND (r.acknowledged_at IS NOT NULL OR NOT n.requires_ack))",
            ct, ("f", actor.FacilityId), ("aff", actor.AffiliationId), ("u", actor.UserId));
        await session.CommitAsync(ct);
        return (int)n;
    }

    public async Task<Result<Guid>> CreateAsync(CreateNoticeRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var manage = actor.Has(Capabilities.NoticeManage);
        var title = (r.Title ?? "").Trim();
        var body = (r.Body ?? "").Trim();
        var priority = r.Priority ?? "Normal";
        var roles = (r.AudienceRoles ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (title.Length is < 3 or > 120)
        {
            errors["title"] = ["3–120 characters."];
        }

        if (body.Length is < 1 or > 4000)
        {
            errors["body"] = ["1–4,000 characters."];
        }

        if (!Priorities.Contains(priority))
        {
            errors["priority"] = ["Normal, Important or Urgent."];
        }

        if (roles.Any(x => !Roles.Contains(x)))
        {
            errors["audienceRoles"] = ["Unknown role."];
        }

        if (r.ExpiresAt is { } exp && (exp <= clock.GetUtcNow() || exp > clock.GetUtcNow().AddYears(1)))
        {
            errors["expiresAt"] = ["Between now and one year ahead."];
        }

        if (LooksLikePatientData(title + " " + body))
        {
            errors["body"] = ["Notices are seen by staff across the clinic — remove MRNs and phone numbers (and do not write patient names)."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("invalid_notice", "Some fields need attention.", errors);
        }

        if (!manage && (priority == "Urgent" || r.Pinned || r.RequiresAck || r.AllFacilities))
        {
            return Error.Forbidden("notice_manage_required", "Urgent, pinned, organisation-wide or must-acknowledge notices are posted by the owner or clinic administrator.");
        }

        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await session.ExecAsync(
            """
            INSERT INTO comms.notices (id, tenant_id, facility_id, title, body, priority, audience_roles, pinned, requires_ack, expires_at, source, author_id, status, created_at, updated_at)
            VALUES (@id, @t, @f, @ti, @b, @p, @roles, @pin, @ack, @exp, 'Staff', @u, 'Active', @now, @now)
            """, ct, ("id", id), ("t", actor.TenantId), ("f", r.AllFacilities ? null : actor.FacilityId), ("ti", title), ("b", body), ("p", priority), ("roles", roles),
            ("pin", r.Pinned), ("ack", r.RequiresAck), ("exp", r.ExpiresAt), ("u", actor.UserId), ("now", now));
        await session.ExecAsync("INSERT INTO comms.notice_reads (notice_id, user_id, tenant_id, read_at, acknowledged_at) VALUES (@n, @u, @t, @now, @now)",
            ct, ("n", id), ("u", actor.UserId), ("t", actor.TenantId), ("now", now));
        await audit.RecordAsync(new AuditEntry("notice.create", "notice", id, Metadata: new Dictionary<string, string> { ["priority"] = priority, ["requiresAck"] = r.RequiresAck.ToString() }), ct);
        await session.CommitAsync(ct);
        return id;
    }

    public async Task<Result<bool>> MarkAsync(Guid id, bool acknowledge, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var visible = await session.QueryAsync($"SELECT n.requires_ack FROM comms.notices n WHERE n.id = @id AND {Visible}", r => r.GetBoolean(0), ct,
            ("id", id), ("f", actor.FacilityId), ("aff", actor.AffiliationId), ("u", actor.UserId));
        if (visible.Count == 0)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        await session.ExecAsync(
            """
            INSERT INTO comms.notice_reads (notice_id, user_id, tenant_id, read_at, acknowledged_at) VALUES (@n, @u, @t, @now, CASE WHEN @ack THEN @now END)
            ON CONFLICT (notice_id, user_id) DO UPDATE SET acknowledged_at = coalesce(comms.notice_reads.acknowledged_at, EXCLUDED.acknowledged_at)
            """, ct, ("n", id), ("u", actor.UserId), ("t", actor.TenantId), ("now", now), ("ack", acknowledge && visible[0]));
        if (acknowledge && visible[0])
        {
            await audit.RecordAsync(new AuditEntry("notice.acknowledge", "notice", id), ct);
        }

        await session.CommitAsync(ct);
        return true;
    }

    public async Task<Result<NoticeReceiptsDto>> ReceiptsAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var n = await session.QueryAsync("SELECT author_id, facility_id, audience_roles FROM comms.notices WHERE id = @id AND (facility_id IS NULL OR facility_id = @f)",
            r => (Author: r.Id(0), Facility: r.Id(1), Roles: r.GetFieldValue<string[]>(2)), ct, ("id", id), ("f", actor.FacilityId));
        if (n.Count == 0 || (!actor.Has(Capabilities.NoticeManage) && n[0].Author != actor.UserId))
        {
            return Error.NotFound();
        }

        var audience = await session.QueryAsync(
            """
            SELECT DISTINCT a.user_id, coalesce(s.display_name, '') FROM identity.v_assignment_roles a
            LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = a.user_id
            WHERE a.is_active AND (@f::uuid IS NULL OR a.facility_id = @f) AND (cardinality(@roles::text[]) = 0 OR a.roles && @roles::text[])
            """, r => (Id: r.GetGuid(0), Name: r.GetString(1)), ct, ("f", n[0].Facility), ("roles", n[0].Roles));
        var receipts = await session.QueryAsync(
            """
            SELECT coalesce(s.display_name, ''), r.read_at, r.acknowledged_at, r.user_id FROM comms.notice_reads r
            LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = r.user_id
            WHERE r.notice_id = @id ORDER BY r.read_at
            """, r => (Dto: new NoticeReceiptDto(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.Time(2)), User: r.GetGuid(3)), ct, ("id", id));
        var acked = receipts.Where(x => x.Dto.AcknowledgedAt is not null).Select(x => x.User).ToHashSet();
        await session.CommitAsync(ct);
        return new NoticeReceiptsDto(audience.Count, receipts.Select(x => x.Dto).ToList(),
            audience.Where(a => !acked.Contains(a.Id)).Select(a => a.Name).Order(StringComparer.Ordinal).ToList());
    }

    public async Task<Result<bool>> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var n = await session.ExecAsync(
            "UPDATE comms.notices SET status = 'Archived', archived_by = @u, archived_at = @now, system_key = system_key || ':closed:' || id::text, version = version + 1 WHERE id = @id AND status = 'Active' AND (facility_id IS NULL OR facility_id = @f) AND (@manage OR author_id = @u)",
            ct, ("id", id), ("u", actor.UserId), ("now", clock.GetUtcNow()), ("manage", actor.Has(Capabilities.NoticeManage)), ("f", actor.FacilityId));
        if (n == 0)
        {
            return Error.NotFound();
        }

        await audit.RecordAsync(new AuditEntry("notice.archive", "notice", id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    /// <summary>Heuristic guard: MRNs and Ethiopian phone numbers are refused (names cannot be detected — the UI warns).</summary>
    public static bool LooksLikePatientData(string text) => MrnPattern().IsMatch(text) || PhonePattern().IsMatch(text);

    [GeneratedRegex(@"\bMRN[-\s]?\d{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex MrnPattern();

    [GeneratedRegex(@"(\+?251(?:[\s-]?\d){9})|(\b0[79](?:[\s-]?\d){8}\b)")]
    private static partial Regex PhonePattern();
}
