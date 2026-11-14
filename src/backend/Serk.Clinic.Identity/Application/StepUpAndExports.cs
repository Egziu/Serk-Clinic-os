using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Identity.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Export;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Application;

/// <summary>
/// Password re-confirmation for sensitive actions (patient-data export, backups). Uses its own connection so a failed
/// attempt is counted even though the business transaction is abandoned; at the lockout limit the account is locked and
/// all of its sessions end, so a stolen session cannot guess the password.
/// </summary>
public sealed class StepUpVerifier(IdentityDbContext db, TimeProvider clock, NpgsqlDataSource dataSource, RequestContext request) : IStepUpVerifier
{
    private static readonly PasswordHasher<StaffUserRow> Hasher = new();

    public async Task<bool> VerifyAsync(Guid userId, string? password, string purpose, CancellationToken ct)
    {
        var user = await db.StaffUsers.SingleAsync(u => u.Id == userId, ct);
        var ok = !string.IsNullOrEmpty(password) && password.Length <= 256
            && Hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
        if (ok)
        {
            if (user.FailedLoginCount != 0)
            {
                user.FailedLoginCount = 0;
                await db.SaveChangesAsync(ct);
            }

            return true;
        }

        // Atomic increment: parallel wrong guesses from one stolen session cannot overwrite each other's count.
        db.ChangeTracker.Clear();
        var count = (await db.Database.SqlQuery<int>(
            $"UPDATE identity.staff_users SET failed_login_count = failed_login_count + 1 WHERE id = {userId} RETURNING failed_login_count AS \"Value\"").ToListAsync(ct)).Single();
        var locked = count >= SessionPolicy.MaxFailedLogins;
        if (locked)
        {
            var now = clock.GetUtcNow();
            await db.StaffUsers.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LockedUntil, now + SessionPolicy.LockoutDuration), ct);
            await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, "step_up_lockout"), ct);
        }

        await AuditTrail.WriteSecurityEventAsync(dataSource, clock, locked ? "step_up.lockout" : "step_up.failed", purpose.Length > 60 ? purpose[..60] : purpose,
            userId, request.Actor?.TenantId, "staff_user", userId, request.CorrelationId, null, ct);
        return false;
    }
}

public sealed class FacilityTimeZones(IdentityDbContext db) : IFacilityTimeZones
{
    public async Task<string> GetAsync(Guid tenantId, Guid facilityId, CancellationToken ct) =>
        await db.Facilities.AsNoTracking().Where(f => f.Id == facilityId && f.TenantId == tenantId).Select(f => f.TimeZone).SingleOrDefaultAsync(ct)
        ?? "Africa/Addis_Ababa";
}

public static class IdentityExports
{
    public static readonly ExportDefinition StaffAccess = new(
        "staff.access", "People", "Staff and access",
        "Every assignment in this organisation: person, facility, roles, added and removed permissions, validity, last sign-in.",
        [Capabilities.StaffAdmin], ContainsPatientData: false, UsesDateRange: false,
        ["username", "display_name", "account_active", "facility", "roles", "added_permissions", "removed_permissions", "assignment_active", "valid_from", "valid_until", "last_login_at"],
        """
        SELECT u.username, u.display_name, u.is_active, f.name, a.roles, a.granted_capabilities, a.revoked_capabilities, a.is_active,
               a.valid_from, a.valid_until, u.last_login_at
        FROM identity.affiliations a
        JOIN identity.staff_users u ON u.id = a.user_id
        JOIN identity.facilities f ON f.id = a.facility_id
        WHERE a.tenant_id = @t
        ORDER BY u.display_name, f.name LIMIT @limit
        """);
}
