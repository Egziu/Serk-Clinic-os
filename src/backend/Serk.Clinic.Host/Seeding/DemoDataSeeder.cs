using Npgsql;
using Serk.Clinic.Identity.Application;

namespace Serk.Clinic.Host.Seeding;

/// <summary>
/// Fixed identifiers of the SYNTHETIC demo dataset. All people and organizations are fictional. Two unrelated tenants
/// (A and B) exist so cross-tenant isolation can be demonstrated and tested.
/// </summary>
public static class DemoData
{
    public static readonly Guid TenantA = Guid.Parse("0195a000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("0195b000-0000-7000-8000-00000000000b");
    public static readonly Guid FacilityABole = Guid.Parse("0195a000-0000-7000-8000-0000000000f1");
    public static readonly Guid FacilityAPiassa = Guid.Parse("0195a000-0000-7000-8000-0000000000f2");
    public static readonly Guid FacilityBHawassa = Guid.Parse("0195b000-0000-7000-8000-0000000000f1");

    public static readonly Guid PractitionerAbebe = Guid.Parse("0195a000-0000-7000-8000-0000000000d1");
    public static readonly Guid PractitionerHana = Guid.Parse("0195a000-0000-7000-8000-0000000000d2");
    public static readonly Guid PractitionerExpired = Guid.Parse("0195a000-0000-7000-8000-0000000000d3");
    public static readonly Guid PractitionerDentist = Guid.Parse("0195a000-0000-7000-8000-0000000000d4");
    public static readonly Guid PractitionerB = Guid.Parse("0195b000-0000-7000-8000-0000000000d1");

    public static readonly Guid PatientSelam = Guid.Parse("0195a000-0000-7000-8000-0000000000a1");
    public static readonly Guid PatientDawit = Guid.Parse("0195a000-0000-7000-8000-0000000000a2");
    public static readonly Guid PatientTenantB = Guid.Parse("0195b000-0000-7000-8000-0000000000a1");

    public const string Reception = "reception.a";
    public const string Clinician = "dr.abebe";
    public const string Clinician2 = "dr.hana";
    public const string ClinicianExpired = "dr.expired";
    public const string Nurse = "nurse.a";
    public const string ClinicAdmin = "admin.a";
    public const string Dentist = "dentist.a";
    public const string ReceptionB = "reception.b";
    public const string ClinicianB = "dr.b";
    public const string Support = "support.serk";
    public const string Owner = "owner.a";
    public const string ClinicAdminB = "admin.b";
    public const string SuperAdmin = "super.admin";

    internal static readonly (string User, Guid Id, string Name)[] Users =
    [
        (Reception, U(1), "Meron Alemu (Reception, synthetic)"),
        (Clinician, U(2), "Dr. Abebe Kassa (synthetic)"),
        (Clinician2, U(3), "Dr. Hana Girma (synthetic)"),
        (ClinicianExpired, U(4), "Dr. Yonas Tadesse (synthetic, expired license)"),
        (Nurse, U(5), "Sister Tigist Haile (synthetic)"),
        (ClinicAdmin, U(6), "Bereket Admasu (Clinic admin, synthetic)"),
        (Dentist, U(7), "Dr. Liya Mekonnen (Dentist, synthetic)"),
        (ReceptionB, U(8), "Rahel Desta (Reception B, synthetic)"),
        (ClinicianB, U(9), "Dr. Samuel Worku (Tenant B, synthetic)"),
        (Support, U(10), "SERK Platform Support (synthetic)"),
        (Owner, U(11), "Almaz Bekele (Owner, synthetic)"),
        (ClinicAdminB, U(12), "Tewodros Ayele (Clinic admin B, synthetic)"),
        (SuperAdmin, U(13), "Saba Kebede (System super admin, synthetic)"),
    ];

    public static Guid UserId(string username) => Users.Single(u => u.User == username).Id;

    private static Guid U(int n) => Guid.Parse($"0195c000-0000-7000-8000-{n:D12}");
}

public static class DemoDataSeeder
{
    public static async Task SeedAsync(string ownerConnection, string password, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ownerConnection);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var hash = AuthService.HashPassword(password);

        await Exec(conn, tx, "INSERT INTO identity.tenants (id, name, created_at) VALUES (@a, 'Abay Family Clinic (SYNTHETIC)', @now), (@b, 'Rift Valley Dental Group (SYNTHETIC)', @now) ON CONFLICT DO NOTHING",
            ("a", DemoData.TenantA), ("b", DemoData.TenantB), ("now", now));
        await Exec(conn, tx,
            """
            INSERT INTO identity.facilities (id, tenant_id, code, name, address_line, phone, license_number, time_zone) VALUES
              (@f1, @a, 'ABY-BOLE', 'Abay Family Clinic - Bole Branch', 'Fictional Road 1, Bole, Addis Ababa', '+251 11 000 0001', 'SYN-FAC-001', 'Africa/Addis_Ababa'),
              (@f2, @a, 'ABY-PIAS', 'Abay Family Clinic - Piassa Branch', 'Fictional Street 9, Piassa, Addis Ababa', '+251 11 000 0002', 'SYN-FAC-002', 'Africa/Addis_Ababa'),
              (@f3, @b, 'RVD-HAW', 'Rift Valley Dental - Hawassa Centre', 'Fictional Avenue 5, Hawassa', '+251 46 000 0003', 'SYN-FAC-101', 'Africa/Addis_Ababa')
            ON CONFLICT DO NOTHING
            """,
            ("f1", DemoData.FacilityABole), ("f2", DemoData.FacilityAPiassa), ("f3", DemoData.FacilityBHawassa), ("a", DemoData.TenantA), ("b", DemoData.TenantB));

        foreach (var (user, id, name) in DemoData.Users)
        {
            await Exec(conn, tx, "INSERT INTO identity.staff_users (id, username, display_name, password_hash, is_active, created_at) VALUES (@id, @u, @n, @h, true, @now) ON CONFLICT DO NOTHING",
                ("id", id), ("u", user), ("n", name), ("h", hash), ("now", now));
        }

        var licenseOk = new DateOnly(2030, 12, 31);
        await Practitioner(conn, tx, DemoData.PractitionerAbebe, DemoData.Clinician, DemoData.TenantA, "MD", "General Practice", "SYN-MD-0001", licenseOk, ct);
        await Practitioner(conn, tx, DemoData.PractitionerHana, DemoData.Clinician2, DemoData.TenantA, "MD", "Internal Medicine", "SYN-MD-0002", licenseOk, ct);
        await Practitioner(conn, tx, DemoData.PractitionerExpired, DemoData.ClinicianExpired, DemoData.TenantA, "MD", "General Practice", "SYN-MD-0003", new DateOnly(2024, 1, 31), ct);
        await Practitioner(conn, tx, DemoData.PractitionerDentist, DemoData.Dentist, DemoData.TenantA, "DDS", "Dentistry", "SYN-DDS-0001", licenseOk, ct);
        await Practitioner(conn, tx, DemoData.PractitionerB, DemoData.ClinicianB, DemoData.TenantB, "MD", "General Practice", "SYN-MD-0101", licenseOk, ct);

        await Affiliation(conn, tx, 1, DemoData.Reception, DemoData.TenantA, DemoData.FacilityABole, ["Receptionist"], ct);
        await Affiliation(conn, tx, 2, DemoData.Clinician, DemoData.TenantA, DemoData.FacilityABole, ["Clinician"], ct);
        await Affiliation(conn, tx, 3, DemoData.Clinician2, DemoData.TenantA, DemoData.FacilityABole, ["Clinician"], ct);
        await Affiliation(conn, tx, 4, DemoData.Clinician2, DemoData.TenantA, DemoData.FacilityAPiassa, ["Clinician"], ct);
        await Affiliation(conn, tx, 5, DemoData.ClinicianExpired, DemoData.TenantA, DemoData.FacilityABole, ["Clinician"], ct);
        await Affiliation(conn, tx, 6, DemoData.Nurse, DemoData.TenantA, DemoData.FacilityABole, ["Nurse"], ct);
        await Affiliation(conn, tx, 7, DemoData.ClinicAdmin, DemoData.TenantA, DemoData.FacilityABole, ["ClinicAdmin"], ct);
        await Affiliation(conn, tx, 8, DemoData.Dentist, DemoData.TenantA, DemoData.FacilityABole, ["Dentist"], ct);
        await Affiliation(conn, tx, 9, DemoData.ReceptionB, DemoData.TenantB, DemoData.FacilityBHawassa, ["Receptionist"], ct);
        await Affiliation(conn, tx, 10, DemoData.ClinicianB, DemoData.TenantB, DemoData.FacilityBHawassa, ["Clinician"], ct);
        await Affiliation(conn, tx, 11, DemoData.Owner, DemoData.TenantA, DemoData.FacilityABole, ["Owner"], ct);
        await Affiliation(conn, tx, 12, DemoData.ClinicAdminB, DemoData.TenantB, DemoData.FacilityBHawassa, ["ClinicAdmin"], ct);
        // DemoData.Support intentionally has NO affiliation: platform support has no routine PHI access.
        // The super administrator also has NO affiliation: system-wide control of access, but no patient data by default.
        await Exec(conn, tx, "INSERT INTO identity.platform_admins (user_id, is_active, granted_at) VALUES (@u, true, @now) ON CONFLICT DO NOTHING",
            ("u", DemoData.UserId(DemoData.SuperAdmin)), ("now", now));

        await Patient(conn, tx, DemoData.PatientSelam, DemoData.TenantA, DemoData.FacilityABole, "MRN-900001", "Selam", "Tesfaye", "Bekele", "Female", new DateOnly(1990, 3, 14), "+251911000001", now, ct);
        await Patient(conn, tx, DemoData.PatientDawit, DemoData.TenantA, DemoData.FacilityABole, "MRN-900002", "Dawit", "Mulugeta", "Assefa", "Male", new DateOnly(1978, 11, 2), "+251911000002", now, ct);
        await Patient(conn, tx, DemoData.PatientTenantB, DemoData.TenantB, DemoData.FacilityBHawassa, "MRN-900001", "Kidist", "Lemma", "Gebre", "Female", new DateOnly(2001, 6, 20), "+251911000003", now, ct);
        await tx.CommitAsync(ct);
    }

    private static Task Practitioner(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string user, Guid tenant, string title, string specialty, string license, DateOnly expires, CancellationToken ct) =>
        Exec(c, tx, "INSERT INTO identity.practitioners (id, user_id, tenant_id, professional_title, specialty, license_number, license_expires_on) VALUES (@id, @u, @t, @ti, @s, @l, @e) ON CONFLICT DO NOTHING",
            ("id", id), ("u", DemoData.UserId(user)), ("t", tenant), ("ti", title), ("s", specialty), ("l", license), ("e", expires));

    private static Task Affiliation(NpgsqlConnection c, NpgsqlTransaction tx, int n, string user, Guid tenant, Guid facility, string[] roles, CancellationToken ct) =>
        Exec(c, tx, "INSERT INTO identity.affiliations (id, user_id, tenant_id, facility_id, roles, is_active, valid_from) VALUES (@id, @u, @t, @f, @r, true, '2025-01-01') ON CONFLICT DO NOTHING",
            ("id", Guid.Parse($"0195e000-0000-7000-8000-{n:D12}")), ("u", DemoData.UserId(user)), ("t", tenant), ("f", facility), ("r", roles));

    private static Task Patient(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid tenant, Guid facility, string mrn, string given, string father, string grandfather, string sex, DateOnly dob, string phone, DateTimeOffset now, CancellationToken ct) =>
        Exec(c, tx,
            """
            INSERT INTO patients.patients (id, tenant_id, mrn, given_name, father_name, grandfather_name, normalized_name, sex, birth_date, birth_date_is_estimated, phone, registered_at_facility_id, created_at, created_by)
            VALUES (@id, @t, @mrn, @g, @f, @gf, lower(@g || ' ' || @f || ' ' || @gf), @sex, @dob, false, @ph, @fac, @now, '00000000-0000-0000-0000-000000000000')
            ON CONFLICT DO NOTHING
            """,
            ("id", id), ("t", tenant), ("mrn", mrn), ("g", given), ("f", father), ("gf", grandfather), ("sex", sex), ("dob", dob), ("ph", phone), ("fac", facility), ("now", now));

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
