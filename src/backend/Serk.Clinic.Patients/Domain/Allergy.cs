using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Patients.Domain;

public enum AllergySeverity
{
    Mild,
    Moderate,
    Severe,
    Unknown,
}

public enum AllergyRecordStatus
{
    Active,
    Inactive,
    EnteredInError,
}

/// <summary>Structured allergy/intolerance. Substance is free text in this slice; coded terminology is a pending item.</summary>
public sealed class Allergy
{
    private Allergy()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PatientId { get; private set; }
    public string Substance { get; private set; } = "";
    public string Reaction { get; private set; } = "";
    public AllergySeverity Severity { get; private set; }
    public AllergyRecordStatus Status { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public Guid RecordedBy { get; private set; }
    public int Version { get; private set; }

    public static Result<Allergy> Record(Patient patient, string? substance, string? reaction, AllergySeverity? severity, Guid actor, DateTimeOffset now)
    {
        var v = new ValidationCollector()
            .Required(substance, "substance").MaxLength(substance?.Trim(), 200, "substance")
            .MaxLength(reaction?.Trim(), 500, "reaction")
            .Require(severity is not null, "severity", "Required (use Unknown if not known).");
        if (v.HasErrors)
        {
            return v.ToError();
        }

        patient.MarkHasAllergies(actor, now);
        return new Allergy
        {
            Id = Guid.CreateVersion7(),
            TenantId = patient.TenantId,
            PatientId = patient.Id,
            Substance = substance!.Trim(),
            Reaction = reaction?.Trim() ?? string.Empty,
            Severity = severity!.Value,
            Status = AllergyRecordStatus.Active,
            RecordedAt = now,
            RecordedBy = actor,
            Version = 1,
        };
    }
}
