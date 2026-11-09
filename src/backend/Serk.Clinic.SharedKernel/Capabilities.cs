namespace Serk.Clinic.SharedKernel;

/// <summary>
/// Fine-grained capability vocabulary. Authorization is expressed against capabilities, never role names.
/// Role-to-capability mapping lives in the Identity module and is versioned there.
/// </summary>
public static class Capabilities
{
    public const string PatientSearch = "patient.search";
    public const string PatientRegister = "patient.register";
    public const string PatientReadDemographics = "patient.read.demographics";
    public const string AllergyRead = "clinical.allergy.read";
    public const string AllergyWrite = "clinical.allergy.write";
    public const string AppointmentRead = "appointment.read";
    public const string AppointmentBook = "appointment.book";
    public const string AppointmentCheckIn = "appointment.checkin";
    public const string EncounterRead = "clinical.encounter.read";
    public const string NoteWrite = "clinical.note.write";
    public const string NoteSign = "clinical.note.sign";
    public const string DocumentCreate = "document.create";
    public const string DocumentFinalize = "document.finalize";
    public const string DocumentRead = "document.read";
    public const string AuditRead = "audit.read";
    public const string StaffAdmin = "staff.admin";
    public const string DentalChartRead = "dental.chart.read";
    public const string DentalChartWrite = "dental.chart.write";
}
