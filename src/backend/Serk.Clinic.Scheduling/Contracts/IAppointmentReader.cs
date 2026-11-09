namespace Serk.Clinic.Scheduling.Contracts;

public sealed record AppointmentSlipView(Guid AppointmentId, Guid FacilityId, Guid PatientId, Guid PractitionerId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);

/// <summary>Published read contract of Scheduling (tenant + facility scoped by the caller's context).</summary>
public interface IAppointmentReader
{
    Task<AppointmentSlipView?> GetAsync(Guid appointmentId, CancellationToken ct);
}
