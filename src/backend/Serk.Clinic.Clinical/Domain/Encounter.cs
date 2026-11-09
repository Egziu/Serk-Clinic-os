using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Clinical.Domain;

public enum EncounterStatus
{
    InProgress,
    Signed,
}

public enum NoteVersionKind
{
    Original,
    Addendum,
}

/// <summary>Credentials of the would-be signer, resolved from the Identity directory at signing time.</summary>
public sealed record SignerCredentials(Guid PractitionerId, string DisplayName, string ProfessionalTitle, string LicenseNumber, DateOnly LicenseExpiresOn)
{
    public string CredentialText => $"{ProfessionalTitle}, License {LicenseNumber}";
}

public sealed class Encounter
{
    private Encounter()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid FacilityId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid? AppointmentId { get; private set; }
    public Guid AttendingPractitionerId { get; private set; }
    public EncounterStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public int Version { get; private set; }

    public static Encounter Start(Guid tenantId, Guid facilityId, Guid patientId, Guid? appointmentId, Guid attending, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        FacilityId = facilityId,
        PatientId = patientId,
        AppointmentId = appointmentId,
        AttendingPractitionerId = attending,
        Status = EncounterStatus.InProgress,
        StartedAt = now,
        Version = 1,
    };

    /// <summary>
    /// Validates every signing precondition and produces the immutable original note version.
    /// Clinical rules here (required fields, allergy status must be recorded) are PROVISIONAL pending clinician review (HAZ-005).
    /// </summary>
    public Result<NoteVersion> Sign(
        NoteDraft draft,
        int expectedDraftVersion,
        bool allergiesReviewed,
        SignerCredentials signer,
        string allergyStatus,
        IReadOnlyList<AllergySnapshot> activeAllergies,
        DateTimeOffset now)
    {
        if (Status != EncounterStatus.InProgress)
        {
            return Error.Conflict("already_signed", "This encounter note is already signed; add an addendum instead.");
        }

        if (signer.PractitionerId != AttendingPractitionerId)
        {
            return Error.Forbidden("not_attending", "Only the attending clinician may sign this encounter note.");
        }

        if (signer.LicenseExpiresOn < DateOnly.FromDateTime(now.UtcDateTime))
        {
            return Error.Forbidden("license_expired", "The signer's professional license is expired.");
        }

        if (draft.Version != expectedDraftVersion)
        {
            return Error.Precondition("draft_changed", "The draft changed since you reviewed it. Reload and review before signing.");
        }

        if (!allergiesReviewed)
        {
            return Error.Rule("allergies_not_reviewed", "Confirm that allergies were reviewed before signing.");
        }

        if (allergyStatus == "Unknown")
        {
            return Error.Rule("allergy_status_unknown", "Record the patient's allergy status (or 'no known allergies') before signing.");
        }

        var content = draft.ToContent();
        var v = new ValidationCollector()
            .Required(content.ChiefComplaint, "chiefComplaint")
            .Required(content.Assessment, "assessment");
        if (v.HasErrors)
        {
            return v.ToError("note_incomplete");
        }

        var snapshot = new SignedNoteSnapshotV1(1, Id, PatientId, 1, nameof(NoteVersionKind.Original), null, content, null, null,
            signer.PractitionerId, signer.DisplayName, signer.CredentialText, now, allergiesReviewed, allergyStatus, activeAllergies);
        Status = EncounterStatus.Signed;
        SignedAt = now;
        Version++;
        return NoteVersion.Create(TenantId, snapshot, NoteVersionKind.Original, null);
    }

    /// <summary>Attributed addendum referencing the latest signed version. Never edits prior versions.</summary>
    public Result<NoteVersion> AddAddendum(
        NoteVersion latest,
        string? text,
        string? reason,
        SignerCredentials signer,
        string allergyStatus,
        IReadOnlyList<AllergySnapshot> activeAllergies,
        DateTimeOffset now)
    {
        if (Status != EncounterStatus.Signed)
        {
            return Error.Rule("not_signed", "Addenda can only be added to a signed note. Edit the draft instead.");
        }

        if (signer.LicenseExpiresOn < DateOnly.FromDateTime(now.UtcDateTime))
        {
            return Error.Forbidden("license_expired", "The signer's professional license is expired.");
        }

        var v = new ValidationCollector()
            .Required(text, "text").MaxLength(text?.Trim(), 4000, "text")
            .Require((reason?.Trim().Length ?? 0) >= 5, "reason", "Give a reason of at least 5 characters.")
            .MaxLength(reason?.Trim(), 500, "reason");
        if (v.HasErrors)
        {
            return v.ToError();
        }

        var versionNo = latest.VersionNo + 1;
        var snapshot = new SignedNoteSnapshotV1(1, Id, PatientId, versionNo, nameof(NoteVersionKind.Addendum), latest.Id, null, text!.Trim(), reason!.Trim(),
            signer.PractitionerId, signer.DisplayName, signer.CredentialText, now, false, allergyStatus, activeAllergies);
        Version++;
        return NoteVersion.Create(TenantId, snapshot, NoteVersionKind.Addendum, latest.Id);
    }
}

public sealed class NoteDraft
{
    private NoteDraft()
    {
    }

    public Guid EncounterId { get; private set; }
    public Guid TenantId { get; private set; }
    public string ChiefComplaint { get; private set; } = "";
    public string History { get; private set; } = "";
    public string Examination { get; private set; } = "";
    public string Assessment { get; private set; } = "";
    public string Plan { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public int Version { get; private set; }

    public static NoteDraft CreateEmpty(Encounter encounter, Guid actor, DateTimeOffset now) => new()
    {
        EncounterId = encounter.Id,
        TenantId = encounter.TenantId,
        UpdatedAt = now,
        UpdatedBy = actor,
        Version = 1,
    };

    public NoteContentV1 ToContent() => new(ChiefComplaint, History, Examination, Assessment, Plan);

    public Result<NoteDraft> Update(Encounter encounter, NoteContentV1? content, int? expectedVersion, Guid? actorPractitionerId, Guid actor, DateTimeOffset now)
    {
        if (encounter.Status != EncounterStatus.InProgress)
        {
            return Error.Conflict("note_signed", "The note is signed and can no longer be edited. Add an addendum.");
        }

        if (actorPractitionerId != encounter.AttendingPractitionerId)
        {
            return Error.Forbidden("not_attending", "Only the attending clinician may edit this draft.");
        }

        if (expectedVersion is null)
        {
            return Error.Precondition("if_match_required", "If-Match with the current draft version is required.");
        }

        if (expectedVersion != Version)
        {
            return Error.Precondition("draft_conflict", "Someone else saved this draft. Reload to see their changes before saving.");
        }

        if (content is null)
        {
            return Error.Validation("content_required", "Note content is required.");
        }

        var v = new ValidationCollector()
            .MaxLength(content.ChiefComplaint, 1000, "chiefComplaint")
            .MaxLength(content.History, 8000, "history")
            .MaxLength(content.Examination, 8000, "examination")
            .MaxLength(content.Assessment, 4000, "assessment")
            .MaxLength(content.Plan, 4000, "plan");
        if (v.HasErrors)
        {
            return v.ToError();
        }

        ChiefComplaint = content.ChiefComplaint ?? "";
        History = content.History ?? "";
        Examination = content.Examination ?? "";
        Assessment = content.Assessment ?? "";
        Plan = content.Plan ?? "";
        UpdatedAt = now;
        UpdatedBy = actor;
        Version++;
        return this;
    }
}

/// <summary>Immutable signed note version (original or addendum). Never updated after insert (DB-enforced).</summary>
public sealed class NoteVersion
{
    private NoteVersion()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid EncounterId { get; private set; }
    public int VersionNo { get; private set; }
    public NoteVersionKind Kind { get; private set; }
    public Guid? AmendsVersionId { get; private set; }
    public int ContentSchemaVersion { get; private set; }
    public string Content { get; private set; } = "";
    public string ContentSha256 { get; private set; } = "";
    public Guid SignedByUserId { get; private set; }
    public Guid SignedByPractitionerId { get; private set; }
    public string SignerDisplayName { get; private set; } = "";
    public string SignerCredential { get; private set; } = "";
    public DateTimeOffset SignedAt { get; private set; }
    public bool AllergiesReviewed { get; private set; }
    public string? AddendumReason { get; private set; }

    internal static NoteVersion Create(Guid tenantId, SignedNoteSnapshotV1 snapshot, NoteVersionKind kind, Guid? amends)
    {
        var json = CanonicalJson.Serialize(snapshot);
        return new NoteVersion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EncounterId = snapshot.EncounterId,
            VersionNo = snapshot.VersionNo,
            Kind = kind,
            AmendsVersionId = amends,
            ContentSchemaVersion = snapshot.SchemaVersion,
            Content = json,
            ContentSha256 = CanonicalJson.Sha256Hex(json),
            SignedByPractitionerId = snapshot.SignerPractitionerId,
            SignerDisplayName = snapshot.SignerDisplayName,
            SignerCredential = snapshot.SignerCredential,
            SignedAt = snapshot.SignedAtUtc,
            AllergiesReviewed = snapshot.AllergiesReviewed,
            AddendumReason = snapshot.AddendumReason,
        };
    }

    public NoteVersion WithSigningUser(Guid userId)
    {
        SignedByUserId = userId;
        return this;
    }

    public SignedNoteSnapshotV1 ReadSnapshot() => CanonicalJson.Deserialize<SignedNoteSnapshotV1>(Content);

    /// <summary>Re-serializes the stored snapshot canonically and compares to the recorded hash (tamper detection).</summary>
    public bool VerifyIntegrity() =>
        string.Equals(CanonicalJson.Sha256Hex(CanonicalJson.Serialize(ReadSnapshot())), ContentSha256, StringComparison.Ordinal);
}
