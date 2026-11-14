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
    public const int Version = 3;

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
        [ClinicAdmin] = [Capabilities.StaffAdmin, Capabilities.AuditRead, Capabilities.AppointmentRead, Capabilities.BrandingManage, Capabilities.PrintersManage, Capabilities.AnalyticsRead],
        // Owner/management: aggregated analytics, branding and printers; no patient-level or clinical access.
        [Owner] =
        [
            Capabilities.AnalyticsRead, Capabilities.BrandingManage, Capabilities.PrintersManage, Capabilities.AuditRead,
            Capabilities.FinanceRead, Capabilities.FinanceApprove, Capabilities.OwnerReportsRead, Capabilities.OwnerSubscriptionsManage,
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
        (ClinicAdmin, "Clinic administrator", "Staff sessions, audit, branding, printers and analytics. No clinical access."),
        (Owner, "Owner / management", "Analytics, finance view and approvals, owner reports, branding. No patient-level access."),
    ];

    /// <summary>Built-in roles as read-only definitions (the tested safety baseline; clone them to customise).</summary>
    public static IReadOnlyList<RoleDefinition> BuiltIns { get; } =
        Descriptions.Select(d => new RoleDefinition(d.Code, d.Name, d.Description, Map[d.Code].Order(StringComparer.Ordinal).ToList(), true)).ToList();
}
