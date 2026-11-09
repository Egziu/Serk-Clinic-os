namespace Serk.Clinic.Patients.Contracts;

public sealed record AllergySummary(string Substance, string Reaction, string Severity);

/// <summary>Minimum identity + safety context shown on every clinical screen and document.</summary>
public sealed record PatientBanner(
    Guid Id,
    string Mrn,
    string DisplayName,
    string Sex,
    DateOnly BirthDate,
    bool BirthDateIsEstimated,
    string AllergyStatus,
    IReadOnlyList<AllergySummary> ActiveAllergies);

/// <summary>Published read contract of the Patients module. Always tenant-scoped by the caller's validated context.</summary>
public interface IPatientDirectory
{
    Task<bool> ExistsAsync(Guid patientId, CancellationToken ct);

    Task<PatientBanner?> GetBannerAsync(Guid patientId, CancellationToken ct);
}
