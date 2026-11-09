using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Dental.Domain;

/// <summary>Condition vocabulary v1 — PROVISIONAL, pending dental clinician review (not a coded terminology).</summary>
public enum ToothCondition
{
    Sound,
    Caries,
    Restored,
    Crown,
    RootCanalTreated,
    Fractured,
    Sealant,
    Missing,
}

public sealed record RecordFindingInput(
    int? ToothCode,
    int? ConfirmToothCode,
    IReadOnlyList<ToothSurface>? Surfaces,
    ToothCondition? Condition,
    string? Note,
    Guid? CorrectsEntryId,
    string? CorrectionReason);

/// <summary>
/// Append-only dental chart entry attached to an encounter (specialty extension of the core encounter, never a copy of it).
/// A correction is a new entry that references the entry it replaces; history is never rewritten (DB-enforced).
/// </summary>
public sealed class DentalFinding
{
    public const int MaxNote = 500;

    private DentalFinding()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid EncounterId { get; private set; }
    public int ToothCode { get; private set; }
    public string[] Surfaces { get; private set; } = [];
    public ToothCondition Condition { get; private set; }
    public string Note { get; private set; } = "";
    public Guid? CorrectsEntryId { get; private set; }
    public string? CorrectionReason { get; private set; }
    public Guid RecordedByUserId { get; private set; }
    public Guid RecordedByPractitionerId { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    /// <param name="corrected">The entry being corrected (must be the same tooth's current entry for this patient), if any.</param>
    public static Result<DentalFinding> Record(
        RecordFindingInput input,
        Guid tenantId,
        Guid patientId,
        Guid encounterId,
        DentalFinding? corrected,
        Guid userId,
        Guid practitionerId,
        DateTimeOffset now)
    {
        var tooth = Domain.ToothCode.Parse(input.ToothCode);
        if (!tooth.IsSuccess)
        {
            return tooth.Error!;
        }

        // Wrong-tooth prevention (DENT-003): the tooth number must be entered twice, independently, and match.
        if (input.ConfirmToothCode != input.ToothCode)
        {
            return Error.Validation("tooth_confirmation_mismatch", "The confirmed tooth number does not match the selected tooth. Check the tooth before recording.",
                new Dictionary<string, string[]> { ["confirmToothCode"] = ["Must equal the selected tooth number."] });
        }

        var surfaces = (input.Surfaces ?? []).Distinct().ToList();
        var v = new ValidationCollector()
            .Require(input.Condition is not null, "condition", "Required.")
            .Require(surfaces.All(s => tooth.Value.ValidSurfaces.Contains(s)), "surfaces",
                tooth.Value.IsAnterior ? "Anterior teeth have an incisal edge, not an occlusal surface." : "Posterior teeth have an occlusal surface, not an incisal edge.")
            .Require(input.Condition is not (ToothCondition.Missing or ToothCondition.Sound) || surfaces.Count == 0, "surfaces", "Surfaces do not apply to a missing or sound tooth.")
            .Require(input.Condition is not (ToothCondition.Caries or ToothCondition.Restored or ToothCondition.Sealant) || surfaces.Count > 0, "surfaces", "Select at least one affected surface.")
            .MaxLength(input.Note?.Trim(), MaxNote, "note");
        if (corrected is not null)
        {
            v.Require(corrected.ToothCode == tooth.Value.Code, "correctsEntryId", "A correction must refer to an entry for the same tooth.")
             .Require((input.CorrectionReason?.Trim().Length ?? 0) >= 5, "correctionReason", "Give a reason of at least 5 characters.")
             .MaxLength(input.CorrectionReason?.Trim(), 300, "correctionReason");
        }

        if (v.HasErrors)
        {
            return v.ToError();
        }

        return new DentalFinding
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PatientId = patientId,
            EncounterId = encounterId,
            ToothCode = tooth.Value.Code,
            Surfaces = surfaces.Order().Select(s => s.ToString()).ToArray(),
            Condition = input.Condition!.Value,
            Note = input.Note?.Trim() ?? "",
            CorrectsEntryId = corrected?.Id,
            CorrectionReason = corrected is null ? null : input.CorrectionReason!.Trim(),
            RecordedByUserId = userId,
            RecordedByPractitionerId = practitionerId,
            RecordedAt = now,
        };
    }

    /// <summary>Current chart = per tooth, the latest entry that has not been corrected by a later entry.</summary>
    public static IReadOnlyList<DentalFinding> CurrentState(IEnumerable<DentalFinding> history)
    {
        var list = history.ToList();
        var corrected = list.Where(f => f.CorrectsEntryId is not null).Select(f => f.CorrectsEntryId!.Value).ToHashSet();
        return list.Where(f => !corrected.Contains(f.Id))
            .GroupBy(f => f.ToothCode)
            .Select(g => g.OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id).First())
            .OrderBy(f => f.ToothCode)
            .ToList();
    }
}
