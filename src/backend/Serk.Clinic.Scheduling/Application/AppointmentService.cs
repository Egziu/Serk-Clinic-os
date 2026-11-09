using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Patients.Contracts;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Idempotency;
using Serk.Clinic.Scheduling.Domain;
using Serk.Clinic.Scheduling.Infrastructure;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Scheduling.Application;

public sealed record BookAppointmentRequest(Guid PatientId, Guid PractitionerId, DateTimeOffset? StartsAt, int DurationMinutes, string? Reason);

public sealed record AppointmentDto(
    Guid Id,
    Guid PatientId,
    string PatientMrn,
    string PatientDisplayName,
    Guid PractitionerId,
    string PractitionerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string Reason,
    Guid? EncounterId,
    int Version);

public sealed class AppointmentService(
    DbSession session,
    RequestContext context,
    AuditTrail audit,
    IdempotencyStore idempotency,
    IPatientDirectory patients,
    IStaffDirectory staff,
    IEncounterLauncher encounters,
    TimeProvider clock)
{
    public async Task<Result<AppointmentDto>> BookAsync(BookAppointmentRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var result = await idempotency.ExecuteAsync("appointment.book", idempotencyKey, request, async innerCt =>
        {
            if (!await patients.ExistsAsync(request.PatientId, innerCt))
            {
                return Error.Validation("patient_not_found", "Patient not found.", new Dictionary<string, string[]> { ["patientId"] = ["Unknown patient."] });
            }

            if (!await staff.IsPractitionerActiveAtFacilityAsync(actor.TenantId, actor.FacilityId, request.PractitionerId, innerCt))
            {
                return Error.Validation("practitioner_unavailable", "Practitioner is not active at this facility.", new Dictionary<string, string[]> { ["practitionerId"] = ["Not available at this facility."] });
            }

            var booked = Appointment.Book(actor.TenantId, actor.FacilityId, request.PatientId, request.PractitionerId, request.StartsAt, request.DurationMinutes, request.Reason, actor.UserId, clock.GetUtcNow());
            if (!booked.IsSuccess)
            {
                return booked.Error!;
            }

            var db = await session.ContextAsync<SchedulingDbContext>(innerCt);
            db.Appointments.Add(booked.Value);
            await audit.RecordAsync(new AuditEntry("appointment.book", "appointment", booked.Value.Id, request.PatientId), innerCt);
            return Result<AppointmentDto>.Ok(await ToDtoAsync(booked.Value, innerCt));
        }, ct);

        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            await session.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            return Error.Conflict("slot_unavailable", "The practitioner already has an appointment overlapping this time.");
        }
    }

    public async Task<Result<IReadOnlyList<AppointmentDto>>> ListDayAsync(DateOnly? day, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var facility = await staff.GetFacilityAsync(actor.TenantId, actor.FacilityId, ct);
        var zone = BusinessTime.Resolve(facility?.TimeZone);
        var date = day ?? BusinessTime.LocalDate(clock.GetUtcNow(), zone);
        var (from, to) = BusinessTime.DayRangeUtc(date, zone);
        var db = await session.ContextAsync<SchedulingDbContext>(ct);
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.FacilityId == actor.FacilityId && a.StartsAt >= from && a.StartsAt < to)
            .OrderBy(a => a.StartsAt)
            .Take(500)
            .ToListAsync(ct);
        var list = new List<AppointmentDto>(rows.Count);
        foreach (var a in rows)
        {
            list.Add(await ToDtoAsync(a, ct));
        }

        return list;
    }

    public async Task<Result<AppointmentDto>> CheckInAsync(Guid id, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<SchedulingDbContext>(ct);
        var appt = await db.Appointments.SingleOrDefaultAsync(a => a.Id == id && a.TenantId == actor.TenantId && a.FacilityId == actor.FacilityId, ct);
        if (appt is null)
        {
            return Error.NotFound();
        }

        if (appt.Status != AppointmentStatus.Booked)
        {
            return Error.Conflict("invalid_transition", $"Cannot check in an appointment in status {appt.Status}.");
        }

        var encounterId = await encounters.StartEncounterAsync(appt.Id, appt.PatientId, appt.PractitionerId, ct);
        var checkedIn = appt.CheckIn(ifMatch, encounterId, clock.GetUtcNow());
        if (!checkedIn.IsSuccess)
        {
            return checkedIn.Error!;
        }

        await audit.RecordAsync(new AuditEntry("appointment.checkin", "appointment", appt.Id, appt.PatientId), ct);
        var dto = await ToDtoAsync(appt, ct); // build the response before commit; the session is closed afterwards
        try
        {
            await session.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrent_change", "The appointment changed concurrently; reload.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("already_checked_in", "This appointment was already checked in.");
        }

        return dto;
    }

    public async Task<Result<AppointmentDto>> CancelAsync(Guid id, int? ifMatch, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await session.ContextAsync<SchedulingDbContext>(ct);
        var appt = await db.Appointments.SingleOrDefaultAsync(a => a.Id == id && a.TenantId == actor.TenantId && a.FacilityId == actor.FacilityId, ct);
        if (appt is null)
        {
            return Error.NotFound();
        }

        var cancelled = appt.Cancel(ifMatch);
        if (!cancelled.IsSuccess)
        {
            return cancelled.Error!;
        }

        await audit.RecordAsync(new AuditEntry("appointment.cancel", "appointment", appt.Id, appt.PatientId), ct);
        var dto = await ToDtoAsync(appt, ct);
        await session.CommitAsync(ct);
        return dto;
    }

    private async Task<AppointmentDto> ToDtoAsync(Appointment a, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var banner = await patients.GetBannerAsync(a.PatientId, ct);
        var practitioner = await staff.GetPractitionerAsync(actor.TenantId, a.PractitionerId, ct);
        return new AppointmentDto(a.Id, a.PatientId, banner?.Mrn ?? "", banner?.DisplayName ?? "", a.PractitionerId, practitioner?.DisplayName ?? "", a.StartsAt, a.EndsAt, a.Status.ToString(), a.Reason, a.EncounterId, a.Version);
    }
}
