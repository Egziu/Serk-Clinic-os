using Serk.Clinic.Platform.Export;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Scheduling.Application;

public static class SchedulingExports
{
    public static readonly ExportDefinition Appointments = new(
        "appointments.list", "Scheduling", "Appointment list",
        "Every appointment in the period with patient MRN and name, clinician, status and times (facility local time). Identifies patients.",
        [Capabilities.AppointmentRead], ContainsPatientData: true, UsesDateRange: true,
        ["local_date", "local_time", "duration_min", "status", "patient_mrn", "patient_name", "clinician", "reason", "checked_in_at", "booked_at"],
        """
        SELECT (a.starts_at AT TIME ZONE @tz)::date, to_char(a.starts_at AT TIME ZONE @tz, 'HH24:MI'),
               (extract(epoch FROM a.ends_at - a.starts_at) / 60)::int, a.status, p.mrn, p.display_name, s.display_name, a.reason,
               a.checked_in_at, a.created_at
        FROM scheduling.appointments a
        LEFT JOIN patients.v_patient_reference p ON p.patient_id = a.patient_id
        LEFT JOIN identity.v_staff_reference s ON s.practitioner_id = a.practitioner_id
        WHERE a.tenant_id = @t AND a.facility_id = @f AND a.starts_at >= @from_utc AND a.starts_at < @to_utc
        ORDER BY a.starts_at LIMIT @limit
        """);

    public static readonly ExportDefinition DailyVolume = new(
        "appointments.daily-volume", "Scheduling", "Daily appointment volume",
        "Counts per day and clinician: booked, checked in, no-show and cancelled. No patient data.",
        [Capabilities.AppointmentRead], ContainsPatientData: false, UsesDateRange: true,
        ["local_date", "clinician", "total", "checked_in", "no_show", "cancelled"],
        """
        SELECT (a.starts_at AT TIME ZONE @tz)::date AS d, coalesce(s.display_name, 'Unknown') AS c, count(*)::int,
               count(*) FILTER (WHERE a.status = 'CheckedIn')::int, count(*) FILTER (WHERE a.status = 'NoShow')::int,
               count(*) FILTER (WHERE a.status = 'Cancelled')::int
        FROM scheduling.appointments a
        LEFT JOIN identity.v_staff_reference s ON s.practitioner_id = a.practitioner_id
        WHERE a.tenant_id = @t AND a.facility_id = @f AND a.starts_at >= @from_utc AND a.starts_at < @to_utc
        GROUP BY 1, 2 ORDER BY 1, 2 LIMIT @limit
        """);
}
