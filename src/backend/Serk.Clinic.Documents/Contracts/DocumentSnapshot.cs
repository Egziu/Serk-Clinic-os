using System.Text.Json.Serialization;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Configuration.Contracts;

namespace Serk.Clinic.Documents.Contracts;

public sealed record FacilityBlock(string Name, string AddressLine, string Phone, string LicenseNumber, string TimeZone);

public sealed record PatientBlock(string Mrn, string DisplayName, string Sex, DateOnly BirthDate, bool BirthDateIsEstimated, int AgeYearsAtSigning);

public sealed record SignedPart(Guid NoteVersionId, int VersionNo, string ContentSha256, SignedNoteSnapshotV1 Note);

/// <summary>Frozen document input (schema v1). Its canonical JSON hash ties preview and final to the same data.</summary>
public sealed record ConsultationDocumentSnapshotV1(
    int SchemaVersion,
    string TemplateCode,
    int TemplateVersion,
    string PaperSize,
    string VerificationRef,
    string VerificationUrl,
    DateTimeOffset GeneratedAtUtc,
    FacilityBlock Facility,
    PatientBlock Patient,
    Guid EncounterId,
    DateTimeOffset EncounterStartedAtUtc,
    IReadOnlyList<SignedPart> SignedParts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrandingBlock? Branding = null);

/// <summary>
/// Branding frozen into a document snapshot (template v3+): accent colour and immutable image references by id + SHA-256.
/// Omitted from JSON when null so snapshots created before v3 keep their original canonical hash.
/// </summary>
public sealed record BrandingBlock(string AccentColor, AssetRef? Logo, AssetRef? Stamp, IReadOnlyDictionary<string, AssetRef> SignaturesByPractitioner);
