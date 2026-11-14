using Serk.Clinic.Platform.Export;

namespace Serk.Clinic.Patients.Application;

public static class PatientExports
{
    public static readonly ExportDefinition Register = new(
        "patients.register", "Patients", "Patient register",
        "Patients registered in the period: MRN, names, sex, birth date, phone. Identifies patients.",
        [], ContainsPatientData: true, UsesDateRange: true,
        ["mrn", "given_name", "father_name", "grandfather_name", "sex", "birth_date", "birth_date_estimated", "phone", "allergy_status", "registered_at"],
        """
        SELECT mrn, given_name, father_name, grandfather_name, sex, birth_date, birth_date_is_estimated, phone, allergy_status, created_at
        FROM patients.patients
        WHERE tenant_id = @t AND created_at >= @from_utc AND created_at < @to_utc
        ORDER BY created_at LIMIT @limit
        """);
}
