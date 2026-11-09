using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Clinical.Domain;
using Serk.Clinic.Clinical.Infrastructure;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Patients.Contracts;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Idempotency;
using Serk.Clinic.Platform.Outbox;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Clinical.Application;

public sealed record NoteDraftDto(NoteContentV1 Content, int Version, DateTimeOffset UpdatedAt);

public sealed record NoteVersionDto(
    Guid Id,
    int VersionNo,
    string Kind,
    Guid? AmendsVersionId,
    string ContentSha256,
    bool IntegrityVerified,
    string SignerDisplayName,
    string SignerCredential,
    DateTimeOffset SignedAt,
    SignedNoteSnapshotV1 Snapshot);

public sealed record EncounterDetailDto(
    Guid Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? SignedAt,
    Guid AttendingPractitionerId,
    bool IsAttending,
    PatientBanner Patient,
    NoteDraftDto? Draft,
    IReadOnlyList<NoteVersionDto> SignedVersions,
    int Version);

public sealed record EncounterListItemDto(Guid Id, string Status, DateTimeOffset StartedAt, Guid PatientId, string PatientMrn, string PatientDisplayName, string AllergyStatus);

/// <param name="AllergyReviewToken">The <c>patient.allergyReviewToken</c> displayed when the clinician confirmed allergy review.</param>
public sealed record SignRequest(int DraftVersion, bool AllergiesReviewed, string? AllergyReviewToken);

public sealed record AddendumRequest(string? Text, string? Reason);

public sealed record SignResultDto(Guid NoteVersionId, int VersionNo, string ContentSha256, DateTimeOffset SignedAt);

public sealed class EncounterService(
    DbSession session,
    RequestContext context,
    AuditTrail audit,
    IdempotencyStore idempotency,
    OutboxWriter outbox,
    IPatientDirectory patients,
    IStaffDirectory staff,
    TimeProvider clock) : IEncounterLauncher, ISignedNoteReader
{
    public const string NoteSignedMessage = "clinical.note-signed.v1";

    public async Task<Guid> StartEncounterAsync(Guid appointmentId, Guid patientId, Guid attendingPractitionerId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (!actor.Has(Capabilities.AppointmentCheckIn))
        {
            throw new UnauthorizedAccessException("Starting an encounter requires the check-in capability.");
        }

        var db = await session.ContextAsync<ClinicalDbContext>(ct);
        var now = Now();
        var encounter = Encounter.Start(actor.TenantId, actor.FacilityId, patientId, appointmentId, attendingPractitionerId, now);
        db.Encounters.Add(encounter);
        db.NoteDrafts.Add(NoteDraft.CreateEmpty(encounter, actor.UserId, now));
        await audit.RecordAsync(new AuditEntry("encounter.start", "encounter", encounter.Id, patientId), ct);
        return encounter.Id;
    }

    public async Task<Result<IReadOnlyList<EncounterListItemDto>>> ListWorklistAsync(DateOnly? day, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ClinicalDbContext>(ct);
        var facility = await staff.GetFacilityAsync(actor.TenantId, actor.FacilityId, ct);
        var zone = BusinessTime.Resolve(facility?.TimeZone);
        var date = day ?? BusinessTime.LocalDate(Now(), zone);
        var (from, to) = BusinessTime.DayRangeUtc(date, zone);
        var query = db.Encounters.AsNoTracking()
            .Where(e => e.TenantId == actor.TenantId && e.FacilityId == actor.FacilityId && e.StartedAt >= from && e.StartedAt < to);
        if (actor.PractitionerId is { } pid && actor.Has(Capabilities.NoteSign))
        {
            query = query.Where(e => e.AttendingPractitionerId == pid);
        }

        var encounters = await query.OrderBy(e => e.StartedAt).Take(100).ToListAsync(ct);
        var items = new List<EncounterListItemDto>(encounters.Count);
        foreach (var e in encounters)
        {
            var banner = await patients.GetBannerAsync(e.PatientId, ct);
            if (banner is not null)
            {
                items.Add(new EncounterListItemDto(e.Id, e.Status.ToString(), e.StartedAt, e.PatientId, banner.Mrn, banner.DisplayName, banner.AllergyStatus));
            }
        }

        return items;
    }

    public async Task<Result<EncounterDetailDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ClinicalDbContext>(ct);
        var encounter = await FindAccessibleAsync(db, id, actor, tracking: false, ct);
        if (encounter is null)
        {
            return Error.NotFound();
        }

        var banner = await patients.GetBannerAsync(encounter.PatientId, ct);
        var draft = encounter.Status == EncounterStatus.InProgress
            ? await db.NoteDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.EncounterId == id, ct)
            : null;
        var versions = await db.NoteVersions.AsNoTracking().Where(v => v.EncounterId == id).OrderBy(v => v.VersionNo).ToListAsync(ct);
        await audit.RecordAsync(new AuditEntry("encounter.read", "encounter", id, encounter.PatientId), ct);
        await session.CommitAsync(ct);
        return new EncounterDetailDto(
            encounter.Id,
            encounter.Status.ToString(),
            encounter.StartedAt,
            encounter.SignedAt,
            encounter.AttendingPractitionerId,
            actor.PractitionerId == encounter.AttendingPractitionerId,
            banner!,
            draft is null ? null : new NoteDraftDto(draft.ToContent(), draft.Version, draft.UpdatedAt),
            versions.Select(ToDto).ToList(),
            encounter.Version);
    }

    public async Task<Result<NoteDraftDto>> SaveDraftAsync(Guid id, NoteContentV1? content, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ClinicalDbContext>(ct);
        var encounter = await FindAccessibleAsync(db, id, actor, tracking: false, ct);
        if (encounter is null)
        {
            return Error.NotFound();
        }

        var draft = await db.NoteDrafts.SingleAsync(d => d.EncounterId == id, ct);
        var updated = draft.Update(encounter, content, ifMatch, actor.PractitionerId, actor.UserId, Now());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        try
        {
            await session.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Precondition("draft_conflict", "Someone else saved this draft. Reload to see their changes before saving.");
        }

        return new NoteDraftDto(draft.ToContent(), draft.Version, draft.UpdatedAt);
    }

    public async Task<Result<SignResultDto>> SignAsync(Guid id, SignRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var result = await idempotency.ExecuteAsync($"encounter.sign:{id}", idempotencyKey, request, async innerCt =>
        {
            var db = await session.ContextAsync<ClinicalDbContext>(innerCt);
            var encounter = await FindAccessibleAsync(db, id, actor, tracking: true, innerCt);
            if (encounter is null)
            {
                return Error.NotFound();
            }

            var signer = await ResolveSignerAsync(actor, innerCt);
            if (!signer.IsSuccess)
            {
                return signer.Error!;
            }

            var draft = await db.NoteDrafts.SingleAsync(d => d.EncounterId == id, innerCt);
            var banner = (await patients.GetBannerAsync(encounter.PatientId, innerCt))!;
            if (!string.Equals(request.AllergyReviewToken, banner.AllergyReviewToken, StringComparison.Ordinal))
            {
                return Error.Conflict("allergies_changed", "The patient's allergy information changed since you reviewed it. Review allergies again before signing.");
            }

            var signed = encounter.Sign(draft, request.DraftVersion, request.AllergiesReviewed, signer.Value, banner.AllergyStatus, Allergies(banner), Now());
            if (!signed.IsSuccess)
            {
                return signed.Error!;
            }

            var version = signed.Value.WithSigningUser(actor.UserId);
            db.NoteVersions.Add(version);
            await audit.RecordAsync(new AuditEntry("note.sign", "note_version", version.Id, encounter.PatientId, Metadata: new Dictionary<string, string> { ["sha256"] = version.ContentSha256 }), innerCt);
            await outbox.EnqueueAsync(NoteSignedMessage, new { encounterId = encounter.Id, noteVersionId = version.Id }, innerCt);
            return Result<SignResultDto>.Ok(new SignResultDto(version.Id, version.VersionNo, version.ContentSha256, version.SignedAt));
        }, ct);

        return await CommitMappingConflictsAsync(result, ct);
    }

    public async Task<Result<SignResultDto>> AddAddendumAsync(Guid id, AddendumRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var result = await idempotency.ExecuteAsync($"encounter.addendum:{id}", idempotencyKey, request, async innerCt =>
        {
            var db = await session.ContextAsync<ClinicalDbContext>(innerCt);
            var encounter = await FindAccessibleAsync(db, id, actor, tracking: true, innerCt);
            if (encounter is null)
            {
                return Error.NotFound();
            }

            var signer = await ResolveSignerAsync(actor, innerCt);
            if (!signer.IsSuccess)
            {
                return signer.Error!;
            }

            var latest = await db.NoteVersions.AsNoTracking().Where(v => v.EncounterId == id).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(innerCt);
            if (latest is null)
            {
                return Error.Rule("not_signed", "Addenda can only be added to a signed note.");
            }

            var banner = (await patients.GetBannerAsync(encounter.PatientId, innerCt))!;
            var addendum = encounter.AddAddendum(latest, request.Text, request.Reason, signer.Value, banner.AllergyStatus, Allergies(banner), Now());
            if (!addendum.IsSuccess)
            {
                return addendum.Error!;
            }

            var version = addendum.Value.WithSigningUser(actor.UserId);
            db.NoteVersions.Add(version);
            await audit.RecordAsync(new AuditEntry("note.addendum", "note_version", version.Id, encounter.PatientId, Reason: "addendum"), innerCt);
            await outbox.EnqueueAsync(NoteSignedMessage, new { encounterId = encounter.Id, noteVersionId = version.Id }, innerCt);
            return Result<SignResultDto>.Ok(new SignResultDto(version.Id, version.VersionNo, version.ContentSha256, version.SignedAt));
        }, ct);

        return await CommitMappingConflictsAsync(result, ct);
    }

    public async Task<IReadOnlyList<SignedNoteView>?> GetSignedChainAsync(Guid noteVersionId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ClinicalDbContext>(ct);
        var target = await db.NoteVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == noteVersionId && v.TenantId == actor.TenantId, ct);
        if (target is null)
        {
            return null;
        }

        var encounter = await FindAccessibleAsync(db, target.EncounterId, actor, tracking: false, ct);
        if (encounter is null)
        {
            return null;
        }

        var chain = await db.NoteVersions.AsNoTracking()
            .Where(v => v.EncounterId == target.EncounterId && v.VersionNo <= target.VersionNo)
            .OrderBy(v => v.VersionNo)
            .ToListAsync(ct);
        if (chain.Any(v => !v.VerifyIntegrity()))
        {
            throw new InvalidOperationException("Signed note integrity verification failed.");
        }

        return chain.Select(v => new SignedNoteView(v.Id, v.TenantId, encounter.FacilityId, encounter.Id, encounter.PatientId, encounter.StartedAt, v.VersionNo, v.ContentSha256, v.ReadSnapshot())).ToList();
    }

    public async Task<Guid?> GetLatestSignedVersionIdAsync(Guid encounterId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<ClinicalDbContext>(ct);
        if (await FindAccessibleAsync(db, encounterId, actor, tracking: false, ct) is null)
        {
            return null;
        }

        return await db.NoteVersions.AsNoTracking().Where(v => v.EncounterId == encounterId).OrderByDescending(v => v.VersionNo).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<Result<T>> CommitMappingConflictsAsync<T>(Result<T> result, CancellationToken ct)
    {
        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            await session.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrent_change", "The encounter changed concurrently; reload and retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("concurrent_signature", "Another signature was recorded concurrently; reload.");
        }
    }

    private async Task<Result<SignerCredentials>> ResolveSignerAsync(StaffActor actor, CancellationToken ct)
    {
        if (actor.PractitionerId is not { } pid)
        {
            return Error.Forbidden("not_practitioner", "Only registered practitioners may sign.");
        }

        if (!await staff.IsPractitionerActiveAtFacilityAsync(actor.TenantId, actor.FacilityId, pid, ct))
        {
            return Error.Forbidden("inactive_affiliation", "The practitioner has no active affiliation at this facility.");
        }

        var p = await staff.GetPractitionerAsync(actor.TenantId, pid, ct);
        return p is null
            ? Error.Forbidden("not_practitioner", "Only registered practitioners may sign.")
            : new SignerCredentials(p.Id, p.DisplayName, p.ProfessionalTitle, p.LicenseNumber, p.LicenseExpiresOn);
    }

    /// <summary>ABAC: same tenant (also RLS) AND same facility as the caller's active affiliation. Otherwise "not found".</summary>
    private static async Task<Encounter?> FindAccessibleAsync(ClinicalDbContext db, Guid id, StaffActor actor, bool tracking, CancellationToken ct)
    {
        var q = tracking ? db.Encounters : db.Encounters.AsNoTracking();
        return await q.SingleOrDefaultAsync(e => e.Id == id && e.TenantId == actor.TenantId && e.FacilityId == actor.FacilityId, ct);
    }

    private static List<AllergySnapshot> Allergies(PatientBanner banner) =>
        banner.ActiveAllergies.Select(a => new AllergySnapshot(a.Substance, a.Reaction, a.Severity)).ToList();

    private static NoteVersionDto ToDto(NoteVersion v) =>
        new(v.Id, v.VersionNo, v.Kind.ToString(), v.AmendsVersionId, v.ContentSha256, v.VerifyIntegrity(), v.SignerDisplayName, v.SignerCredential, v.SignedAt, v.ReadSnapshot());

    /// <summary>UTC now truncated to microseconds so the snapshot timestamp equals the PostgreSQL timestamptz value.</summary>
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }
}
