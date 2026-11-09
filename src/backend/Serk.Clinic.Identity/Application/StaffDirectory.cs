using Microsoft.EntityFrameworkCore;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Identity.Infrastructure;

namespace Serk.Clinic.Identity.Application;

internal sealed class StaffDirectory(IdentityDbContext db, TimeProvider clock) : IStaffDirectory
{
    public async Task<PractitionerSummary?> GetPractitionerAsync(Guid tenantId, Guid practitionerId, CancellationToken ct) =>
        await (from p in db.Practitioners.AsNoTracking()
               join u in db.StaffUsers.AsNoTracking() on p.UserId equals u.Id
               where p.Id == practitionerId && p.TenantId == tenantId
               select new PractitionerSummary(p.Id, u.Id, p.TenantId, u.DisplayName, p.ProfessionalTitle, p.Specialty, p.LicenseNumber, p.LicenseExpiresOn))
            .SingleOrDefaultAsync(ct);

    public async Task<FacilitySummary?> GetFacilityAsync(Guid tenantId, Guid facilityId, CancellationToken ct) =>
        await db.Facilities.AsNoTracking()
            .Where(f => f.Id == facilityId && f.TenantId == tenantId)
            .Select(f => new FacilitySummary(f.Id, f.TenantId, f.Code, f.Name, f.AddressLine, f.Phone, f.LicenseNumber, f.TimeZone))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PractitionerSummary>> ListFacilityPractitionersAsync(Guid tenantId, Guid facilityId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await (from p in db.Practitioners.AsNoTracking()
                      join u in db.StaffUsers.AsNoTracking() on p.UserId equals u.Id
                      join a in db.Affiliations.AsNoTracking() on new { p.UserId, p.TenantId } equals new { a.UserId, a.TenantId }
                      where p.TenantId == tenantId && a.FacilityId == facilityId && a.IsActive && u.IsActive
                            && a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now)
                      orderby u.DisplayName
                      select new PractitionerSummary(p.Id, u.Id, p.TenantId, u.DisplayName, p.ProfessionalTitle, p.Specialty, p.LicenseNumber, p.LicenseExpiresOn))
            .ToListAsync(ct);
    }

    public async Task<bool> IsPractitionerActiveAtFacilityAsync(Guid tenantId, Guid facilityId, Guid practitionerId, CancellationToken ct) =>
        (await ListFacilityPractitionersAsync(tenantId, facilityId, ct)).Any(p => p.Id == practitionerId);
}
