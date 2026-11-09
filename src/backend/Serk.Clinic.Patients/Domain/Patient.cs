using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Patients.Domain;

public enum Sex
{
    Female,
    Male,
    Other,
    Unknown,
}

public enum AllergyStatus
{
    /// <summary>Allergies have not been asked/recorded. Must be visibly distinct from "no known allergies".</summary>
    Unknown,
    NoKnownAllergies,
    HasAllergies,
}

public sealed record PatientRegistration(
    string? GivenName,
    string? FatherName,
    string? GrandfatherName,
    Sex? Sex,
    DateOnly? BirthDate,
    bool BirthDateIsEstimated,
    string? Phone,
    string? NationalId);

/// <summary>Tenant-scoped patient master record (aggregate root). Ethiopian naming: given name + father's + grandfather's name.</summary>
public sealed partial class Patient
{
    public const int MaxAgeYears = 130;

    private Patient()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Mrn { get; private set; } = "";
    public string GivenName { get; private set; } = "";
    public string FatherName { get; private set; } = "";
    public string GrandfatherName { get; private set; } = "";
    public string NormalizedName { get; private set; } = "";
    public Sex Sex { get; private set; }
    public DateOnly BirthDate { get; private set; }
    public bool BirthDateIsEstimated { get; private set; }
    public string Phone { get; private set; } = "";
    public string? NationalId { get; private set; }
    public AllergyStatus AllergyStatus { get; private set; }
    public DateTimeOffset? AllergyStatusUpdatedAt { get; private set; }
    public Guid? AllergyStatusUpdatedBy { get; private set; }
    public Guid RegisteredAtFacilityId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public int Version { get; private set; }

    public string DisplayName => string.Join(' ', new[] { GivenName, FatherName, GrandfatherName }.Where(s => s.Length > 0));

    public static Result<PatientRegistration> Validate(PatientRegistration input, DateOnly today)
    {
        var v = new ValidationCollector()
            .Required(input.GivenName, "givenName").MaxLength(input.GivenName?.Trim(), 100, "givenName")
            .Required(input.FatherName, "fatherName").MaxLength(input.FatherName?.Trim(), 100, "fatherName")
            .MaxLength(input.GrandfatherName?.Trim(), 100, "grandfatherName")
            .Require(input.Sex is not null, "sex", "Required.")
            .Require(input.BirthDate is not null, "birthDate", "Required (use an estimated date and tick 'estimated' if unknown).")
            .Require(input.BirthDate is null || input.BirthDate <= today, "birthDate", "Cannot be in the future.")
            .Require(input.BirthDate is null || input.BirthDate >= today.AddYears(-MaxAgeYears), "birthDate", "Implausible birth date.")
            .Require(string.IsNullOrWhiteSpace(input.Phone) || PhonePattern().IsMatch(input.Phone.Trim()), "phone", "Use digits, spaces and an optional leading +.")
            .Require(string.IsNullOrWhiteSpace(input.NationalId) || NationalIdPattern().IsMatch(input.NationalId.Trim()), "nationalId", "Use letters, digits and dashes only.");
        return v.HasErrors ? v.ToError() : input;
    }

    public static Patient Register(PatientRegistration valid, Guid id, Guid tenantId, Guid facilityId, string mrn, Guid actor, DateTimeOffset now)
    {
        var given = Clean(valid.GivenName!);
        var father = Clean(valid.FatherName!);
        var grandfather = Clean(valid.GrandfatherName ?? string.Empty);
        return new Patient
        {
            Id = id,
            TenantId = tenantId,
            Mrn = mrn,
            GivenName = given,
            FatherName = father,
            GrandfatherName = grandfather,
            NormalizedName = NormalizeName(given, father, grandfather),
            Sex = valid.Sex!.Value,
            BirthDate = valid.BirthDate!.Value,
            BirthDateIsEstimated = valid.BirthDateIsEstimated,
            Phone = NormalizePhone(valid.Phone),
            NationalId = string.IsNullOrWhiteSpace(valid.NationalId) ? null : valid.NationalId.Trim().ToUpperInvariant(),
            AllergyStatus = AllergyStatus.Unknown,
            RegisteredAtFacilityId = facilityId,
            CreatedAt = now,
            CreatedBy = actor,
            Version = 1,
        };
    }

    public Result<Patient> RecordNoKnownAllergies(int expectedVersion, bool hasActiveAllergies, Guid actor, DateTimeOffset now)
    {
        if (expectedVersion != Version)
        {
            return Error.Precondition("version_mismatch", "The patient record changed; reload before updating.");
        }

        if (hasActiveAllergies)
        {
            return Error.Rule("active_allergies_exist", "Active allergies are recorded; inactivate them before recording 'no known allergies'.");
        }

        AllergyStatus = AllergyStatus.NoKnownAllergies;
        AllergyStatusUpdatedAt = now;
        AllergyStatusUpdatedBy = actor;
        Version++;
        return this;
    }

    internal void MarkHasAllergies(Guid actor, DateTimeOffset now)
    {
        AllergyStatus = AllergyStatus.HasAllergies;
        AllergyStatusUpdatedAt = now;
        AllergyStatusUpdatedBy = actor;
        Version++;
    }

    public static int AgeInYears(DateOnly birthDate, DateOnly today)
    {
        var age = today.Year - birthDate.Year;
        if (today < birthDate.AddYears(age))
        {
            age--;
        }

        return Math.Max(age, 0);
    }

    /// <summary>Lower-case, whitespace-collapsed, diacritic-free (Latin) name used only for matching, never display.</summary>
    public static string NormalizeName(params string[] parts)
    {
        var joined = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        var decomposed = joined.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return Whitespace().Replace(sb.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    public static string NormalizePhone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) ? string.Empty : Whitespace().Replace(phone.Trim(), string.Empty);

    private static string Clean(string s) => Whitespace().Replace(s.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\+?[0-9 ]{6,20}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^[A-Za-z0-9-]{4,64}$")]
    private static partial Regex NationalIdPattern();
}
