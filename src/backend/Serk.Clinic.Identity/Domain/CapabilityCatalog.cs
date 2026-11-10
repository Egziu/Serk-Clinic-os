using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Domain;

/// <summary>How much harm misuse of a capability can do; drives warnings and review in the administration console.</summary>
public enum CapabilitySensitivity
{
    /// <summary>Operational, no patient data.</summary>
    Standard,

    /// <summary>Patient identity/demographics or aggregate data derived from patients.</summary>
    PatientData,

    /// <summary>Clinical record content or clinical acts (notes, signatures, allergies, charts, documents).</summary>
    Clinical,

    /// <summary>Changes who can do what, or reads the audit trail.</summary>
    Administrative,
}

public sealed record CapabilityInfo(string Code, string Group, string Label, string Description, CapabilitySensitivity Sensitivity);

/// <summary>
/// The complete, described vocabulary of tenant capabilities that roles and per-assignment overrides may use. Anything not
/// listed here is rejected. A unit test proves every constant in <see cref="Capabilities"/> is described here.
/// Platform super administration is NOT a capability: it cannot be put in a role or granted through an assignment.
/// </summary>
public static class CapabilityCatalog
{
    public static readonly IReadOnlyList<CapabilityInfo> All =
    [
        new(Capabilities.PatientSearch, "Patients", "Search patients", "Find patients by name, MRN or phone (minimal result list).", CapabilitySensitivity.PatientData),
        new(Capabilities.PatientRegister, "Patients", "Register patients", "Create new patient records and run duplicate checks.", CapabilitySensitivity.PatientData),
        new(Capabilities.PatientReadDemographics, "Patients", "View demographics", "Open a patient's identity and contact details.", CapabilitySensitivity.PatientData),
        new(Capabilities.AppointmentRead, "Scheduling", "View appointments", "See day lists and clinician schedules.", CapabilitySensitivity.PatientData),
        new(Capabilities.AppointmentBook, "Scheduling", "Book appointments", "Create appointments for patients.", CapabilitySensitivity.PatientData),
        new(Capabilities.AppointmentCheckIn, "Scheduling", "Check patients in", "Confirm identity and start the visit (opens an encounter).", CapabilitySensitivity.PatientData),
        new(Capabilities.EncounterRead, "Clinical", "View encounters", "Open encounters and signed clinical notes.", CapabilitySensitivity.Clinical),
        new(Capabilities.NoteWrite, "Clinical", "Write notes", "Draft consultation notes (attending clinician only).", CapabilitySensitivity.Clinical),
        new(Capabilities.NoteSign, "Clinical", "Sign notes", "Sign notes and addenda (needs a practitioner profile with a valid licence).", CapabilitySensitivity.Clinical),
        new(Capabilities.AllergyRead, "Clinical", "View allergies", "See allergy status and active allergies.", CapabilitySensitivity.Clinical),
        new(Capabilities.AllergyWrite, "Clinical", "Record allergies", "Record or refute allergies and 'no known allergies'.", CapabilitySensitivity.Clinical),
        new(Capabilities.DentalChartRead, "Dental", "View dental chart", "See FDI tooth findings.", CapabilitySensitivity.Clinical),
        new(Capabilities.DentalChartWrite, "Dental", "Record dental findings", "Chart tooth findings and corrections (attending dentist).", CapabilitySensitivity.Clinical),
        new(Capabilities.DocumentRead, "Documents", "View documents", "Open previews and issued documents.", CapabilitySensitivity.Clinical),
        new(Capabilities.DocumentCreate, "Documents", "Prepare documents", "Prepare draft consultation summaries.", CapabilitySensitivity.Clinical),
        new(Capabilities.DocumentFinalize, "Documents", "Issue documents", "Finalize documents (immutable, verifiable).", CapabilitySensitivity.Clinical),
        new(Capabilities.AnalyticsRead, "Management", "View analytics", "Aggregated, de-identified clinic statistics and CSV export.", CapabilitySensitivity.PatientData),
        new(Capabilities.BrandingManage, "Management", "Manage branding", "Accent colour, logo, stamp, document defaults and automation.", CapabilitySensitivity.Standard),
        new(Capabilities.PrintersManage, "Management", "Manage printers", "Printer profiles (thermal vs standard paper).", CapabilitySensitivity.Standard),
        new(Capabilities.StaffAdmin, "Administration", "Manage staff sessions", "Revoke staff sessions within the organisation.", CapabilitySensitivity.Administrative),
        new(Capabilities.AuditRead, "Administration", "Read audit trail", "Read the organisation's audit records.", CapabilitySensitivity.Administrative),
    ];

    private static readonly Dictionary<string, CapabilityInfo> ByCode = All.ToDictionary(c => c.Code, StringComparer.Ordinal);

    public static bool IsKnown(string code) => ByCode.ContainsKey(code);

    public static CapabilityInfo? Find(string code) => ByCode.GetValueOrDefault(code);
}
