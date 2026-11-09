using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Domain;

public enum DocumentStatus
{
    Draft,
    Final,
    Superseded,
}

/// <summary>Paper sizes allowed for clinical documents (archival, standard printers only — never thermal).</summary>
public enum PaperSize
{
    A4,
    A5,
    A3,
}

/// <summary>
/// A document bound at creation to an exact data snapshot (hash) and template version. Preview and final PDF are both
/// rendered from that snapshot by the same renderer; finalization stores the PDF bytes + hash and freezes the record.
/// </summary>
public sealed class DocumentVersion
{
    private DocumentVersion()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid FacilityId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid EncounterId { get; private set; }
    public Guid NoteVersionId { get; private set; }
    public string TemplateCode { get; private set; } = "";
    public int TemplateVersion { get; private set; }
    public PaperSize PaperSize { get; private set; }
    public string Snapshot { get; private set; } = "";
    public string SnapshotSha256 { get; private set; } = "";
    public DocumentStatus Status { get; private set; }
    public string VerificationRef { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }
    public Guid? FinalizedBy { get; private set; }
    public string? PdfObjectKey { get; private set; }
    public string? PdfSha256 { get; private set; }
    public int? PdfSizeBytes { get; private set; }
    public Guid? SupersedesId { get; private set; }
    public int Version { get; private set; }

    public static DocumentVersion CreateDraft(
        Guid id,
        Guid tenantId,
        Guid facilityId,
        Guid patientId,
        Guid encounterId,
        Guid noteVersionId,
        string templateCode,
        int templateVersion,
        PaperSize paperSize,
        string canonicalSnapshotJson,
        string verificationRef,
        Guid actor,
        DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = tenantId,
            FacilityId = facilityId,
            PatientId = patientId,
            EncounterId = encounterId,
            NoteVersionId = noteVersionId,
            TemplateCode = templateCode,
            TemplateVersion = templateVersion,
            PaperSize = paperSize,
            Snapshot = canonicalSnapshotJson,
            SnapshotSha256 = CanonicalJson.Sha256Hex(canonicalSnapshotJson),
            Status = DocumentStatus.Draft,
            VerificationRef = verificationRef,
            CreatedAt = now,
            CreatedBy = actor,
            Version = 1,
        };

    public Result<DocumentVersion> Finalize(string objectKey, byte[] pdf, Guid? supersedesId, Guid actor, DateTimeOffset now)
    {
        if (Status != DocumentStatus.Draft)
        {
            return Error.Conflict("already_final", "This document is already finalized.");
        }

        PdfObjectKey = objectKey;
        PdfSha256 = CanonicalJson.Sha256Hex(pdf);
        PdfSizeBytes = pdf.Length;
        FinalizedAt = now;
        FinalizedBy = actor;
        SupersedesId = supersedesId;
        Status = DocumentStatus.Final;
        Version++;
        return this;
    }

    public void MarkSuperseded()
    {
        if (Status == DocumentStatus.Final)
        {
            Status = DocumentStatus.Superseded;
            Version++;
        }
    }
}
