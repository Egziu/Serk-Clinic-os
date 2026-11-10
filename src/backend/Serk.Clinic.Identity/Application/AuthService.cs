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
    Guid? PractitionerId,
    bool IsPlatformAdmin,
    bool MustChangePassword);

public sealed record LoginOutcome(string SessionToken, SessionDto Session);

/// <summary>Validated session used by the authentication handler.</summary>
/// <summary>
/// Validated session used by the authentication handler. While <see cref="MustChangePassword"/> is set, the session carries
/// no actor and no platform role: only the session, password-change and logout endpoints work.
/// </summary>
public sealed record ValidatedSession(Guid SessionId, Guid UserId, string CsrfToken, StaffActor? Actor, bool IsPlatformAdmin, bool MustChangePassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

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
            // Single atomic statement: concurrent failures cannot lose increments and slip past the lockout threshold.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE identity.staff_users
                SET locked_until = CASE WHEN failed_login_count + 1 >= {SessionPolicy.MaxFailedLogins} THEN {now + SessionPolicy.LockoutDuration} ELSE locked_until END,
                    failed_login_count = CASE WHEN failed_login_count + 1 >= {SessionPolicy.MaxFailedLogins} THEN 0 ELSE failed_login_count + 1 END
                WHERE id = {user.Id}
                """,
                ct);
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
        if (session is null || session.RevokedAt is not null || session.AbsoluteExpiresAt <= now)
        {
            return null;
        }

        var user = await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Id == session.UserId, ct);
        var isPlatformAdmin = await IsActivePlatformAdminAsync(user.Id, ct);
        var idle = isPlatformAdmin ? SessionPolicy.PlatformAdminIdleTimeout : SessionPolicy.IdleTimeout;
        if (!user.IsActive || now - session.LastSeenAt > idle)
        {
            return null;
        }

        if (now - session.LastSeenAt > SessionPolicy.TouchInterval)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }

        return await BuildSessionAsync(session.Id, session.CsrfToken, session.ActiveAffiliationId, user, isPlatformAdmin, now, ct);
    }

    /// <summary>
    /// Resolves the request identity: the actor for the selected affiliation (effective capabilities from built-in and
    /// custom roles plus per-assignment overrides) and the platform-administrator flag. Both are withheld while a password
    /// change is pending.
    /// </summary>
    private async Task<ValidatedSession> BuildSessionAsync(Guid sessionId, string csrf, Guid? affiliationId, StaffUserRow user, bool isPlatformAdmin, DateTimeOffset now, CancellationToken ct)
    {
        if (user.MustChangePassword)
        {
            return new ValidatedSession(sessionId, user.Id, csrf, null, false, true);
        }

        StaffActor? actor = null;
        if (affiliationId is { } affId)
        {
            var affiliation = await db.Affiliations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == affId && a.UserId == user.Id, ct);
            if (affiliation is not null && IsAffiliationActive(affiliation, now))
            {
                var practitioner = await db.Practitioners.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.UserId == user.Id && p.TenantId == affiliation.TenantId, ct);
                actor = new StaffActor(
                    user.Id,
                    sessionId,
                    affiliation.Id,
                    affiliation.TenantId,
                    affiliation.FacilityId,
                    practitioner?.Id,
                    user.DisplayName,
                    await EffectiveCapabilitiesAsync(affiliation, ct));
            }
        }

        return new ValidatedSession(sessionId, user.Id, csrf, actor, isPlatformAdmin, false);
    }

    /// <summary>Effective capabilities of an assignment: (built-in ∪ active custom roles of the same tenant ∪ granted) − revoked.</summary>
    internal async Task<IReadOnlySet<string>> EffectiveCapabilitiesAsync(AffiliationRow affiliation, CancellationToken ct)
    {
        var customCodes = affiliation.Roles.Where(r => !RoleCatalog.IsKnownRole(r)).ToArray();
        var custom = customCodes.Length == 0
            ? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            : await db.RoleDefinitions.AsNoTracking()
                .Where(r => r.TenantId == affiliation.TenantId && r.IsActive && customCodes.Contains(r.Code))
                .ToDictionaryAsync(r => r.Code, r => (IReadOnlyCollection<string>)r.Capabilities, StringComparer.Ordinal, ct);
        return AccessPolicy.Effective(affiliation.Roles, custom, affiliation.GrantedCapabilities, affiliation.RevokedCapabilities);
    }

    private Task<bool> IsActivePlatformAdminAsync(Guid userId, CancellationToken ct) =>
        db.PlatformAdmins.AsNoTracking().AnyAsync(p => p.UserId == userId && p.IsActive, ct);

    /// <summary>
    /// Changes the signed-in user's own password (also completes a forced change). Requires the current password, applies
    /// the password policy, clears the must-change flag and revokes the user's OTHER sessions.
    /// </summary>
    public async Task<Result<SessionDto>> ChangeOwnPasswordAsync(ValidatedSession current, ChangePasswordRequest body, CancellationToken ct)
    {
        var user = await db.StaffUsers.SingleAsync(u => u.Id == current.UserId, ct);
        if (string.IsNullOrEmpty(body.CurrentPassword) || body.CurrentPassword.Length > 256
            || Hasher.VerifyHashedPassword(user, user.PasswordHash, body.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            await SecurityEventAsync("password.change_failed", "wrong_current_password", user.Id, null, ct);
            return Error.Validation("invalid_current_password", "The current password is not correct.");
        }

        var policy = AccessPolicy.ValidateNewPassword(body.NewPassword, user.Username);
        if (!policy.IsSuccess)
        {
            return policy.Error!;
        }

        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, body.NewPassword!) != PasswordVerificationResult.Failed)
        {
            return Error.Validation("password_reused", "Choose a password different from the current one.");
        }

        var now = clock.GetUtcNow();
        user.PasswordHash = HashPassword(body.NewPassword!);
        user.MustChangePassword = false;
        user.PasswordChangedAt = now;
        var others = await db.Sessions.Where(s => s.UserId == user.Id && s.Id != current.SessionId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var other in others)
        {
            other.RevokedAt = now;
            other.RevokedReason = "password_changed";
        }

        await db.SaveChangesAsync(ct);
        await SecurityEventAsync("password.changed", "success", user.Id, null, ct);
        var refreshed = await ValidateByIdAsync(current.SessionId, ct);
        return await DescribeAsync(refreshed, ct);
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
        var sharesTenant = await db.Affiliations.AnyAsync(a => a.UserId == userId && a.TenantId == admin.TenantId && a.IsActive, ct);
        if (!sharesTenant)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        // Scope: only sessions working in the administrator's tenant (or not yet bound to any facility).
        var tenantAffiliations = db.Affiliations.Where(a => a.TenantId == admin.TenantId).Select(a => a.Id);
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (s.ActiveAffiliationId == null || tenantAffiliations.Contains(s.ActiveAffiliationId.Value)))
            .ToListAsync(ct);
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
            session.Actor?.PractitionerId,
            session.IsPlatformAdmin,
            session.MustChangePassword);
    }

    private async Task<ValidatedSession> ValidateByIdAsync(Guid sessionId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var row = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, ct);
        var user = await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Id == row.UserId, ct);
        var isPlatformAdmin = await IsActivePlatformAdminAsync(user.Id, ct);
        return await BuildSessionAsync(row.Id, row.CsrfToken, row.ActiveAffiliationId, user, isPlatformAdmin, clock.GetUtcNow(), ct);
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
