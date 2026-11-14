using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Identity.Infrastructure;
using Serk.Clinic.Platform.Context;

namespace Serk.Clinic.Identity.Application;

public sealed record MatrixRoleDto(string Code, string Name, string Description, bool BuiltIn, IReadOnlyList<string> Responsibilities, IReadOnlyList<string> Capabilities, int People);

public sealed record RolesMatrixDto(IReadOnlyList<CapabilityInfo> Capabilities, IReadOnlyList<MatrixRoleDto> Roles, int PolicyVersion);

public sealed record TeamAssignmentDto(string Facility, IReadOnlyList<string> Roles, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool Active, DateTimeOffset? ValidUntil);

public sealed record TeamMemberDto(Guid UserId, string Username, string DisplayName, bool AccountActive, bool Locked, DateTimeOffset? LastLoginAt, IReadOnlyList<TeamAssignmentDto> Assignments);

/// <summary>
/// Read-only view of who does what in the caller's own organisation: the roles-and-responsibilities matrix (every
/// member may see it) and the team list (staff administrators). Changing access stays with super administrators.
/// </summary>
public sealed class OrganisationService(IdentityDbContext db, RequestContext context, TimeProvider clock)
{
    public async Task<RolesMatrixDto> MatrixAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var now = clock.GetUtcNow();
        var assignments = await db.Affiliations.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.IsActive && a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now))
            .Select(a => new { a.UserId, a.Roles }).ToListAsync(ct);
        int People(string code) => assignments.Where(a => a.Roles.Contains(code)).Select(a => a.UserId).Distinct().Count();
        var builtIn = RoleCatalog.BuiltIns.Select(r => new MatrixRoleDto(r.Code, r.Name, r.Description, true, RoleCatalog.ResponsibilitiesFor(r.Code), r.Capabilities.ToList(), People(r.Code)));
        var custom = (await db.RoleDefinitions.AsNoTracking().Where(r => r.TenantId == actor.TenantId && r.IsActive).OrderBy(r => r.Name).ToListAsync(ct))
            .Select(r => new MatrixRoleDto(r.Code, r.Name, r.Description, false, [], r.Capabilities.Order(StringComparer.Ordinal).ToList(), People(r.Code)));
        return new RolesMatrixDto(CapabilityCatalog.All, builtIn.Concat(custom).ToList(), RoleCatalog.Version);
    }

    public async Task<IReadOnlyList<TeamMemberDto>> TeamAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var now = clock.GetUtcNow();
        var rows = await (from a in db.Affiliations.AsNoTracking()
                          join u in db.StaffUsers.AsNoTracking() on a.UserId equals u.Id
                          join f in db.Facilities.AsNoTracking() on a.FacilityId equals f.Id
                          where a.TenantId == actor.TenantId
                          select new { a, u.Username, u.DisplayName, u.IsActive, u.LockedUntil, u.LastLoginAt, Facility = f.Name }).ToListAsync(ct);
        return rows.GroupBy(r => r.a.UserId).Select(g =>
        {
            var first = g.First();
            return new TeamMemberDto(g.Key, first.Username, first.DisplayName, first.IsActive, first.LockedUntil is { } l && l > now, first.LastLoginAt,
                g.OrderBy(x => x.Facility, StringComparer.Ordinal).Select(x => new TeamAssignmentDto(x.Facility, x.a.Roles, x.a.GrantedCapabilities, x.a.RevokedCapabilities,
                    x.a.IsActive && x.a.ValidFrom <= now && (x.a.ValidUntil == null || x.a.ValidUntil > now), x.a.ValidUntil)).ToList());
        }).OrderBy(m => m.DisplayName, StringComparer.Ordinal).ToList();
    }
}
