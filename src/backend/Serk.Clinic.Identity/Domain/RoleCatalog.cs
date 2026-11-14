using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Domain;

/// <summary>
/// Versioned default role → capability mapping (policy version <see cref="Version"/>). Roles are bundles; checks are
/// always on capabilities. Notable design decisions (see docs/security/THREAT_MODEL.md):
/// clinic administrators/owners manage staff, branding, printers and read aggregated analytics but have NO clinical-note or
/// patient-level capability; platform support has no
/// tenant role at all; reception sees demographics but not allergies or notes.
/// </summary>
public static class RoleCatalog
{
    public const int Version = 4;

    public const string Receptionist = "Receptionist";
    public const string Nurse = "Nurse";
    public const string Clinician = "Clinician";
    public const string Dentist = "Dentist";
    public const string ClinicAdmin = "ClinicAdmin";
    public const string Cashier = "Cashier";
    public const string Owner = "Owner";
    public const string FinanceSupervisor = "FinanceSupervisor";

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [Receptionist] =
        [
            Capabilities.PatientSearch, Capabilities.PatientRegister, Capabilities.PatientReadDemographics,
            Capabilities.AppointmentRead, Capabilities.AppointmentBook, Capabilities.AppointmentCheckIn,
        ],
        [Nurse] =
        [
            Capabilities.PatientSearch, Capabilities.PatientReadDemographics, Capabilities.AllergyRead, Capabilities.AllergyWrite,
            Capabilities.AppointmentRead, Capabilities.AppointmentCheckIn, Capabilities.EncounterRead,
        ],
        [Clinician] =
        [
            Capabilities.PatientSearch, Capabilities.PatientReadDemographics, Capabilities.AllergyRead, Capabilities.AllergyWrite,
            Capabilities.AppointmentRead, Capabilities.EncounterRead, Capabilities.NoteWrite, Capabilities.NoteSign,
            Capabilities.DocumentCreate, Capabilities.DocumentFinalize, Capabilities.DocumentRead,
        ],
        [Dentist] =
        [
            Capabilities.PatientSearch, Capabilities.PatientReadDemographics, Capabilities.AllergyRead, Capabilities.AllergyWrite,
            Capabilities.AppointmentRead, Capabilities.EncounterRead, Capabilities.NoteWrite, Capabilities.NoteSign,
            Capabilities.DocumentCreate, Capabilities.DocumentFinalize, Capabilities.DocumentRead,
            Capabilities.DentalChartRead, Capabilities.DentalChartWrite,
        ],
        [ClinicAdmin] = [Capabilities.StaffAdmin, Capabilities.AuditRead, Capabilities.AppointmentRead, Capabilities.BrandingManage, Capabilities.PrintersManage, Capabilities.AnalyticsRead, Capabilities.DataExport],
        // Owner: every administrative, financial, configuration and data capability of the organisation (v4). No capability
        // to read or write charts in the app. Exception, deliberately: backup.manage (Clinical sensitivity) — the owner, as
        // data controller, may take an encrypted backup that contains clinical records, with a purpose, password
        // confirmation and a log entry (ADR-0015). Revoke it per person where the clinic decides otherwise.
        [Owner] =
        [
            Capabilities.AnalyticsRead, Capabilities.BrandingManage, Capabilities.PrintersManage, Capabilities.AuditRead, Capabilities.StaffAdmin,
            Capabilities.AppointmentRead, Capabilities.FinanceRead, Capabilities.FinanceApprove, Capabilities.FinanceClose, Capabilities.FinanceConfigure,
            Capabilities.OwnerReportsRead, Capabilities.OwnerSubscriptionsManage,
            Capabilities.DataExport, Capabilities.PatientDataExport, Capabilities.BackupManage,
        ],
        [Cashier] = [Capabilities.PatientSearch, Capabilities.PatientReadDemographics, Capabilities.InvoiceWrite, Capabilities.PaymentCollect],
        // Finance supervisor: reconciles, approves and closes; no clinical capability and cannot collect cash (separation of duties).
        [FinanceSupervisor] = [Capabilities.FinanceRead, Capabilities.FinanceApprove, Capabilities.FinanceClose, Capabilities.FinanceConfigure],
    };

    public static IReadOnlySet<string> CapabilitiesFor(IEnumerable<string> roles)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (Map.TryGetValue(role, out var caps))
            {
                set.UnionWith(caps);
            }
        }

        return set;
    }

    public static bool IsKnownRole(string role) => Map.ContainsKey(role);

    private static readonly (string Code, string Name, string Description)[] Descriptions =
    [
        (Receptionist, "Receptionist", "Registration, booking and check-in. Demographics only — no allergies or notes."),
        (Nurse, "Nurse", "Allergies, check-in and reading encounters. Cannot write or sign notes."),
        (Clinician, "Clinician", "Full clinical documentation: notes, signatures, allergies and documents."),
        (Dentist, "Dentist", "Clinician permissions plus the dental chart."),
        (Cashier, "Cashier", "Invoices, payments, receipts and own cash drawer."),
        (FinanceSupervisor, "Finance supervisor", "Approvals, reconciliation, daily close and price list. No clinical access, no cash handling."),
        (ClinicAdmin, "Clinic administrator", "Staff sessions, audit, clinic profile, printers, analytics and operational exports. No clinical access."),
        (Owner, "Owner / management", "Full administrative control: clinic profile, finance (approve, close, prices), owner reports, staff sessions, audit, exports and encrypted backups (which contain clinical records). Cannot open charts in the app."),
    ];

    private static readonly Dictionary<string, string[]> ResponsibilityMap = new(StringComparer.Ordinal)
    {
        [Receptionist] = ["Register patients and prevent duplicates", "Book, reschedule and check patients in", "Confirm identity at arrival", "Print appointment slips"],
        [Nurse] = ["Record and verify allergies before treatment", "Check patients in and prepare them", "Read encounters to support the clinician"],
        [Clinician] = ["Write and sign accurate consultation notes", "Record allergies", "Issue patient documents and correct them by addendum only", "Keep own signature image current"],
        [Dentist] = ["Everything a clinician does", "Chart tooth findings (FDI) and correct them by new entries", "Explain treatment and costs before treatment"],
        [Cashier] = ["Open a drawer with a counted float", "Issue invoices from the price list and take payments", "Give a receipt for every payment", "Count cash blind at the end of the shift"],
        [FinanceSupervisor] = ["Approve discounts, refunds and cash variances (never one's own)", "Reconcile provider settlements", "Submit or approve the daily close", "Maintain the price list"],
        [ClinicAdmin] = ["Keep the clinic profile, printers and branding correct", "End staff sessions when someone leaves", "Review the audit trail", "Export operational reports"],
        [Owner] = ["Set up the clinic profile, logo, motto and contacts", "Approve finance exceptions and lock daily closes", "Read owner reports and manage report recipients", "Take and verify encrypted backups regularly", "Decide who exports patient data and why", "Request access changes from the system administrator"],
    };

    /// <summary>What each built-in role is accountable for (shown in the roles and responsibilities matrix).</summary>
    public static IReadOnlyList<string> ResponsibilitiesFor(string role) => ResponsibilityMap.TryGetValue(role, out var r) ? r : [];

    /// <summary>Built-in roles as read-only definitions (the tested safety baseline; clone them to customise).</summary>
    public static IReadOnlyList<RoleDefinition> BuiltIns { get; } =
        Descriptions.Select(d => new RoleDefinition(d.Code, d.Name, d.Description, Map[d.Code].Order(StringComparer.Ordinal).ToList(), true)).ToList();
}
