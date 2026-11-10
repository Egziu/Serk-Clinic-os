using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Identity.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Application;

// ---------- DTOs ----------
public sealed record FacilityAdminDto(Guid Id, Guid TenantId, string Code, string Name, string AddressLine, string Phone, string LicenseNumber, string TimeZone);

public sealed record TenantAdminDto(Guid Id, string Name, DateTimeOffset CreatedAt, IReadOnlyList<FacilityAdminDto> Facilities, int StaffCount, int CustomRoleCount);

public sealed record CreateTenantRequest(string? Name, string? FacilityCode, string? FacilityName, string? TimeZone);

public sealed record RenameTenantRequest(string? Name);

public sealed record FacilityRequest(Guid TenantId, string? Code, string? Name, string? AddressLine, string? Phone, string? LicenseNumber, string? TimeZone);

public sealed record AssignmentAdminDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    Guid FacilityId,
    string FacilityName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> GrantedCapabilities,
    IReadOnlyList<string> RevokedCapabilities,
    IReadOnlyList<string> EffectiveCapabilities,
    bool IsActive,
    bool IsCurrentlyValid,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    int Version);

public sealed record PractitionerProfileDto(Guid Id, Guid TenantId, string TenantName, string ProfessionalTitle, string Specialty, string LicenseNumber, DateOnly LicenseExpiresOn);

public sealed record StaffSummaryDto(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsActive,
    bool IsLocked,
    bool MustChangePassword,
    bool IsPlatformAdmin,
    DateTimeOffset? LastLoginAt,
    int ActiveAssignments,
    IReadOnlyList<string> Organisations);

public sealed record StaffDetailDto(
    StaffSummaryDto Summary,
    IReadOnlyList<AssignmentAdminDto> Assignments,
    IReadOnlyList<PractitionerProfileDto> PractitionerProfiles,
    int ActiveSessions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PasswordChangedAt);

public sealed record CreateStaffRequest(string? Username, string? DisplayName, string? TemporaryPassword);

public sealed record UpdateStaffRequest(string? DisplayName);

public sealed record SetActiveRequest(bool IsActive, string? Reason);

public sealed record ResetPasswordRequest(string? TemporaryPassword);

public sealed record PractitionerProfileRequest(Guid TenantId, string? ProfessionalTitle, string? Specialty, string? LicenseNumber, DateOnly? LicenseExpiresOn);

public sealed record AssignmentRequest(
    Guid FacilityId,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<string>? GrantedCapabilities,
    IReadOnlyList<string>? RevokedCapabilities,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    bool? IsActive,
    int? Version,
    string? Reason);

public sealed record RoleAdminDto(
    Guid? Id,
    Guid? TenantId,
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string> Capabilities,
    bool IsBuiltIn,
    bool IsActive,
    int Version,
    int AssignedCount);

public sealed record CreateRoleRequest(Guid TenantId, string? Name, string? Description, IReadOnlyList<string>? Capabilities);

public sealed record UpdateRoleRequest(string? Name, string? Description, IReadOnlyList<string>? Capabilities, bool IsActive, int Version);

public sealed record PreviewRequest(Guid TenantId, Guid? UserId, IReadOnlyList<string>? Roles, IReadOnlyList<string>? GrantedCapabilities, IReadOnlyList<string>? RevokedCapabilities);

public sealed record PreviewDto(IReadOnlyList<CapabilityInfo> Effective, IReadOnlyDictionary<string, int> BySensitivity, IReadOnlyList<string> Warnings);

public sealed record PlatformAdminDto(Guid UserId, string Username, string DisplayName, bool IsActive, DateTimeOffset GrantedAt, string? GrantedBy, DateTimeOffset? RevokedAt);

public sealed record PlatformAdminRequest(Guid UserId, string? ConfirmPassword, string? Reason);

public sealed record AdminChangeDto(Guid Id, DateTimeOffset OccurredAt, string Actor, string Action, string TargetType, Guid? TargetId, Guid? TenantId, string? Before, string? After, string? Reason);

public sealed record PlatformOverviewDto(int Organisations, int Facilities, int Staff, int ActiveStaff, int LockedAccounts, int PendingPasswordChanges, int PlatformAdmins, int CustomRoles, int ActiveAssignments, int ChangesLast7Days);

public sealed record CatalogDto(IReadOnlyList<CapabilityInfo> Capabilities, IReadOnlyList<RoleAdminDto> BuiltInRoles);

/// <summary>
/// Platform super-administration. Every operation is authorised by <c>RequirePlatformAdmin</c> at the endpoint and here
/// re-checked for the invariants that keep the super administrator from becoming an unchecked superuser:
/// no self-modification of access, never fewer than one active super administrator, step-up password for super-admin
/// grants/revocations, no patient data in any response, and an append-only before/after change record written in the
/// same transaction as the change itself.
/// </summary>
public sealed class PlatformAdminService(IdentityDbContext db, AuthService auth, TimeProvider clock, NpgsqlDataSource dataSource, RequestContext request)
{
    private static readonly PasswordHasher<StaffUserRow> Hasher = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid ActorId => request.PlatformAdminUserId ?? throw new InvalidOperationException("Platform administrator required.");

    // ---------- overview & catalog ----------
    public async Task<PlatformOverviewDto> OverviewAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var weekAgo = now.AddDays(-7);
        return new PlatformOverviewDto(
            await db.Tenants.CountAsync(ct),
            await db.Facilities.CountAsync(ct),
            await db.StaffUsers.CountAsync(ct),
            await db.StaffUsers.CountAsync(u => u.IsActive, ct),
            await db.StaffUsers.CountAsync(u => u.LockedUntil != null && u.LockedUntil > now, ct),
            await db.StaffUsers.CountAsync(u => u.MustChangePassword, ct),
            await db.PlatformAdmins.CountAsync(p => p.IsActive, ct),
            await db.RoleDefinitions.CountAsync(r => r.IsActive, ct),
            await db.Affiliations.CountAsync(a => a.IsActive && a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now), ct),
            await db.AdminChanges.CountAsync(c => c.OccurredAt >= weekAgo, ct));
    }

    public static CatalogDto Catalog() =>
        new(CapabilityCatalog.All, RoleCatalog.BuiltIns.Select(r => new RoleAdminDto(null, null, r.Code, r.Name, r.Description, r.Capabilities.ToList(), true, true, RoleCatalog.Version, 0)).ToList());

    // ---------- organisations & facilities ----------
    public async Task<IReadOnlyList<TenantAdminDto>> ListTenantsAsync(CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var facilities = await db.Facilities.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var staff = await db.Affiliations.AsNoTracking().Where(a => a.IsActive).GroupBy(a => a.TenantId)
            .Select(g => new { g.Key, Count = g.Select(a => a.UserId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var roles = await db.RoleDefinitions.AsNoTracking().Where(r => r.IsActive).GroupBy(r => r.TenantId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return tenants.Select(t => new TenantAdminDto(
            t.Id, t.Name, t.CreatedAt,
            facilities.Where(f => f.TenantId == t.Id).Select(ToDto).ToList(),
            staff.GetValueOrDefault(t.Id), roles.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<Result<TenantAdminDto>> CreateTenantAsync(CreateTenantRequest body, CancellationToken ct)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length is < 2 or > 200)
        {
            return Invalid("invalid_name", "Organisation name must be 2–200 characters.", "name");
        }

        var tenant = new TenantRow { Id = Guid.CreateVersion7(), Name = name, CreatedAt = clock.GetUtcNow() };
        var facility = BuildFacility(tenant.Id, body.FacilityCode, body.FacilityName, null, null, null, body.TimeZone);
        if (!facility.IsSuccess)
        {
            return facility.Error!;
        }

        db.Tenants.Add(tenant);
        db.Facilities.Add(facility.Value);
        Log("organisation.create", "tenant", tenant.Id, tenant.Id, null, new { tenant.Name, facility = facility.Value.Name });
        await db.SaveChangesAsync(ct);
        return new TenantAdminDto(tenant.Id, tenant.Name, tenant.CreatedAt, [ToDto(facility.Value)], 0, 0);
    }

    public async Task<Result<bool>> RenameTenantAsync(Guid tenantId, RenameTenantRequest body, CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            return Error.NotFound();
        }

        var name = (body.Name ?? "").Trim();
        if (name.Length is < 2 or > 200)
        {
            return Invalid("invalid_name", "Organisation name must be 2–200 characters.", "name");
        }

        Log("organisation.rename", "tenant", tenant.Id, tenant.Id, new { tenant.Name }, new { Name = name });
        tenant.Name = name;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Result<FacilityAdminDto>> CreateFacilityAsync(FacilityRequest body, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct))
        {
            return Error.NotFound("tenant_not_found", "Organisation not found.");
        }

        var facility = BuildFacility(body.TenantId, body.Code, body.Name, body.AddressLine, body.Phone, body.LicenseNumber, body.TimeZone);
        if (!facility.IsSuccess)
        {
            return facility.Error!;
        }

        if (await db.Facilities.AnyAsync(f => f.TenantId == body.TenantId && f.Code == facility.Value.Code, ct))
        {
            return Error.Conflict("facility_code_taken", "This organisation already has a facility with that code.");
        }

        db.Facilities.Add(facility.Value);
        Log("facility.create", "facility", facility.Value.Id, body.TenantId, null, ToDto(facility.Value));
        await db.SaveChangesAsync(ct);
        return ToDto(facility.Value);
    }

    public async Task<Result<FacilityAdminDto>> UpdateFacilityAsync(Guid facilityId, FacilityRequest body, CancellationToken ct)
    {
        var row = await db.Facilities.SingleOrDefaultAsync(f => f.Id == facilityId, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        var candidate = BuildFacility(row.TenantId, row.Code, body.Name, body.AddressLine, body.Phone, body.LicenseNumber, body.TimeZone);
        if (!candidate.IsSuccess)
        {
            return candidate.Error!;
        }

        var before = ToDto(row);
        row.Name = candidate.Value.Name;
        row.AddressLine = candidate.Value.AddressLine;
        row.Phone = candidate.Value.Phone;
        row.LicenseNumber = candidate.Value.LicenseNumber;
        row.TimeZone = candidate.Value.TimeZone;
        Log("facility.update", "facility", row.Id, row.TenantId, before, ToDto(row));
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    // ---------- staff ----------
    public async Task<IReadOnlyList<StaffSummaryDto>> ListStaffAsync(string? q, Guid? tenantId, CancellationToken ct)
    {
        var users = db.StaffUsers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            users = users.Where(u => EF.Functions.ILike(u.Username, pattern) || EF.Functions.ILike(u.DisplayName, pattern));
        }

        if (tenantId is { } t)
        {
            users = users.Where(u => db.Affiliations.Any(a => a.UserId == u.Id && a.TenantId == t));
        }

        var list = await users.OrderBy(u => u.DisplayName).Take(200).ToListAsync(ct);
        return await SummariesAsync(list, ct);
    }

    public async Task<Result<StaffDetailDto>> GetStaffAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.StaffUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        var summary = (await SummariesAsync([user], ct))[0];
        var assignments = await AssignmentsAsync(userId, ct);
        var profiles = await (
            from p in db.Practitioners.AsNoTracking()
            join t in db.Tenants.AsNoTracking() on p.TenantId equals t.Id
            where p.UserId == userId
            orderby t.Name
            select new PractitionerProfileDto(p.Id, p.TenantId, t.Name, p.ProfessionalTitle, p.Specialty, p.LicenseNumber, p.LicenseExpiresOn)).ToListAsync(ct);
        var sessions = await db.Sessions.CountAsync(s => s.UserId == userId && s.RevokedAt == null && s.AbsoluteExpiresAt > now, ct);
        return new StaffDetailDto(summary, assignments, profiles, sessions, user.CreatedAt, user.PasswordChangedAt);
    }

    public async Task<Result<StaffDetailDto>> CreateStaffAsync(CreateStaffRequest body, CancellationToken ct)
    {
        var username = AccessPolicy.ValidateUsername(body.Username);
        if (!username.IsSuccess)
        {
            return username.Error!;
        }

        var displayName = (body.DisplayName ?? "").Trim();
        if (displayName.Length is < 1 or > 200)
        {
            return Invalid("invalid_display_name", "Display name must be 1–200 characters.", "displayName");
        }

        var password = AccessPolicy.ValidateNewPassword(body.TemporaryPassword, username.Value);
        if (!password.IsSuccess)
        {
            return password.Error!;
        }

        if (await db.StaffUsers.AnyAsync(u => u.Username == username.Value, ct))
        {
            return Error.Conflict("username_taken", "That username is already in use.");
        }

        var user = new StaffUserRow
        {
            Id = Guid.CreateVersion7(),
            Username = username.Value,
            DisplayName = displayName,
            PasswordHash = AuthService.HashPassword(password.Value),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = clock.GetUtcNow(),
        };
        db.StaffUsers.Add(user);
        Log("staff.create", "staff_user", user.Id, null, null, new { user.Username, user.DisplayName, mustChangePassword = true });
        await db.SaveChangesAsync(ct);
        return await GetStaffAsync(user.Id, ct);
    }

    public async Task<Result<bool>> UpdateStaffAsync(Guid userId, UpdateStaffRequest body, CancellationToken ct)
    {
        var guard = NotSelf(userId);
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var user = await db.StaffUsers.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound();
        }

        var displayName = (body.DisplayName ?? "").Trim();
        if (displayName.Length is < 1 or > 200)
        {
            return Invalid("invalid_display_name", "Display name must be 1–200 characters.", "displayName");
        }

        Log("staff.rename", "staff_user", userId, null, new { user.DisplayName }, new { DisplayName = displayName });
        user.DisplayName = displayName;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Activates or deactivates an account. Deactivation ends every session immediately.</summary>
    public async Task<Result<bool>> SetActiveAsync(Guid userId, SetActiveRequest body, CancellationToken ct)
    {
        var guard = NotSelf(userId);
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var user = await db.StaffUsers.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound();
        }

        if (!body.IsActive && await IsLastActivePlatformAdminAsync(userId, ct))
        {
            return Error.Rule("last_super_admin", "This is the last active super administrator; grant another super administrator first.");
        }

        if (user.IsActive == body.IsActive)
        {
            return true;
        }

        Log(body.IsActive ? "staff.activate" : "staff.deactivate", "staff_user", userId, null, new { user.IsActive }, new { body.IsActive }, body.Reason);
        user.IsActive = body.IsActive;
        if (!body.IsActive)
        {
            await RevokeSessionsAsync(userId, "account_deactivated", ct);
        }

        await db.SaveChangesAsync(ct);
        await SecurityEventAsync(body.IsActive ? "staff.activated" : "staff.deactivated", userId, ct);
        return true;
    }

    public async Task<Result<bool>> UnlockAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.StaffUsers.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound();
        }

        Log("staff.unlock", "staff_user", userId, null, new { user.FailedLoginCount, user.LockedUntil }, new { FailedLoginCount = 0 });
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sets a temporary password the user must change at next sign-in; ends existing sessions and clears lockout.</summary>
    public async Task<Result<bool>> ResetPasswordAsync(Guid userId, ResetPasswordRequest body, CancellationToken ct)
    {
        var guard = NotSelf(userId, "Change your own password from your account menu.");
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var user = await db.StaffUsers.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound();
        }

        var password = AccessPolicy.ValidateNewPassword(body.TemporaryPassword, user.Username);
        if (!password.IsSuccess)
        {
            return password.Error!;
        }

        user.PasswordHash = AuthService.HashPassword(password.Value);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await RevokeSessionsAsync(userId, "password_reset", ct);
        Log("staff.password_reset", "staff_user", userId, null, null, new { mustChangePassword = true });
        await db.SaveChangesAsync(ct);
        await SecurityEventAsync("staff.password_reset", userId, ct);
        return true;
    }

    public async Task<Result<int>> RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.StaffUsers.AnyAsync(u => u.Id == userId, ct))
        {
            return Error.NotFound();
        }

        var count = await RevokeSessionsAsync(userId, "super_admin_revoked", ct);
        Log("staff.sessions_revoked", "staff_user", userId, null, null, new { revoked = count });
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<Result<PractitionerProfileDto>> UpsertPractitionerAsync(Guid userId, PractitionerProfileRequest body, CancellationToken ct)
    {
        var guard = NotSelf(userId);
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == body.TenantId, ct);
        if (tenant is null || !await db.StaffUsers.AnyAsync(u => u.Id == userId, ct))
        {
            return Error.NotFound();
        }

        var title = (body.ProfessionalTitle ?? "").Trim();
        var specialty = (body.Specialty ?? "").Trim();
        var license = (body.LicenseNumber ?? "").Trim();
        if (title.Length is < 2 or > 120 || specialty.Length is < 2 or > 120 || license.Length is < 2 or > 60 || body.LicenseExpiresOn is null)
        {
            return Invalid("invalid_practitioner", "Title, specialty, licence number and licence expiry date are required.", "practitioner");
        }

        var row = await db.Practitioners.SingleOrDefaultAsync(p => p.UserId == userId && p.TenantId == body.TenantId, ct);
        object? before = row is null ? null : new { row.ProfessionalTitle, row.Specialty, row.LicenseNumber, row.LicenseExpiresOn };
        if (row is null)
        {
            row = new PractitionerRow { Id = Guid.CreateVersion7(), UserId = userId, TenantId = body.TenantId };
            db.Practitioners.Add(row);
        }

        row.ProfessionalTitle = title;
        row.Specialty = specialty;
        row.LicenseNumber = license;
        row.LicenseExpiresOn = body.LicenseExpiresOn.Value;
        Log("practitioner.upsert", "staff_user", userId, body.TenantId, before, new { title, specialty, license, body.LicenseExpiresOn });
        await db.SaveChangesAsync(ct);
        return new PractitionerProfileDto(row.Id, row.TenantId, tenant.Name, title, specialty, license, row.LicenseExpiresOn);
    }

    // ---------- assignments (affiliations) ----------
    public async Task<Result<AssignmentAdminDto>> CreateAssignmentAsync(Guid userId, AssignmentRequest body, CancellationToken ct)
    {
        var guard = NotSelf(userId, "Super administrators cannot change their own access; ask another super administrator.");
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var user = await db.StaffUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        var facility = await db.Facilities.AsNoTracking().SingleOrDefaultAsync(f => f.Id == body.FacilityId, ct);
        if (user is null || facility is null)
        {
            return Error.NotFound();
        }

        if (await db.Affiliations.AnyAsync(a => a.UserId == userId && a.FacilityId == body.FacilityId, ct))
        {
            return Error.Conflict("assignment_exists", "This person already has an assignment at that facility; edit it instead.");
        }

        var now = clock.GetUtcNow();
        var validated = await ValidateAssignmentAsync(facility.TenantId, body, body.ValidFrom ?? now, ct);
        if (!validated.IsSuccess)
        {
            return validated.Error!;
        }

        var (roles, granted, revoked) = validated.Value;
        var row = new AffiliationRow
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TenantId = facility.TenantId,
            FacilityId = facility.Id,
            Roles = roles,
            GrantedCapabilities = granted,
            RevokedCapabilities = revoked,
            IsActive = body.IsActive ?? true,
            ValidFrom = body.ValidFrom ?? now,
            ValidUntil = body.ValidUntil,
            Version = 1,
            UpdatedBy = ActorId,
            UpdatedAt = now,
        };
        db.Affiliations.Add(row);
        Log("assignment.create", "staff_user", userId, row.TenantId, null, Snapshot(row), body.Reason);
        await db.SaveChangesAsync(ct);
        return (await AssignmentsAsync(userId, ct)).Single(a => a.Id == row.Id);
    }

    public async Task<Result<AssignmentAdminDto>> UpdateAssignmentAsync(Guid assignmentId, AssignmentRequest body, CancellationToken ct)
    {
        var row = await db.Affiliations.SingleOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        var guard = NotSelf(row.UserId, "Super administrators cannot change their own access; ask another super administrator.");
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        if (body.Version is { } v && v != row.Version)
        {
            return Error.Precondition("assignment_changed", "Someone else changed this assignment. Reload and try again.");
        }

        if (body.FacilityId != row.FacilityId)
        {
            return Invalid("facility_immutable", "An assignment's facility cannot change; create a new assignment instead.", "facilityId");
        }

        var validFrom = body.ValidFrom ?? row.ValidFrom;
        var validated = await ValidateAssignmentAsync(row.TenantId, body with { ValidFrom = validFrom }, validFrom, ct);
        if (!validated.IsSuccess)
        {
            return validated.Error!;
        }

        var before = Snapshot(row);
        var (roles, granted, revoked) = validated.Value;
        row.Roles = roles;
        row.GrantedCapabilities = granted;
        row.RevokedCapabilities = revoked;
        row.ValidFrom = validFrom;
        row.ValidUntil = body.ValidUntil;
        row.IsActive = body.IsActive ?? row.IsActive;
        row.Version++;
        row.UpdatedBy = ActorId;
        row.UpdatedAt = clock.GetUtcNow();
        Log(row.IsActive ? "assignment.update" : "assignment.deactivate", "staff_user", row.UserId, row.TenantId, before, Snapshot(row), body.Reason);
        await db.SaveChangesAsync(ct);
        return (await AssignmentsAsync(row.UserId, ct)).Single(a => a.Id == row.Id);
    }

    /// <summary>Computes what an assignment would allow, with warnings for risky or ineffective combinations.</summary>
    public async Task<Result<PreviewDto>> PreviewAsync(PreviewRequest body, CancellationToken ct)
    {
        var granted = AccessPolicy.ValidateCapabilities(body.GrantedCapabilities, "grantedCapabilities", true);
        var revoked = AccessPolicy.ValidateCapabilities(body.RevokedCapabilities, "revokedCapabilities", true);
        if (!granted.IsSuccess || !revoked.IsSuccess)
        {
            return (granted.Error ?? revoked.Error)!;
        }

        var roles = (body.Roles ?? []).Distinct(StringComparer.Ordinal).ToList();
        var custom = await CustomRoleMapAsync(body.TenantId, ct);
        var effective = AccessPolicy.Effective(roles, custom, granted.Value, revoked.Value);
        var infos = CapabilityCatalog.All.Where(c => effective.Contains(c.Code)).ToList();
        var warnings = new List<string>();
        var unknownRoles = roles.Where(r => !RoleCatalog.IsKnownRole(r) && !custom.ContainsKey(r)).ToList();
        if (unknownRoles.Count > 0)
        {
            warnings.Add($"Unknown or inactive roles grant nothing: {string.Join(", ", unknownRoles)}.");
        }

        var clinical = infos.Count(c => c.Sensitivity == CapabilitySensitivity.Clinical);
        var admin = infos.Count(c => c.Sensitivity == CapabilitySensitivity.Administrative);
        if (clinical > 0 && admin > 0)
        {
            warnings.Add("Combines clinical record access with administrative permissions; consider separate accounts (separation of duties).");
        }

        if ((effective.Contains(Capabilities.NoteSign) || effective.Contains(Capabilities.DentalChartWrite)) && body.UserId is { } uid
            && !await db.Practitioners.AnyAsync(p => p.UserId == uid && p.TenantId == body.TenantId, ct))
        {
            warnings.Add("Signing and charting also need a practitioner profile with a valid licence in this organisation; without one they will be refused.");
        }

        if (granted.Value.Intersect(revoked.Value).Any())
        {
            warnings.Add("A permission cannot be both granted and removed.");
        }

        var bySensitivity = Enum.GetValues<CapabilitySensitivity>().ToDictionary(s => s.ToString(), s => infos.Count(c => c.Sensitivity == s));
        return new PreviewDto(infos, bySensitivity, warnings);
    }

    // ---------- roles ----------
    public async Task<IReadOnlyList<RoleAdminDto>> ListRolesAsync(Guid tenantId, CancellationToken ct)
    {
        var assignments = await db.Affiliations.AsNoTracking().Where(a => a.TenantId == tenantId && a.IsActive).Select(a => a.Roles).ToListAsync(ct);
        int Count(string code) => assignments.Count(r => r.Contains(code));
        var builtIn = RoleCatalog.BuiltIns.Select(r => new RoleAdminDto(null, null, r.Code, r.Name, r.Description, r.Capabilities.ToList(), true, true, RoleCatalog.Version, Count(r.Code)));
        var custom = (await db.RoleDefinitions.AsNoTracking().Where(r => r.TenantId == tenantId).OrderBy(r => r.Name).ToListAsync(ct))
            .Select(r => ToDto(r, Count(r.Code)));
        return builtIn.Concat(custom).ToList();
    }

    public async Task<Result<RoleAdminDto>> CreateRoleAsync(CreateRoleRequest body, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == body.TenantId, ct))
        {
            return Error.NotFound("tenant_not_found", "Organisation not found.");
        }

        var name = AccessPolicy.ValidateRoleName(body.Name);
        var caps = AccessPolicy.ValidateCapabilities(body.Capabilities, "capabilities", false);
        if (!name.IsSuccess || !caps.IsSuccess)
        {
            return (name.Error ?? caps.Error)!;
        }

        var code = AccessPolicy.CodeFromName(name.Value);
        if (await db.RoleDefinitions.AnyAsync(r => r.TenantId == body.TenantId && (r.Code == code || r.Name == name.Value), ct)
            || RoleCatalog.BuiltIns.Any(b => string.Equals(b.Name, name.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Conflict("role_exists", "A role with that name already exists in this organisation.");
        }

        var now = clock.GetUtcNow();
        var row = new RoleDefinitionRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = body.TenantId,
            Code = code,
            Name = name.Value,
            Description = Clip(body.Description, 500),
            Capabilities = [.. caps.Value],
            IsActive = true,
            Version = 1,
            CreatedBy = ActorId,
            CreatedAt = now,
            UpdatedBy = ActorId,
            UpdatedAt = now,
        };
        db.RoleDefinitions.Add(row);
        Log("role.create", "role_definition", row.Id, row.TenantId, null, new { row.Code, row.Name, row.Capabilities });
        await db.SaveChangesAsync(ct);
        return ToDto(row, 0);
    }

    public async Task<Result<RoleAdminDto>> UpdateRoleAsync(Guid roleId, UpdateRoleRequest body, CancellationToken ct)
    {
        var row = await db.RoleDefinitions.SingleOrDefaultAsync(r => r.Id == roleId, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        if (body.Version != row.Version)
        {
            return Error.Precondition("role_changed", "Someone else changed this role. Reload and try again.");
        }

        var name = AccessPolicy.ValidateRoleName(body.Name);
        var caps = AccessPolicy.ValidateCapabilities(body.Capabilities, "capabilities", false);
        if (!name.IsSuccess || !caps.IsSuccess)
        {
            return (name.Error ?? caps.Error)!;
        }

        if (await db.RoleDefinitions.AnyAsync(r => r.TenantId == row.TenantId && r.Id != row.Id && r.Name == name.Value, ct))
        {
            return Error.Conflict("role_exists", "Another role in this organisation already has that name.");
        }

        var before = new { row.Name, row.Capabilities, row.IsActive, row.Version };
        row.Name = name.Value;
        row.Description = Clip(body.Description, 500);
        row.Capabilities = [.. caps.Value];
        row.IsActive = body.IsActive;
        row.Version++;
        row.UpdatedBy = ActorId;
        row.UpdatedAt = clock.GetUtcNow();
        Log("role.update", "role_definition", row.Id, row.TenantId, before, new { row.Name, row.Capabilities, row.IsActive, row.Version });
        await db.SaveChangesAsync(ct);
        var count = await db.Affiliations.CountAsync(a => a.TenantId == row.TenantId && a.IsActive && a.Roles.Contains(row.Code), ct);
        return ToDto(row, count);
    }

    // ---------- super administrators ----------
    public async Task<IReadOnlyList<PlatformAdminDto>> ListPlatformAdminsAsync(CancellationToken ct)
    {
        var rows = await (
            from p in db.PlatformAdmins.AsNoTracking()
            join u in db.StaffUsers.AsNoTracking() on p.UserId equals u.Id
            join g in db.StaffUsers.AsNoTracking() on p.GrantedBy equals g.Id into granters
            from g in granters.DefaultIfEmpty()
            orderby p.IsActive descending, u.DisplayName
            select new PlatformAdminDto(p.UserId, u.Username, u.DisplayName, p.IsActive, p.GrantedAt, g == null ? null : g.DisplayName, p.RevokedAt)).ToListAsync(ct);
        return rows;
    }

    public async Task<Result<bool>> GrantPlatformAdminAsync(PlatformAdminRequest body, CancellationToken ct)
    {
        var stepUp = await ConfirmActorPasswordAsync(body.ConfirmPassword, ct);
        if (!stepUp.IsSuccess)
        {
            return stepUp.Error!;
        }

        var guard = NotSelf(body.UserId);
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var user = await db.StaffUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == body.UserId, ct);
        if (user is null)
        {
            return Error.NotFound();
        }

        if (!user.IsActive)
        {
            return Error.Rule("inactive_account", "Activate the account before granting super administration.");
        }

        var now = clock.GetUtcNow();
        var row = await db.PlatformAdmins.SingleOrDefaultAsync(p => p.UserId == body.UserId, ct);
        if (row is { IsActive: true })
        {
            return true;
        }

        if (row is null)
        {
            db.PlatformAdmins.Add(new PlatformAdminRow { UserId = body.UserId, IsActive = true, GrantedBy = ActorId, GrantedAt = now });
        }
        else
        {
            row.IsActive = true;
            row.GrantedBy = ActorId;
            row.GrantedAt = now;
            row.RevokedAt = null;
            row.RevokedBy = null;
        }

        Log("super_admin.grant", "staff_user", body.UserId, null, new { isSuperAdmin = false }, new { isSuperAdmin = true }, body.Reason);
        await db.SaveChangesAsync(ct);
        await SecurityEventAsync("super_admin.granted", body.UserId, ct);
        return true;
    }

    public async Task<Result<bool>> RevokePlatformAdminAsync(PlatformAdminRequest body, CancellationToken ct)
    {
        var stepUp = await ConfirmActorPasswordAsync(body.ConfirmPassword, ct);
        if (!stepUp.IsSuccess)
        {
            return stepUp.Error!;
        }

        var guard = NotSelf(body.UserId, "You cannot remove your own super administration; ask another super administrator.");
        if (!guard.IsSuccess)
        {
            return guard.Error!;
        }

        var row = await db.PlatformAdmins.SingleOrDefaultAsync(p => p.UserId == body.UserId && p.IsActive, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        if (await IsLastActivePlatformAdminAsync(body.UserId, ct))
        {
            return Error.Rule("last_super_admin", "At least one active super administrator must remain.");
        }

        row.IsActive = false;
        row.RevokedAt = clock.GetUtcNow();
        row.RevokedBy = ActorId;
        Log("super_admin.revoke", "staff_user", body.UserId, null, new { isSuperAdmin = true }, new { isSuperAdmin = false }, body.Reason);
        await db.SaveChangesAsync(ct);
        await SecurityEventAsync("super_admin.revoked", body.UserId, ct);
        return true;
    }

    // ---------- change log ----------
    public async Task<IReadOnlyList<AdminChangeDto>> ChangesAsync(Guid? targetId, DateTimeOffset? before, int limit, CancellationToken ct)
    {
        var query = db.AdminChanges.AsNoTracking();
        if (targetId is { } t)
        {
            query = query.Where(c => c.TargetId == t);
        }

        if (before is { } b)
        {
            query = query.Where(c => c.OccurredAt < b);
        }

        return await (
            from c in query
            join u in db.StaffUsers.AsNoTracking() on c.ActorUserId equals u.Id
            orderby c.OccurredAt descending
            select new AdminChangeDto(c.Id, c.OccurredAt, u.DisplayName, c.Action, c.TargetType, c.TargetId, c.TenantId, c.Before, c.After, c.Reason))
            .Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
    }

    // ---------- helpers ----------
    private async Task<Result<(string[] Roles, string[] Granted, string[] Revoked)>> ValidateAssignmentAsync(Guid tenantId, AssignmentRequest body, DateTimeOffset validFrom, CancellationToken ct)
    {
        var roles = (body.Roles ?? []).Select(r => r?.Trim() ?? "").Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (roles.Length == 0)
        {
            return Invalid("roles_required", "Choose at least one role.", "roles");
        }

        var custom = await CustomRoleMapAsync(tenantId, ct);
        var unknown = roles.FirstOrDefault(r => !RoleCatalog.IsKnownRole(r) && !custom.ContainsKey(r));
        if (unknown is not null)
        {
            return Invalid("unknown_role", $"'{unknown}' is not an active role in this organisation.", "roles");
        }

        var granted = AccessPolicy.ValidateCapabilities(body.GrantedCapabilities, "grantedCapabilities", true);
        var revoked = AccessPolicy.ValidateCapabilities(body.RevokedCapabilities, "revokedCapabilities", true);
        if (!granted.IsSuccess || !revoked.IsSuccess)
        {
            return (granted.Error ?? revoked.Error)!;
        }

        if (granted.Value.Intersect(revoked.Value).Any())
        {
            return Invalid("override_conflict", "A permission cannot be both granted and removed.", "grantedCapabilities");
        }

        if (body.ValidUntil is { } until && until <= validFrom)
        {
            return Invalid("invalid_validity", "The end date must be after the start date.", "validUntil");
        }

        return (roles, granted.Value.ToArray(), revoked.Value.ToArray());
    }

    private async Task<Dictionary<string, IReadOnlyCollection<string>>> CustomRoleMapAsync(Guid tenantId, CancellationToken ct) =>
        await db.RoleDefinitions.AsNoTracking().Where(r => r.TenantId == tenantId && r.IsActive)
            .ToDictionaryAsync(r => r.Code, r => (IReadOnlyCollection<string>)r.Capabilities, StringComparer.Ordinal, ct);

    private async Task<List<AssignmentAdminDto>> AssignmentsAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await (
            from a in db.Affiliations.AsNoTracking()
            join f in db.Facilities.AsNoTracking() on a.FacilityId equals f.Id
            join t in db.Tenants.AsNoTracking() on a.TenantId equals t.Id
            where a.UserId == userId
            orderby t.Name, f.Name
            select new { a, FacilityName = f.Name, TenantName = t.Name }).ToListAsync(ct);
        var result = new List<AssignmentAdminDto>();
        foreach (var r in rows)
        {
            var effective = await auth.EffectiveCapabilitiesAsync(r.a, ct);
            result.Add(new AssignmentAdminDto(
                r.a.Id, r.a.TenantId, r.TenantName, r.a.FacilityId, r.FacilityName, r.a.Roles, r.a.GrantedCapabilities, r.a.RevokedCapabilities,
                effective.Order(StringComparer.Ordinal).ToList(), r.a.IsActive,
                r.a.IsActive && r.a.ValidFrom <= now && (r.a.ValidUntil is null || r.a.ValidUntil > now),
                r.a.ValidFrom, r.a.ValidUntil, r.a.Version));
        }

        return result;
    }

    private async Task<List<StaffSummaryDto>> SummariesAsync(List<StaffUserRow> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var now = clock.GetUtcNow();
        var admins = await db.PlatformAdmins.AsNoTracking().Where(p => ids.Contains(p.UserId) && p.IsActive).Select(p => p.UserId).ToListAsync(ct);
        var assignments = await (
            from a in db.Affiliations.AsNoTracking()
            join t in db.Tenants.AsNoTracking() on a.TenantId equals t.Id
            where ids.Contains(a.UserId)
            select new { a.UserId, a.IsActive, a.ValidFrom, a.ValidUntil, t.Name }).ToListAsync(ct);
        return users.Select(u =>
        {
            var mine = assignments.Where(a => a.UserId == u.Id).ToList();
            return new StaffSummaryDto(
                u.Id, u.Username, u.DisplayName, u.IsActive, u.LockedUntil is { } l && l > now, u.MustChangePassword, admins.Contains(u.Id), u.LastLoginAt,
                mine.Count(a => a.IsActive && a.ValidFrom <= now && (a.ValidUntil is null || a.ValidUntil > now)),
                mine.Select(a => a.Name).Distinct().Order(StringComparer.Ordinal).ToList());
        }).ToList();
    }

    private async Task<int> RevokeSessionsAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var s in sessions)
        {
            s.RevokedAt = now;
            s.RevokedReason = reason;
        }

        return sessions.Count;
    }

    private async Task<bool> IsLastActivePlatformAdminAsync(Guid userId, CancellationToken ct) =>
        await db.PlatformAdmins.AnyAsync(p => p.UserId == userId && p.IsActive, ct)
        && await db.PlatformAdmins.CountAsync(p => p.IsActive, ct) <= 1;

    private async Task<Result<bool>> ConfirmActorPasswordAsync(string? password, CancellationToken ct)
    {
        var actor = await db.StaffUsers.AsNoTracking().SingleAsync(u => u.Id == ActorId, ct);
        if (string.IsNullOrEmpty(password) || password.Length > 256
            || Hasher.VerifyHashedPassword(actor, actor.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            await SecurityEventAsync("super_admin.step_up_failed", actor.Id, ct);
            return Error.Forbidden("step_up_failed", "Confirm with your own current password to change super administrators.");
        }

        return true;
    }

    private Result<bool> NotSelf(Guid targetUserId, string? message = null) =>
        targetUserId == ActorId
            ? Error.Forbidden("self_modification", message ?? "Super administrators cannot change their own account or access; ask another super administrator.")
            : true;

    private void Log(string action, string targetType, Guid? targetId, Guid? tenantId, object? before, object? after, string? reason = null) =>
        db.AdminChanges.Add(new AdminChangeRow
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = clock.GetUtcNow(),
            ActorUserId = ActorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TenantId = tenantId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)],
            CorrelationId = request.CorrelationId,
        });

    private Task SecurityEventAsync(string type, Guid targetUserId, CancellationToken ct) =>
        AuditTrail.WriteSecurityEventAsync(dataSource, clock, type, "success", ActorId, null, "staff_user", targetUserId, request.CorrelationId, null, ct);

    private static object Snapshot(AffiliationRow a) =>
        new { assignmentId = a.Id, a.FacilityId, a.Roles, a.GrantedCapabilities, a.RevokedCapabilities, a.IsActive, a.ValidFrom, a.ValidUntil, a.Version };

    private static Result<FacilityRow> BuildFacility(Guid tenantId, string? code, string? name, string? address, string? phone, string? license, string? timeZone)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        var n = (name ?? "").Trim();
        var tz = string.IsNullOrWhiteSpace(timeZone) ? "Africa/Addis_Ababa" : timeZone.Trim();
        if (c.Length is < 1 or > 20 || !c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
        {
            return Invalid("invalid_code", "Facility code must be 1–20 letters, digits or dashes.", "code");
        }

        if (n.Length is < 2 or > 200)
        {
            return Invalid("invalid_name", "Facility name must be 2–200 characters.", "name");
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
        {
            return Invalid("invalid_time_zone", "Unknown time zone (use an IANA name such as Africa/Addis_Ababa).", "timeZone");
        }

        return new FacilityRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Code = c,
            Name = n,
            AddressLine = Clip(address, 300),
            Phone = Clip(phone, 40),
            LicenseNumber = Clip(license, 60),
            TimeZone = tz,
        };
    }

    private static string Clip(string? value, int max) => (value ?? "").Trim() is var v && v.Length <= max ? v : v[..max];

    private static FacilityAdminDto ToDto(FacilityRow f) => new(f.Id, f.TenantId, f.Code, f.Name, f.AddressLine, f.Phone, f.LicenseNumber, f.TimeZone);

    private static RoleAdminDto ToDto(RoleDefinitionRow r, int assigned) =>
        new(r.Id, r.TenantId, r.Code, r.Name, r.Description, r.Capabilities, false, r.IsActive, r.Version, assigned);

    private static Error Invalid(string code, string message, string field) =>
        Error.Validation(code, message, new Dictionary<string, string[]> { [field] = [message] });
}
