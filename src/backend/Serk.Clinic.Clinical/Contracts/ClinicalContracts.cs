namespace Serk.Clinic.Clinical.Contracts;

/// <summary>Structured consultation note content, schema version 1. Field limits mirror database CHECK constraints.</summary>
public sealed record NoteContentV1(string ChiefComplaint, string History, string Examination, string Assessment, string Plan)
{
    public static readonly NoteContentV1 Empty = new("", "", "", "", "");
}

public sealed record AllergySnapshot(string Substance, string Reaction, string Severity);

/// <summary>
/// Immutable signed-note snapshot. Its canonical JSON (declaration-order properties) is hashed with SHA-256 at signing.
/// Includes the safety context the clinician saw (allergy status and list) so later readers know what was reviewed.
/// </summary>
public sealed record SignedNoteSnapshotV1(
    int SchemaVersion,
    Guid EncounterId,
    Guid PatientId,
    int VersionNo,
    string Kind,
    Guid? AmendsVersionId,
    NoteContentV1? Content,
    string? AddendumText,
    string? AddendumReason,
    Guid SignerPractitionerId,
    string SignerDisplayName,
    string SignerCredential,
    DateTimeOffset SignedAtUtc,
    bool AllergiesReviewed,
    string AllergyStatusAtSigning,
    IReadOnlyList<AllergySnapshot> ActiveAllergiesAtSigning);

public sealed record SignedNoteView(
    Guid NoteVersionId,
    Guid TenantId,
    Guid FacilityId,
    Guid EncounterId,
    Guid PatientId,
    DateTimeOffset EncounterStartedAtUtc,
    int VersionNo,
    string ContentSha256,
    SignedNoteSnapshotV1 Snapshot);

/// <summary>Read contract used by Documents. Returns null when the caller's tenant/facility may not access the note.</summary>
public interface ISignedNoteReader
{
    /// <summary>All signed versions of the encounter up to and including <paramref name="noteVersionId"/>, oldest first.</summary>
    Task<IReadOnlyList<SignedNoteView>?> GetSignedChainAsync(Guid noteVersionId, CancellationToken ct);

    /// <summary>The most recent signed version of the encounter (original or addendum), or null if none/inaccessible.</summary>
    Task<Guid?> GetLatestSignedVersionIdAsync(Guid encounterId, CancellationToken ct);
}

/// <summary>Command contract used by Scheduling at check-in; executes in the caller's transaction.</summary>
public interface IEncounterLauncher
{
    Task<Guid> StartEncounterAsync(Guid appointmentId, Guid patientId, Guid attendingPractitionerId, CancellationToken ct);
}

/// <summary>Minimal encounter facts for specialty modules, only returned when the caller may access the encounter.</summary>
public sealed record EncounterAccessView(Guid EncounterId, Guid PatientId, Guid FacilityId, Guid AttendingPractitionerId, bool IsOpen);

/// <summary>Published contract for specialty packs that extend an encounter (stable, typed; no table access).</summary>
public interface IEncounterAccess
{
    Task<EncounterAccessView?> GetAsync(Guid encounterId, CancellationToken ct);
}
