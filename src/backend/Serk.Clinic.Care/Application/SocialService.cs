using Serk.Clinic.Care.Domain;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Care.Application;

public sealed record SocialPostDto(Guid Id, string Platform, DateTimeOffset PlannedAt, string Title, string Caption, string Status, string Campaign,
    bool ShowsPatient, string? PatientConsentRef, string? PublishedUrl, DateTimeOffset? PublishedAt, int Version);

public sealed record SocialPostRequest(string? Platform, DateTimeOffset? PlannedAt, string? Title, string? Caption, string? Status, string? Campaign,
    bool ShowsPatient, string? PatientConsentRef, string? PublishedUrl);

public sealed record SocialMetricDto(Guid Id, string Platform, DateOnly MeasuredOn, int Followers, int Reach, int Engagements, int Inquiries, int Bookings, double? EngagementRate);

public sealed record SocialMetricRequest(string? Platform, DateOnly? MeasuredOn, int Followers, int Reach, int Engagements, int Inquiries, int Bookings);

public sealed record PlatformSummary(string Platform, int Followers, double? FollowerGrowth, int Reach30, int Engagements30, double? EngagementRate, int Inquiries30, int Bookings30, int Published30, int Planned14);

public sealed record SocialSummaryDto(IReadOnlyList<PlatformSummary> Platforms, int PlannedNext14, int PublishedLast30, int Ideas);

/// <summary>
/// Social media planner and tracker. Planning only: nothing is posted automatically to any platform. Results are entered
/// by staff (or imported later). A post that shows a patient must carry a written-consent reference (database rule).
/// </summary>
public sealed class SocialService(DbSession session, RequestContext context, AuditTrail audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<SocialPostDto>> PostsAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        context.RequireActor();
        var f = from ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-31);
        var t = to ?? f.AddDays(92);
        var list = await session.QueryAsync(
            """
            SELECT id, platform, planned_at, title, caption, status, campaign, shows_patient, patient_consent_ref, published_url, published_at, version
            FROM care.social_posts WHERE planned_at >= @f AND planned_at < @t ORDER BY planned_at
            """,
            r => new SocialPostDto(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetBoolean(7), r.Str(8), r.Str(9), r.Time(10), r.GetInt32(11)),
            ct, ("f", f.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("t", t.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<Guid>> SavePostAsync(Guid? id, SocialPostRequest r, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var errors = Validate(r);
        if (errors.Count > 0)
        {
            return Error.Validation("invalid_post", "Some fields need attention.", errors);
        }

        var now = clock.GetUtcNow();
        DateTimeOffset? publishedAt = r.Status == "Published" ? now : null;
        if (id is null)
        {
            var newId = Guid.CreateVersion7();
            await session.ExecAsync(
                """
                INSERT INTO care.social_posts (id, tenant_id, platform, planned_at, title, caption, status, campaign, owner_user_id, shows_patient, patient_consent_ref,
                  published_url, published_at, created_by, created_at)
                VALUES (@id, @t, @pl, @at, @ti, @c, @s, @cam, @u, @sp, @ref, @url, @pub, @u, @now)
                """, ct, ("id", newId), ("t", actor.TenantId), ("pl", r.Platform), ("at", r.PlannedAt!.Value), ("ti", r.Title!.Trim()), ("c", (r.Caption ?? "").Trim()),
                ("s", r.Status ?? "Idea"), ("cam", (r.Campaign ?? "").Trim()), ("u", actor.UserId), ("sp", r.ShowsPatient), ("ref", Clean(r.PatientConsentRef)),
                ("url", Clean(r.PublishedUrl)), ("pub", publishedAt), ("now", now));
            await audit.RecordAsync(new AuditEntry("social.post.create", "social_post", newId), ct);
            await session.CommitAsync(ct);
            return newId;
        }

        var n = await session.ExecAsync(
            """
            UPDATE care.social_posts SET platform = @pl, planned_at = @at, title = @ti, caption = @c, status = @s, campaign = @cam, shows_patient = @sp,
              patient_consent_ref = @ref, published_url = @url,
              published_at = CASE WHEN @s = 'Published' THEN coalesce(published_at, @now) ELSE NULL END, version = version + 1
            WHERE id = @id AND (@v::int IS NULL OR version = @v)
            """, ct, ("id", id.Value), ("pl", r.Platform), ("at", r.PlannedAt!.Value), ("ti", r.Title!.Trim()), ("c", (r.Caption ?? "").Trim()), ("s", r.Status ?? "Idea"),
            ("cam", (r.Campaign ?? "").Trim()), ("sp", r.ShowsPatient), ("ref", Clean(r.PatientConsentRef)), ("url", Clean(r.PublishedUrl)), ("now", now), ("v", ifMatch));
        if (n == 0)
        {
            return Error.Precondition("post_changed", "This post changed meanwhile. Reload and retry.");
        }

        await audit.RecordAsync(new AuditEntry("social.post.update", "social_post", id.Value), ct);
        await session.CommitAsync(ct);
        return id.Value;
    }

    public async Task<IReadOnlyList<SocialMetricDto>> MetricsAsync(int days, CancellationToken ct)
    {
        context.RequireActor();
        var list = await session.QueryAsync(
            "SELECT id, platform, measured_on, followers, reach, engagements, inquiries, bookings FROM care.social_metrics WHERE measured_on >= @since ORDER BY measured_on, platform",
            r => new SocialMetricDto(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                SocialRules.EngagementRate(r.GetInt32(5), r.GetInt32(4))),
            ct, ("since", DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-Math.Clamp(days, 7, 730))));
        await session.CommitAsync(ct);
        return list;
    }

    public async Task<Result<bool>> RecordMetricAsync(SocialMetricRequest r, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (r.Platform is null || !SocialRules.Platforms.Contains(r.Platform) || r.MeasuredOn is not { } day || day > today || day < today.AddYears(-3)
            || r.Followers < 0 || r.Reach < 0 || r.Engagements < 0 || r.Inquiries < 0 || r.Bookings < 0 || r.Followers > 100_000_000)
        {
            return Error.Validation("invalid_metric", "Choose a platform, a date not in the future, and non-negative numbers.");
        }

        await session.ExecAsync(
            """
            INSERT INTO care.social_metrics (id, tenant_id, platform, measured_on, followers, reach, engagements, inquiries, bookings, entered_by, entered_at)
            VALUES (@id, @t, @pl, @d, @fo, @re, @en, @in, @bo, @u, @now)
            ON CONFLICT (tenant_id, platform, measured_on) DO UPDATE SET followers = EXCLUDED.followers, reach = EXCLUDED.reach, engagements = EXCLUDED.engagements,
              inquiries = EXCLUDED.inquiries, bookings = EXCLUDED.bookings, entered_by = EXCLUDED.entered_by, entered_at = EXCLUDED.entered_at
            """, ct, ("id", Guid.CreateVersion7()), ("t", actor.TenantId), ("pl", r.Platform), ("d", day), ("fo", r.Followers), ("re", r.Reach), ("en", r.Engagements),
            ("in", r.Inquiries), ("bo", r.Bookings), ("u", actor.UserId), ("now", clock.GetUtcNow()));
        await audit.RecordAsync(new AuditEntry("social.metric.record", "social_metric", Guid.Empty, Metadata: new Dictionary<string, string> { ["platform"] = r.Platform, ["date"] = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<SocialSummaryDto> SummaryAsync(CancellationToken ct)
    {
        context.RequireActor();
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var metrics = await session.QueryAsync(
            "SELECT platform, measured_on, followers, reach, engagements, inquiries, bookings FROM care.social_metrics WHERE measured_on >= @since ORDER BY measured_on",
            r => (Platform: r.GetString(0), Day: r.GetFieldValue<DateOnly>(1), Followers: r.GetInt32(2), Reach: r.GetInt32(3), Eng: r.GetInt32(4), Inq: r.GetInt32(5), Book: r.GetInt32(6)),
            ct, ("since", today.AddDays(-60)));
        var posts = await session.QueryAsync(
            "SELECT platform, status, planned_at, published_at FROM care.social_posts WHERE planned_at > @a OR published_at > @a",
            r => (Platform: r.GetString(0), Status: r.GetString(1), Planned: r.GetFieldValue<DateTimeOffset>(2), Published: r.Time(3)), ct, ("a", now.AddDays(-30)));
        var platforms = metrics.Select(m => m.Platform).Concat(posts.Select(p => p.Platform)).Distinct().Order(StringComparer.Ordinal).Select(p =>
        {
            var series = metrics.Where(m => m.Platform == p).ToList();
            var recent = series.Where(m => m.Day > today.AddDays(-30)).ToList();
            var latest = series.LastOrDefault();
            var baseline = series.LastOrDefault(m => m.Day <= today.AddDays(-28));
            return new PlatformSummary(p, latest.Followers, SocialRules.Growth(baseline.Platform is null ? null : baseline.Followers, latest.Followers),
                recent.Sum(m => m.Reach), recent.Sum(m => m.Eng), SocialRules.EngagementRate(recent.Sum(m => m.Eng), recent.Sum(m => m.Reach)),
                recent.Sum(m => m.Inq), recent.Sum(m => m.Book),
                posts.Count(x => x.Platform == p && x.Published > now.AddDays(-30)),
                posts.Count(x => x.Platform == p && x.Status is "Scheduled" or "Draft" && x.Planned >= now && x.Planned < now.AddDays(14)));
        }).ToList();
        var dto = new SocialSummaryDto(platforms,
            posts.Count(x => x.Status is "Scheduled" or "Draft" && x.Planned >= now && x.Planned < now.AddDays(14)),
            posts.Count(x => x.Published > now.AddDays(-30)),
            await session.ScalarAsync<long>("SELECT count(*) FROM care.social_posts WHERE status = 'Idea'", ct) is var ideas ? (int)ideas : 0);
        await session.CommitAsync(ct);
        return dto;
    }

    private static Dictionary<string, string[]> Validate(SocialPostRequest r)
    {
        var e = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (r.Platform is null || !SocialRules.Platforms.Contains(r.Platform))
        {
            e["platform"] = ["Choose a platform."];
        }

        if (r.PlannedAt is null)
        {
            e["plannedAt"] = ["Choose a date and time."];
        }

        if (r.Title?.Trim().Length is not (>= 3 and <= 120))
        {
            e["title"] = ["3–120 characters."];
        }

        if ((r.Caption ?? "").Length > 2200)
        {
            e["caption"] = ["At most 2,200 characters."];
        }

        if (r.Status is not null && !SocialRules.PostStatuses.Contains(r.Status))
        {
            e["status"] = ["Unknown status."];
        }

        if (r.ShowsPatient && Clean(r.PatientConsentRef) is null)
        {
            e["patientConsentRef"] = ["A post showing a patient needs the reference of their signed media consent."];
        }

        if (Clean(r.PublishedUrl) is { } url && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"))
        {
            e["publishedUrl"] = ["Use the full https:// link of the published post."];
        }

        return e;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
