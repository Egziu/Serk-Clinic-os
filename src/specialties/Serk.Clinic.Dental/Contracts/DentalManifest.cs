namespace Serk.Clinic.Dental.Contracts;

/// <summary>SpecialtyManifest (02_MODULES "Specialty contract"). Clinical content status is explicit.</summary>
public static class DentalManifest
{
    public const string Id = "dental";
    public const string Version = "0.1.0";
    public const string MinimumPlatformVersion = "0.1.0";
    public const string ClinicalReviewStatus = "PENDING CLINICIAN REVIEW";
    public static readonly IReadOnlyList<string> Workflows = ["odontogram.findings.v1"];
}
