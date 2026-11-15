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
        new(Capabilities.BrandingManage, "Management", "Manage clinic profile and branding", "Clinic name, motto, address, contacts, registration numbers, logo, stamp, colour, document defaults and automation.", CapabilitySensitivity.Standard),
        new(Capabilities.PrintersManage, "Management", "Manage printers", "Printer profiles (thermal vs standard paper).", CapabilitySensitivity.Standard),
        new(Capabilities.InvoiceWrite, "Billing", "Create invoices", "Prepare and issue invoices from the price list; request refunds.", CapabilitySensitivity.PatientData),
        new(Capabilities.PaymentCollect, "Billing", "Collect payments", "Run a cashier session, take payments and issue receipts.", CapabilitySensitivity.PatientData),
        new(Capabilities.FinanceRead, "Finance", "View finance", "Ledger, trial balance, money trace, sessions and daily closes.", CapabilitySensitivity.Administrative),
        new(Capabilities.FinanceApprove, "Finance", "Approve finance exceptions", "Approve discounts, refunds and cash variances (never one's own).", CapabilitySensitivity.Administrative),
        new(Capabilities.FinanceClose, "Finance", "Close the business day", "Submit and approve the daily close; record provider settlements.", CapabilitySensitivity.Administrative),
        new(Capabilities.FinanceConfigure, "Finance", "Configure prices", "Maintain the service and price list.", CapabilitySensitivity.Administrative),
        new(Capabilities.OwnerReportsRead, "Owner", "Read owner reports", "Locked daily close reports (aggregates only, no patient data).", CapabilitySensitivity.Standard),
        new(Capabilities.OwnerSubscriptionsManage, "Owner", "Manage report recipients", "Bind or revoke owner digest recipients.", CapabilitySensitivity.Administrative),
        new(Capabilities.StaffAdmin, "Administration", "Manage staff sessions", "Revoke staff sessions within the organisation.", CapabilitySensitivity.Administrative),
        new(Capabilities.AuditRead, "Administration", "Read audit trail", "Read the organisation's audit records.", CapabilitySensitivity.Administrative),
        new(Capabilities.DataExport, "Data", "Export operational data", "Download finance, schedule-capacity, staff and audit data as CSV or JSON (every export is logged).", CapabilitySensitivity.Administrative),
        new(Capabilities.PatientDataExport, "Data", "Export patient data", "Download patient-identifying data (register, appointments, invoices by patient). Needs a stated purpose and password confirmation; logged.", CapabilitySensitivity.PatientData),
        new(Capabilities.CareContactWrite, "Care+", "Record contact consent", "Record a patient's phone, Telegram link, language and consent for reminders and health tips.", CapabilitySensitivity.PatientData),
        new(Capabilities.CareDesk, "Care+", "Work the care desk", "See follow-up messages and patient replies, call patients back and close care tasks.", CapabilitySensitivity.PatientData),
        new(Capabilities.CareManage, "Care+", "Manage patient messaging", "Edit message templates, schedule health-tip campaigns, retry failed messages and see delivery statistics.", CapabilitySensitivity.Administrative),
        new(Capabilities.CareTipsWrite, "Care+", "Write health tips", "Draft health tips for patients (they need clinician approval before sending).", CapabilitySensitivity.Standard),
        new(Capabilities.CareTipsApprove, "Care+", "Approve health tips", "Approve another person's health tip as clinically appropriate (never one's own).", CapabilitySensitivity.Clinical),
        new(Capabilities.SocialManage, "Marketing", "Plan and track social media", "Plan posts and record social media results. Posts showing patients need recorded written consent.", CapabilitySensitivity.Standard),
        new(Capabilities.BackupManage, "Data", "Create and verify backups", "Create and verify the organisation's encrypted backup. A backup contains EVERY record, including clinical notes and signature images: needs a stated purpose and password confirmation; logged.", CapabilitySensitivity.Clinical),
    ];

    private static readonly Dictionary<string, CapabilityInfo> ByCode = All.ToDictionary(c => c.Code, StringComparer.Ordinal);

    public static bool IsKnown(string code) => ByCode.ContainsKey(code);

    public static CapabilityInfo? Find(string code) => ByCode.GetValueOrDefault(code);
}
