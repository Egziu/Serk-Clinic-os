using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Domain;

/// <summary>
/// Versioned default role → capability mapping (policy version <see cref="Version"/>). Roles are bundles; checks are
/// always on capabilities. Notable design decisions (see docs/security/THREAT_MODEL.md):
/// clinic administrators manage staff and read audit but have NO clinical-note capability; platform support has no
/// tenant role at all; reception sees demographics but not allergies or notes.
/// </summary>
public static class RoleCatalog
{
    public const int Version = 1;

    public const string Receptionist = "Receptionist";
    public const string Nurse = "Nurse";
    public const string Clinician = "Clinician";
    public const string Dentist = "Dentist";
    public const string ClinicAdmin = "ClinicAdmin";
    public const string Cashier = "Cashier";

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
        [ClinicAdmin] = [Capabilities.StaffAdmin, Capabilities.AuditRead, Capabilities.AppointmentRead],
        [Cashier] = [Capabilities.PatientSearch, Capabilities.PatientReadDemographics],
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
}
