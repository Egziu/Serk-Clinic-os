using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Identity.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Application;

public sealed record AffiliationDto(Guid Id, Guid TenantId, string TenantName, Guid FacilityId, string FacilityName, IReadOnlyList<string> Roles);

public sealed record SessionDto(
    Guid UserId,
    string DisplayName,
    string CsrfToken,
    Guid? ActiveAffiliationId,
    IReadOnlyList<AffiliationDto> Affiliations,
    IReadOnlyList<string> Capabilities,
    Guid? PractitionerId);

public sealed record LoginOutcome(string SessionToken, SessionDto Session);

/// <summary>Validated session used by the authentication handler.</summary>
public sealed record ValidatedSession(Guid SessionId, Guid UserId, string CsrfToken, StaffActor? Actor);

/// <summary>
/// Development/pilot credential login with lockout and revocable server-side sessions.
/// PRODUCTION NOTE: ADR-0004 requires federation to an OIDC identity provider with MFA for privileged roles before any
/// real-patient deployment; this local provider is for synthetic-data environments.
/// </summary>
public sealed class AuthService(
    IdentityDbContext db,
    TimeProvider clock,
    NpgsqlDataSource dataSource,
    RequestContext request)
{
    private static readonly PasswordHasher<StaffUserRow> Hasher = new();

    // Pre-computed hash used to equalize timing when the username does not exist.
    private static readonly string DummyHash = Hasher.HashPassword(new StaffUserRow(), "serk-dummy-password-for-timing");

    public static string HashPassword(string password) => Hasher.HashPassword(new StaffUserRow(), password);

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<Result<LoginOutcome>> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        var failure = Error.Validation("invalid_credentials", "Invalid username or password, or the account is temporarily locked.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || username.Length > 64 || password.Length > 256)
        {
            return failure;
        }

        var normalized = username.Trim().ToLowerInvariant();
        var hint = HashToken(normalized)[..16];
        var now = clock.GetUtcNow();
        var user = await db.StaffUsers.SingleOrDefaultAsync(u => u.Username == normalized, ct);
        if (user is null)
        {
            Hasher.VerifyHashedPassword(new StaffUserRow(), DummyHash, password);
            await SecurityEventAsync("login.failed", "unknown_user", null, hint, ct);
            return failure;
        }

        if (!user.IsActive || (user.LockedUntil is { } locked && locked > now))
        {
            Hasher.VerifyHashedPassword(user, DummyHash, password);
            await SecurityEventAsync("login.failed", user.IsActive ? "locked" : "inactive", user.Id, hint, ct);
            return failure;
        }

        var verification = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= SessionPolicy.MaxFailedLogins)
            {
                user.LockedUntil = now + SessionPolicy.LockoutDuration;
                user.FailedLoginCount = 0;
            }

            await db.SaveChangesAsync(ct);
            await SecurityEventAsync("login.failed", "bad_password", user.Id, hint, ct);
            return failure;
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var token = RandomToken();
        var affiliations = await ActiveAffiliationsAsync(user.Id, now, ct);
        var session = new SessionRow
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = HashToken(token),
            CsrfToken = RandomToken(),
            ActiveAffiliationId = affiliations.Count == 1 ? affiliations[0].Id : null,
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = now + SessionPolicy.AbsoluteLifetime,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        await SecurityEventAsync("login.succeeded", "success", user.Id, hint, ct);

        var validated = await ValidateAsync(token, ct);
        return new LoginOutcome(token, await DescribeAsync(validated!, ct));
    }

    public async Task<ValidatedSession?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var hash = HashToken(token);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null || session.RevokedAt is not null || session.AbsoluteExpiresAt <= now || now - session.LastSeenAt > SessionPolicy.IdleTimeout)
        {
            return null;
        }

        var user = await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Id == session.UserId, ct);
        if (!user.IsActive)
        {
            return null;
        }

        if (now - session.LastSeenAt > SessionPolicy.TouchInterval)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }

        StaffActor? actor = null;
        if (session.ActiveAffiliationId is { } affiliationId)
        {
            var affiliation = await db.Affiliations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == affiliationId, ct);
            if (affiliation is not null && IsAffiliationActive(affiliation, now))
            {
                var practitioner = await db.Practitioners.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.UserId == user.Id && p.TenantId == affiliation.TenantId, ct);
                actor = new StaffActor(
                    user.Id,
                    session.Id,
                    affiliation.Id,
                    affiliation.TenantId,
                    affiliation.FacilityId,
                    practitioner?.Id,
                    user.DisplayName,
                    RoleCatalog.CapabilitiesFor(affiliation.Roles));
            }
        }

        return new ValidatedSession(session.Id, user.Id, session.CsrfToken, actor);
    }

    public async Task<Result<SessionDto>> SelectAffiliationAsync(ValidatedSession current, Guid affiliationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var affiliation = await db.Affiliations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == affiliationId && a.UserId == current.UserId, ct);
        if (affiliation is null || !IsAffiliationActive(affiliation, now))
        {
            return Error.NotFound("affiliation_not_found", "The affiliation is not available to this account.");
        }

        var session = await db.Sessions.SingleAsync(s => s.Id == current.SessionId, ct);
        session.ActiveAffiliationId = affiliation.Id;
        await db.SaveChangesAsync(ct);
        var refreshed = await ValidateByIdAsync(current.SessionId, ct);
        return await DescribeAsync(refreshed, ct);
    }

    public async Task RevokeAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is { RevokedAt: null })
        {
            session.RevokedAt = clock.GetUtcNow();
            session.RevokedReason = reason;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Revokes every live session of a staff member who is affiliated with the administrator's tenant.</summary>
    public async Task<Result<int>> RevokeAllForUserAsync(StaffActor admin, Guid userId, CancellationToken ct)
    {
        var sharesTenant = await db.Affiliations.AnyAsync(a => a.UserId == userId && a.TenantId == admin.TenantId, ct);
        if (!sharesTenant)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var s in sessions)
        {
            s.RevokedAt = now;
            s.RevokedReason = "admin_revoked";
        }

        await db.SaveChangesAsync(ct);
        await SecurityEventAsync("sessions.revoked_by_admin", "success", userId, null, ct);
        return sessions.Count;
    }

    public async Task<SessionDto> DescribeAsync(ValidatedSession session, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Id == session.UserId, ct);
        var affiliations = await ActiveAffiliationsAsync(session.UserId, now, ct);
        return new SessionDto(
            session.UserId,
            user.DisplayName,
            session.CsrfToken,
            session.Actor?.AffiliationId,
            affiliations,
            session.Actor?.Capabilities.Order(StringComparer.Ordinal).ToList() ?? [],
            session.Actor?.PractitionerId);
    }

    private async Task<ValidatedSession> ValidateByIdAsync(Guid sessionId, CancellationToken ct)
    {
        var row = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, ct);
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();
        var user = await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Id == row.UserId, ct);
        StaffActor? actor = null;
        if (row.ActiveAffiliationId is { } affId)
        {
            var aff = await db.Affiliations.AsNoTracking().SingleAsync(a => a.Id == affId, ct);
            if (IsAffiliationActive(aff, now))
            {
                var practitioner = await db.Practitioners.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == user.Id && p.TenantId == aff.TenantId, ct);
                actor = new StaffActor(user.Id, row.Id, aff.Id, aff.TenantId, aff.FacilityId, practitioner?.Id, user.DisplayName, RoleCatalog.CapabilitiesFor(aff.Roles));
            }
        }

        return new ValidatedSession(row.Id, row.UserId, row.CsrfToken, actor);
    }

    private async Task<List<AffiliationDto>> ActiveAffiliationsAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await (
            from a in db.Affiliations.AsNoTracking()
            join f in db.Facilities.AsNoTracking() on a.FacilityId equals f.Id
            join t in db.Tenants.AsNoTracking() on a.TenantId equals t.Id
            where a.UserId == userId && a.IsActive && a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now)
            orderby t.Name, f.Name
            select new AffiliationDto(a.Id, t.Id, t.Name, f.Id, f.Name, a.Roles)).ToListAsync(ct);
        return rows;
    }

    private static bool IsAffiliationActive(AffiliationRow a, DateTimeOffset now) =>
        a.IsActive && a.ValidFrom <= now && (a.ValidUntil is null || a.ValidUntil > now);

    private static string RandomToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task SecurityEventAsync(string type, string outcome, Guid? userId, string? hint, CancellationToken ct) =>
        AuditTrail.WriteSecurityEventAsync(dataSource, clock, type, outcome, userId, null, "staff_user", userId, request.CorrelationId, hint, ct);
}
