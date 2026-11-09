using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Documents.Contracts;
using Serk.Clinic.Documents.Domain;
using Serk.Clinic.Documents.Infrastructure;
using Serk.Clinic.Documents.Rendering;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Patients.Contracts;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Idempotency;
using Serk.Clinic.Platform.Storage;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Application;

public sealed class DocumentOptions
{
    /// <summary>Public base URL used in QR verification links. Must not contain tenant or patient identifiers.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";
}

public sealed record CreateDocumentRequest(Guid NoteVersionId, PaperSize? PaperSize);

public sealed record DocumentDto(
    Guid Id,
    Guid EncounterId,
    Guid NoteVersionId,
    string TemplateCode,
    int TemplateVersion,
    string PaperSize,
    string Status,
    string SnapshotSha256,
    string VerificationRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinalizedAt,
    string? PdfSha256,
    Guid? SupersedesId);

public sealed record RenderedPdf(byte[] Bytes, string FileName, string SnapshotSha256, int TemplateVersion, string? PdfSha256);

public sealed record PrintEventRequest(string? Kind);

public sealed record VerificationDto(bool Valid, string? FacilityName, string? TemplateCode, int? TemplateVersion, string? Status, DateTimeOffset? FinalizedAt, string? PdfSha256);

public sealed class DocumentService(
    DbSession session,
    RequestContext context,
    AuditTrail audit,
    IdempotencyStore idempotency,
    ISignedNoteReader notes,
    IPatientDirectory patients,
    IStaffDirectory staff,
    IObjectStore objects,
    IOptions<DocumentOptions> options,
    TimeProvider clock)
{
    /// <summary>Print kinds the browser can truthfully report. Physical printing can never be confirmed by the browser.</summary>
    public static readonly IReadOnlySet<string> PrintKinds = new HashSet<string>(StringComparer.Ordinal) { "PrintDialogOpened", "PrintFailureReported" };

    public async Task<Result<DocumentDto>> CreateAsync(CreateDocumentRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var chain = await notes.GetSignedChainAsync(request.NoteVersionId, ct);
        if (chain is null || chain.Count == 0)
        {
            return Error.NotFound();
        }

        var last = chain[^1];
        var banner = await patients.GetBannerAsync(last.PatientId, ct);
        var facility = await staff.GetFacilityAsync(actor.TenantId, last.FacilityId, ct);
        if (banner is null || facility is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        var reference = NewReference();
        var paper = request.PaperSize ?? PaperSize.A4;
        var signedDate = DateOnly.FromDateTime(chain[0].Snapshot.SignedAtUtc.UtcDateTime);
        var snapshot = new ConsultationDocumentSnapshotV1(
            1,
            ConsultationSummaryRenderer.TemplateCode,
            ConsultationSummaryRenderer.TemplateVersion,
            paper.ToString(),
            reference,
            options.Value.PublicBaseUrl.TrimEnd('/') + "/verify/" + reference,
            new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero),
            new FacilityBlock(facility.Name, facility.AddressLine, facility.Phone, facility.LicenseNumber, facility.TimeZone),
            new PatientBlock(banner.Mrn, banner.DisplayName, banner.Sex, banner.BirthDate, banner.BirthDateIsEstimated, AgeYears(banner.BirthDate, signedDate)),
            last.EncounterId,
            last.EncounterStartedAtUtc,
            chain.Select(c => new SignedPart(c.NoteVersionId, c.VersionNo, c.ContentSha256, c.Snapshot)).ToList());

        var json = CanonicalJson.Serialize(snapshot);
        var doc = DocumentVersion.CreateDraft(Guid.CreateVersion7(), actor.TenantId, last.FacilityId, last.PatientId, last.EncounterId, last.NoteVersionId,
            snapshot.TemplateCode, snapshot.TemplateVersion, paper, json, reference, actor.UserId, now);
        var db = await session.ContextAsync<DocumentsDbContext>(ct);
        db.Documents.Add(doc);
        await audit.RecordAsync(new AuditEntry("document.create", "document", doc.Id, doc.PatientId, Metadata: new Dictionary<string, string> { ["snapshotSha256"] = doc.SnapshotSha256 }), ct);
        await session.CommitAsync(ct);
        return ToDto(doc);
    }

    public async Task<Result<IReadOnlyList<DocumentDto>>> ListForEncounterAsync(Guid encounterId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<DocumentsDbContext>(ct);
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.EncounterId == encounterId && d.TenantId == actor.TenantId && d.FacilityId == actor.FacilityId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return docs.Select(ToDto).ToList();
    }

    public async Task<Result<RenderedPdf>> PreviewAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<DocumentsDbContext>(ct);
        var doc = await FindAsync(db, id, actor, tracking: false, ct);
        if (doc is null)
        {
            return Error.NotFound();
        }

        var bytes = ConsultationSummaryRenderer.Render(ReadSnapshot(doc), RenderMode.Preview);
        await audit.RecordAsync(new AuditEntry("document.preview", "document", doc.Id, doc.PatientId), ct);
        await session.CommitAsync(ct);
        return new RenderedPdf(bytes, $"preview-{doc.VerificationRef}.pdf", doc.SnapshotSha256, doc.TemplateVersion, null);
    }

    public async Task<Result<DocumentDto>> FinalizeAsync(Guid id, string? idempotencyKey, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var result = await idempotency.ExecuteAsync($"document.finalize:{id}", idempotencyKey, new { id }, async innerCt =>
        {
            var db = await session.ContextAsync<DocumentsDbContext>(innerCt);
            var doc = await FindAsync(db, id, actor, tracking: true, innerCt);
            if (doc is null)
            {
                return Error.NotFound();
            }

            if (doc.Status != DocumentStatus.Draft)
            {
                return Error.Conflict("already_final", "This document is already finalized.");
            }

            var pdf = ConsultationSummaryRenderer.Render(ReadSnapshot(doc), RenderMode.Final);
            var key = $"documents/{doc.TenantId:N}/{Guid.NewGuid():N}.pdf";
            var previous = await db.Documents
                .Where(d => d.EncounterId == doc.EncounterId && d.TemplateCode == doc.TemplateCode && d.Status == DocumentStatus.Final && d.Id != doc.Id)
                .ToListAsync(innerCt);
            var finalized = doc.Finalize(key, pdf, previous.OrderByDescending(p => p.FinalizedAt).FirstOrDefault()?.Id, actor.UserId, clock.GetUtcNow());
            if (!finalized.IsSuccess)
            {
                return finalized.Error!;
            }

            foreach (var p in previous)
            {
                p.MarkSuperseded();
            }

            // Write-once object first; if the DB commit later fails the orphan object is harmless (unreferenced).
            await objects.PutAsync(key, pdf, innerCt);
            await audit.RecordAsync(new AuditEntry("document.finalize", "document", doc.Id, doc.PatientId, Metadata: new Dictionary<string, string> { ["pdfSha256"] = doc.PdfSha256!, ["snapshotSha256"] = doc.SnapshotSha256 }), innerCt);
            return Result<DocumentDto>.Ok(ToDto(doc));
        }, ct);

        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            await session.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrent_finalize", "The document was finalized concurrently; reload.");
        }

        return result;
    }

    public async Task<Result<RenderedPdf>> DownloadAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<DocumentsDbContext>(ct);
        var doc = await FindAsync(db, id, actor, tracking: false, ct);
        if (doc is null)
        {
            return Error.NotFound();
        }

        if (doc.Status == DocumentStatus.Draft)
        {
            return Error.Rule("not_final", "Only finalized documents can be downloaded. Use preview for drafts.");
        }

        byte[] bytes;
        try
        {
            bytes = await objects.GetVerifiedAsync(doc.PdfObjectKey!, doc.PdfSha256!, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
        {
            // Never serve a document whose stored bytes do not match the hash recorded at finalization.
            await audit.RecordSecurityEventAsync("document.integrity_failure", ex.GetType().Name, "document", doc.Id, ct);
            return Error.Conflict("document_integrity_failure", "The stored document failed integrity verification and was not served. Contact the administrator.");
        }

        await audit.RecordAsync(new AuditEntry("document.download", "document", doc.Id, doc.PatientId, Metadata: new Dictionary<string, string> { ["status"] = doc.Status.ToString() }), ct);
        await session.CommitAsync(ct);
        return new RenderedPdf(bytes, $"consultation-{doc.VerificationRef}.pdf", doc.SnapshotSha256, doc.TemplateVersion, doc.PdfSha256);
    }

    public async Task<Result<bool>> RecordPrintEventAsync(Guid id, PrintEventRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (request.Kind is null || !PrintKinds.Contains(request.Kind))
        {
            return Error.Validation("invalid_print_kind", "Kind must be PrintDialogOpened or PrintFailureReported. Physical printing cannot be confirmed by the browser.");
        }

        var db = await session.ContextAsync<DocumentsDbContext>(ct);
        var doc = await FindAsync(db, id, actor, tracking: false, ct);
        if (doc is null)
        {
            return Error.NotFound();
        }

        if (doc.Status != DocumentStatus.Final)
        {
            return Error.Rule("not_final", "Only the current final document may be printed.");
        }

        await audit.RecordAsync(new AuditEntry("document.print." + request.Kind, "document", doc.Id, doc.PatientId), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<VerificationDto> VerifyAsync(string reference, CancellationToken ct)
    {
        if (reference.Length is < 16 or > 64 || !reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return new VerificationDto(false, null, null, null, null, null, null);
        }

        await using var cmd = await session.CommandAsync("SELECT facility_name, template_code, template_version, status, finalized_at, pdf_sha256 FROM documents.verify_reference(@r)", ct);
        cmd.Parameters.AddWithValue("r", reference);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new VerificationDto(false, null, null, null, null, null, null);
        }

        return new VerificationDto(true, reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5));
    }

    private static async Task<DocumentVersion?> FindAsync(DocumentsDbContext db, Guid id, StaffActor actor, bool tracking, CancellationToken ct)
    {
        var q = tracking ? db.Documents : db.Documents.AsNoTracking();
        return await q.SingleOrDefaultAsync(d => d.Id == id && d.TenantId == actor.TenantId && d.FacilityId == actor.FacilityId, ct);
    }

    private static ConsultationDocumentSnapshotV1 ReadSnapshot(DocumentVersion doc)
    {
        var snapshot = CanonicalJson.Deserialize<ConsultationDocumentSnapshotV1>(doc.Snapshot);
        if (!string.Equals(CanonicalJson.Sha256Hex(CanonicalJson.Serialize(snapshot)), doc.SnapshotSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Document snapshot failed integrity verification.");
        }

        return snapshot;
    }

    private static string NewReference() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int AgeYears(DateOnly birth, DateOnly at)
    {
        var age = at.Year - birth.Year;
        if (at < birth.AddYears(age))
        {
            age--;
        }

        return Math.Max(0, age);
    }

    private static DocumentDto ToDto(DocumentVersion d) =>
        new(d.Id, d.EncounterId, d.NoteVersionId, d.TemplateCode, d.TemplateVersion, d.PaperSize.ToString(), d.Status.ToString(), d.SnapshotSha256, d.VerificationRef, d.CreatedAt, d.FinalizedAt, d.PdfSha256, d.SupersedesId);
}
