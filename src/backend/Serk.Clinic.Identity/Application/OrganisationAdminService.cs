using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Identity.Infrastructure;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Application;

public sealed record OrganisationCreateStaffRequest(string? Username, string? DisplayName, string? TemporaryPassword, Guid FacilityId, IReadOnlyList<string>? Roles);

/// <summary>
/// Staff and role administration delegated to an organisation (holders of <c>staff.manage</c>, the Owner by default).
/// Re-uses the super-administration service, but every call is confined to the caller's own organisation:
/// only people whose every assignment is in this organisation can be changed; never one's own account or access; never a
/// super administrator; facilities, roles and the change log are this organisation's only. All changes land in the same
/// append-only change log, attributed to the organisation administrator.
/// </summary>
public sealed class OrganisationAdminService(PlatformAdminService admin, IdentityDbContext db, RequestContext context)
{
    private StaffActor Actor => context.RequireActor();

    private Guid Tenant => Actor.TenantId;

    private PlatformAdminService Scoped
    {
        get
        {
            admin.ActAsOrganisationAdministrator(Actor.UserId, Tenant);
            return admin;
        }
    }

    public async Task<IReadOnlyList<TenantAdminDto>> OrganisationsAsync(CancellationToken ct) =>
        (await admin.ListTenantsAsync(ct)).Where(t => t.Id == Tenant).ToList();

    public async Task<IReadOnlyList<StaffSummaryDto>> ListStaffAsync(string? q, CancellationToken ct)
    {
        var name = await db.Tenants.AsNoTracking().Where(t => t.Id == Tenant).Select(t => t.Name).SingleAsync(ct);
        return (await admin.ListStaffAsync(q, Tenant, ct))
            .Select(s => s with { Organisations = s.Organisations.Where(o => o == name).ToList() }).ToList();
    }

    public async Task<Result<StaffDetailDto>> CreateStaffAsync(OrganisationCreateStaffRequest body, CancellationToken ct)
    {
        if (!await db.Facilities.AnyAsync(f => f.Id == body.FacilityId && f.TenantId == Tenant, ct))
        {
            return Error.Validation("facility_required", "Choose one of this organisation's facilities.", new Dictionary<string, string[]> { ["facilityId"] = ["Choose a facility."] });
        }

        var roles = (body.Roles ?? []).Distinct(StringComparer.Ordinal).ToList();
        var custom = await db.RoleDefinitions.AsNoTracking().Where(r => r.TenantId == Tenant && r.IsActive).Select(r => r.Code).ToListAsync(ct);
        if (roles.Count == 0 || roles.Any(r => !RoleCatalog.IsKnownRole(r) && !custom.Contains(r)))
        {
            return Error.Validation("role_required", "Choose at least one existing role.", new Dictionary<string, string[]> { ["roles"] = ["Choose a role."] });
        }

        var created = await Scoped.CreateStaffAsync(new CreateStaffRequest(body.Username, body.DisplayName, body.TemporaryPassword), ct);
        if (!created.IsSuccess)
        {
            return created;
        }

        var assigned = await Scoped.CreateAssignmentAsync(created.Value.Summary.Id,
            new AssignmentRequest(body.FacilityId, roles, [], [], null, null, true, null, "Added by organisation administrator"), ct);
        return assigned.IsSuccess ? await GetStaffAsync(created.Value.Summary.Id, ct) : assigned.Error!;
    }

    public async Task<Result<StaffDetailDto>> GetStaffAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Affiliations.AnyAsync(a => a.UserId == userId && a.TenantId == Tenant, ct))
        {
            return Error.NotFound();
        }

        var detail = await admin.GetStaffAsync(userId, ct);
        if (!detail.IsSuccess)
        {
            return detail;
        }

        // Other organisations' assignments and profiles are not this organisation's business.
        var d = detail.Value;
        return d with
        {
            Assignments = d.Assignments.Where(a => a.TenantId == Tenant).ToList(),
            PractitionerProfiles = d.PractitionerProfiles.Where(p => p.TenantId == Tenant).ToList(),
        };
    }

    public Task<Result<bool>> UpdateStaffAsync(Guid userId, UpdateStaffRequest body, CancellationToken ct) => Guarded(userId, () => Scoped.UpdateStaffAsync(userId, body, ct), ct);

    public Task<Result<bool>> SetActiveAsync(Guid userId, SetActiveRequest body, CancellationToken ct) => Guarded(userId, () => Scoped.SetActiveAsync(userId, body, ct), ct);

    public Task<Result<bool>> UnlockAsync(Guid userId, CancellationToken ct) => Guarded(userId, () => Scoped.UnlockAsync(userId, ct), ct);

    public Task<Result<bool>> ResetPasswordAsync(Guid userId, ResetPasswordRequest body, CancellationToken ct) => Guarded(userId, () => Scoped.ResetPasswordAsync(userId, body, ct), ct);

    public Task<Result<int>> RevokeSessionsAsync(Guid userId, CancellationToken ct) => Guarded(userId, () => Scoped.RevokeSessionsAsync(userId, ct), ct);

    public Task<Result<PractitionerProfileDto>> UpsertPractitionerAsync(Guid userId, PractitionerProfileRequest body, CancellationToken ct) =>
        body.TenantId != Tenant ? Task.FromResult<Result<PractitionerProfileDto>>(Error.NotFound()) : Guarded(userId, () => Scoped.UpsertPractitionerAsync(userId, body, ct), ct);

    public async Task<Result<AssignmentAdminDto>> CreateAssignmentAsync(Guid userId, AssignmentRequest body, CancellationToken ct)
    {
        if (!await db.Facilities.AnyAsync(f => f.Id == body.FacilityId && f.TenantId == Tenant, ct))
        {
            return Error.NotFound();
        }

        return await Guarded(userId, () => Scoped.CreateAssignmentAsync(userId, body, ct), ct);
    }

    public async Task<Result<AssignmentAdminDto>> UpdateAssignmentAsync(Guid assignmentId, AssignmentRequest body, CancellationToken ct)
    {
        var row = await db.Affiliations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assignmentId && a.TenantId == Tenant, ct);
        return row is null ? Error.NotFound() : await Guarded(row.UserId, () => Scoped.UpdateAssignmentAsync(assignmentId, body, ct), ct);
    }

    public Task<Result<PreviewDto>> PreviewAsync(PreviewRequest body, CancellationToken ct) => Scoped.PreviewAsync(body with { TenantId = Tenant }, ct);

    public Task<IReadOnlyList<RoleAdminDto>> ListRolesAsync(CancellationToken ct) => admin.ListRolesAsync(Tenant, ct);

    public Task<Result<RoleAdminDto>> CreateRoleAsync(CreateRoleRequest body, CancellationToken ct) => Scoped.CreateRoleAsync(body with { TenantId = Tenant }, ct);

    public async Task<Result<RoleAdminDto>> UpdateRoleAsync(Guid roleId, UpdateRoleRequest body, CancellationToken ct) =>
        await db.RoleDefinitions.AnyAsync(r => r.Id == roleId && r.TenantId == Tenant, ct) ? await Scoped.UpdateRoleAsync(roleId, body, ct) : Error.NotFound();

    public Task<IReadOnlyList<AdminChangeDto>> ChangesAsync(Guid? targetId, int limit, CancellationToken ct) => admin.ChangesAsync(targetId, null, limit, ct, Tenant);

    /// <summary>Only people who belong to this organisation alone, are not super administrators, and are not the caller.</summary>
    private async Task<Result<T>> Guarded<T>(Guid userId, Func<Task<Result<T>>> action, CancellationToken ct)
    {
        if (userId == Actor.UserId)
        {
            return Error.Forbidden("self_modification", "You cannot change your own account or access; ask another administrator.");
        }

        var tenants = await db.Affiliations.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.TenantId).Distinct().ToListAsync(ct);
        if (!tenants.Contains(Tenant))
        {
            return Error.NotFound();
        }

        if (tenants.Count > 1)
        {
            return Error.Forbidden("shared_account", "This person also works for another organisation; ask the system administrator to change their account.");
        }

        if (await db.PlatformAdmins.AnyAsync(p => p.UserId == userId && p.IsActive, ct))
        {
            return Error.Forbidden("super_admin", "System super administrators are managed only by other super administrators.");
        }

        return await action();
    }
}
