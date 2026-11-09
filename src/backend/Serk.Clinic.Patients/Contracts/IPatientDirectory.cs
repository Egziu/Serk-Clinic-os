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
    DateTimeOffset? AllergyStatusUpdatedAt,
    IReadOnlyList<AllergySummary> ActiveAllergies)
{
    /// <summary>
    /// Fingerprint of the allergy information shown to the clinician. Signing must present the token it displayed; if
    /// allergies changed in between, the signature is refused (prevents attesting to unseen allergies).
    /// </summary>
    public string AllergyReviewToken => Serk.Clinic.SharedKernel.CanonicalJson.Sha256Hex(
        Serk.Clinic.SharedKernel.CanonicalJson.Serialize(new { AllergyStatus, ActiveAllergies }))[..32];
}

/// <summary>Published read contract of the Patients module. Always tenant-scoped by the caller's validated context.</summary>
public interface IPatientDirectory
{
    Task<bool> ExistsAsync(Guid patientId, CancellationToken ct);

    Task<PatientBanner?> GetBannerAsync(Guid patientId, CancellationToken ct);

    /// <summary>Batch lookup (two queries regardless of count) to avoid N+1 on lists.</summary>
    Task<IReadOnlyDictionary<Guid, PatientBanner>> GetBannersAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken ct);
}
