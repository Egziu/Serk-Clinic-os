namespace Serk.Clinic.Identity.Contracts;

public sealed record PractitionerSummary(
    Guid Id,
    Guid UserId,
    Guid TenantId,
    string DisplayName,
    string ProfessionalTitle,
    string Specialty,
    string LicenseNumber,
    DateOnly LicenseExpiresOn);

public sealed record FacilitySummary(
    Guid Id,
    Guid TenantId,
    string Code,
    string Name,
    string AddressLine,
    string Phone,
    string LicenseNumber,
    string TimeZone);

/// <summary>Published read contract of the Identity module for other modules.</summary>
public interface IStaffDirectory
{
    Task<PractitionerSummary?> GetPractitionerAsync(Guid tenantId, Guid practitionerId, CancellationToken ct);

    Task<FacilitySummary?> GetFacilityAsync(Guid tenantId, Guid facilityId, CancellationToken ct);

    /// <summary>Practitioners with an active affiliation at the facility.</summary>
    Task<IReadOnlyList<PractitionerSummary>> ListFacilityPractitionersAsync(Guid tenantId, Guid facilityId, CancellationToken ct);

    /// <summary>True when the practitioner holds an active affiliation at the facility.</summary>
    Task<bool> IsPractitionerActiveAtFacilityAsync(Guid tenantId, Guid facilityId, Guid practitionerId, CancellationToken ct);

    /// <summary>All tenant ids (for trusted background jobs that iterate tenants with per-tenant RLS context).</summary>
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct);
}
