using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Scheduling.Domain;

public enum AppointmentStatus
{
    Booked,
    CheckedIn,
    Cancelled,
    NoShow,
    Completed,
}

/// <summary>
/// Appointment aggregate. Overlap prevention for a practitioner is enforced by a PostgreSQL exclusion constraint
/// (race-safe); the domain enforces shape and state transitions.
/// </summary>
public sealed class Appointment
{
    public const int MinMinutes = 5;
    public const int MaxMinutes = 240;
    public static readonly TimeSpan BackdateTolerance = TimeSpan.FromMinutes(30);

    private Appointment()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid FacilityId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid PractitionerId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public AppointmentStatus Status { get; private set; }
    public string Reason { get; private set; } = "";
    public Guid? EncounterId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? CheckedInAt { get; private set; }
    public int Version { get; private set; }

    public static Result<Appointment> Book(
        Guid tenantId,
        Guid facilityId,
        Guid patientId,
        Guid practitionerId,
        DateTimeOffset? startsAt,
        int durationMinutes,
        string? reason,
        Guid actor,
        DateTimeOffset now)
    {
        var v = new ValidationCollector()
            .Require(startsAt is not null, "startsAt", "Required.")
            .Require(startsAt is null || startsAt.Value >= now - BackdateTolerance, "startsAt", "Cannot book in the past.")
            .Require(durationMinutes is >= MinMinutes and <= MaxMinutes, "durationMinutes", $"Must be {MinMinutes}-{MaxMinutes} minutes.")
            .MaxLength(reason?.Trim(), 300, "reason");
        if (v.HasErrors)
        {
            return v.ToError();
        }

        var start = startsAt!.Value.ToUniversalTime();
        start = new DateTimeOffset(start.Ticks - (start.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        return new Appointment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            FacilityId = facilityId,
            PatientId = patientId,
            PractitionerId = practitionerId,
            StartsAt = start,
            EndsAt = start.AddMinutes(durationMinutes),
            Status = AppointmentStatus.Booked,
            Reason = reason?.Trim() ?? string.Empty,
            CreatedAt = now,
            CreatedBy = actor,
            Version = 1,
        };
    }

    public Result<Appointment> CheckIn(int? expectedVersion, Guid encounterId, DateTimeOffset now)
    {
        if (expectedVersion is not null && expectedVersion != Version)
        {
            return Error.Precondition("version_mismatch", "The appointment changed; reload.");
        }

        if (Status != AppointmentStatus.Booked)
        {
            return Error.Conflict("invalid_transition", $"Cannot check in an appointment in status {Status}.");
        }

        Status = AppointmentStatus.CheckedIn;
        CheckedInAt = now;
        EncounterId = encounterId;
        Version++;
        return this;
    }

    public Result<Appointment> Cancel(int? expectedVersion)
    {
        if (expectedVersion is not null && expectedVersion != Version)
        {
            return Error.Precondition("version_mismatch", "The appointment changed; reload.");
        }

        if (Status != AppointmentStatus.Booked)
        {
            return Error.Conflict("invalid_transition", $"Cannot cancel an appointment in status {Status}.");
        }

        Status = AppointmentStatus.Cancelled;
        Version++;
        return this;
    }
}
