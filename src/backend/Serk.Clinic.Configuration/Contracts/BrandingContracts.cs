namespace Serk.Clinic.Configuration.Contracts;

/// <summary>Reference to an immutable branding image, identified by id and SHA-256 of its bytes.</summary>
public sealed record AssetRef(Guid Id, string Sha256, string ContentType);

public sealed record FacilityBranding(string AccentColor, AssetRef? Logo, AssetRef? Stamp, string DefaultClinicalPaper, bool AutoPrepareDocuments);

/// <summary>Published read contract of the Configuration module (tenant-scoped by the caller's context).</summary>
public interface IBrandingReader
{
    Task<FacilityBranding> GetFacilityBrandingAsync(Guid facilityId, CancellationToken ct);

    Task<AssetRef?> GetActiveSignatureAsync(Guid practitionerId, CancellationToken ct);

    /// <summary>Returns the asset bytes after verifying they still hash to <see cref="AssetRef.Sha256"/>.</summary>
    Task<byte[]> ReadAssetAsync(AssetRef asset, CancellationToken ct);
}
