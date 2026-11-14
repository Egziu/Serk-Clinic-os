namespace Serk.Clinic.Configuration.Contracts;

/// <summary>Reference to an immutable branding image, identified by id and SHA-256 of its bytes.</summary>
public sealed record AssetRef(Guid Id, string Sha256, string ContentType);

public sealed record FacilityBranding(string AccentColor, AssetRef? Logo, AssetRef? Stamp, string DefaultClinicalPaper, bool AutoPrepareDocuments);

/// <summary>Clinic profile as printed on letterheads and receipts. Empty strings mean "not set".</summary>
public sealed record ClinicLetterhead(
    string DisplayName,
    string Motto,
    string AddressLine1,
    string AddressLine2,
    string City,
    string Region,
    string Country,
    string Phone,
    string Email,
    string Website,
    string TaxId,
    string LicenceNo,
    string DocumentFooter,
    string ReceiptFooter,
    string Currency)
{
    public bool IsSet => DisplayName.Length > 0 || Motto.Length > 0 || AddressLine1.Length > 0;
}

/// <summary>Published read contract of the Configuration module (tenant-scoped by the caller's context).</summary>
public interface IBrandingReader
{
    Task<FacilityBranding> GetFacilityBrandingAsync(Guid facilityId, CancellationToken ct);

    /// <summary>The facility's current clinic profile (letterhead fields); all empty when not yet set up.</summary>
    Task<ClinicLetterhead> GetLetterheadAsync(Guid facilityId, CancellationToken ct);

    /// <summary>
    /// The practitioner's signature image that was in effect at <paramref name="signedAtUtc"/> (uploaded at or before that
    /// moment and not yet retired). A signature uploaded after a note was signed is never applied to that note.
    /// </summary>
    Task<AssetRef?> GetSignatureInEffectAsync(Guid practitionerId, DateTimeOffset signedAtUtc, CancellationToken ct);

    /// <summary>Returns the asset bytes after verifying they still hash to <see cref="AssetRef.Sha256"/>.</summary>
    Task<byte[]> ReadAssetAsync(AssetRef asset, CancellationToken ct);
}
