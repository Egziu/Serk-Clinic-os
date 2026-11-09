using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Dental.Domain;
using Serk.Clinic.Dental.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Dental.Application;

public sealed record FindingDto(
    Guid Id,
    int ToothCode,
    string ToothName,
    IReadOnlyList<string> Surfaces,
    string Condition,
    string Note,
    Guid EncounterId,
    Guid? CorrectsEntryId,
    string? CorrectionReason,
    DateTimeOffset RecordedAt);

public sealed record DentalChartDto(Guid PatientId, IReadOnlyList<FindingDto> Current, int HistoryCount, string ClinicalReviewStatus);

public sealed class DentalChartService(DbSession session, RequestContext context, AuditTrail audit, IEncounterAccess encounters, TimeProvider clock)
{
    public async Task<Result<DentalChartDto>> GetChartAsync(Guid encounterId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var enc = await encounters.GetAsync(encounterId, ct);
        if (enc is null)
        {
            return Error.NotFound();
        }

        var db = await session.ContextAsync<DentalDbContext>(ct);
        var history = await db.Findings.AsNoTracking().Where(f => f.PatientId == enc.PatientId && f.TenantId == actor.TenantId).ToListAsync(ct);
        await audit.RecordAsync(new AuditEntry("dental.chart.read", "dental_chart", enc.PatientId, enc.PatientId, Metadata: new Dictionary<string, string> { ["encounterId"] = encounterId.ToString() }), ct);
        await session.CommitAsync(ct);
        return new DentalChartDto(enc.PatientId, DentalFinding.CurrentState(history).Select(ToDto).ToList(), history.Count, Contracts.DentalManifest.ClinicalReviewStatus);
    }

    public async Task<Result<IReadOnlyList<FindingDto>>> GetToothHistoryAsync(Guid encounterId, int toothCode, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var enc = await encounters.GetAsync(encounterId, ct);
        if (enc is null)
        {
            return Error.NotFound();
        }

        var db = await session.ContextAsync<DentalDbContext>(ct);
        var rows = await db.Findings.AsNoTracking()
            .Where(f => f.PatientId == enc.PatientId && f.TenantId == actor.TenantId && f.ToothCode == toothCode)
            .OrderBy(f => f.RecordedAt).ToListAsync(ct);
        await audit.RecordAsync(new AuditEntry("dental.tooth_history.read", "dental_chart", enc.PatientId, enc.PatientId), ct);
        await session.CommitAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<FindingDto>> RecordAsync(Guid encounterId, RecordFindingInput input, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var enc = await encounters.GetAsync(encounterId, ct);
        if (enc is null)
        {
            return Error.NotFound();
        }

        if (!enc.IsOpen)
        {
            return Error.Conflict("encounter_signed", "The encounter is signed. Record new findings in a new encounter.");
        }

        if (actor.PractitionerId != enc.AttendingPractitionerId)
        {
            return Error.Forbidden("not_attending", "Only the attending dentist may record findings in this encounter.");
        }

        var db = await session.ContextAsync<DentalDbContext>(ct);
        DentalFinding? corrected = null;
        if (input.CorrectsEntryId is { } cid)
        {
            corrected = await db.Findings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == cid && f.PatientId == enc.PatientId && f.TenantId == actor.TenantId, ct);
            if (corrected is null)
            {
                return Error.Validation("correction_target_not_found", "The entry to correct was not found for this patient.");
            }
        }

        var finding = DentalFinding.Record(input, actor.TenantId, enc.PatientId, encounterId, corrected, actor.UserId, actor.PractitionerId!.Value, clock.GetUtcNow());
        if (!finding.IsSuccess)
        {
            return finding.Error!;
        }

        db.Findings.Add(finding.Value);
        await audit.RecordAsync(new AuditEntry(corrected is null ? "dental.finding.record" : "dental.finding.correct", "dental_finding", finding.Value.Id, enc.PatientId,
            Metadata: new Dictionary<string, string> { ["tooth"] = finding.Value.ToothCode.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct);
        var dto = ToDto(finding.Value);
        try
        {
            await session.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("already_corrected", "That entry was already corrected; reload the chart.");
        }

        return dto;
    }

    private static FindingDto ToDto(DentalFinding f) =>
        new(f.Id, f.ToothCode, ToothCode.Parse(f.ToothCode).Value.DisplayName, f.Surfaces, f.Condition.ToString(), f.Note, f.EncounterId, f.CorrectsEntryId, f.CorrectionReason, f.RecordedAt);
}
