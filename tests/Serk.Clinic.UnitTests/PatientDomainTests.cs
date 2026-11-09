using Serk.Clinic.Patients.Domain;

namespace Serk.Clinic.UnitTests;

public class PatientDomainTests
{
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static Patient Make(string given, string father, DateOnly dob, string? phone = null, string? nid = null, bool estimated = false) =>
        Patient.Register(new PatientRegistration(given, father, "X", Sex.Female, dob, estimated, phone, nid), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "MRN-1", Guid.NewGuid(), Now);

    [Fact]
    public void Validation_rejects_future_birth_date_and_missing_names()
    {
        var r = Patient.Validate(new PatientRegistration("", null, null, null, Today.AddDays(1), false, "abc", "!!"), Today);
        Assert.False(r.IsSuccess);
        Assert.Contains("givenName", r.Error!.Fields!.Keys);
        Assert.Contains("fatherName", r.Error.Fields.Keys);
        Assert.Contains("sex", r.Error.Fields.Keys);
        Assert.Contains("birthDate", r.Error.Fields.Keys);
        Assert.Contains("phone", r.Error.Fields.Keys);
        Assert.Contains("nationalId", r.Error.Fields.Keys);
    }

    [Fact]
    public void Validation_rejects_implausible_age()
    {
        Assert.False(Patient.Validate(new PatientRegistration("A", "B", null, Sex.Male, Today.AddYears(-131), false, null, null), Today).IsSuccess);
    }

    [Fact]
    public void Normalization_is_case_whitespace_and_diacritic_insensitive_and_keeps_ethiopic()
    {
        Assert.Equal("selam tesfaye", Patient.NormalizeName("  Sélam ", "TESFAYE"));
        Assert.Equal("ሰላም ተስፋዬ", Patient.NormalizeName("ሰላም", " ተስፋዬ "));
    }

    [Fact]
    public void Name_alone_is_never_a_probable_duplicate()
    {
        var a = Make("Selam", "Tesfaye", new DateOnly(1990, 1, 1));
        var b = Make("Selam", "Tesfaye", new DateOnly(1970, 1, 1));
        Assert.Empty(DuplicateMatcher.Score(a, [b]));
    }

    [Fact]
    public void Same_name_and_birth_date_is_probable()
    {
        var a = Make("Selam", "Tesfaye", new DateOnly(1990, 1, 1));
        var b = Make("selam", "tesfaye", new DateOnly(1990, 1, 1));
        var m = Assert.Single(DuplicateMatcher.Score(a, [b]));
        Assert.Equal(DuplicateStrength.Probable, m.Strength);
    }

    [Fact]
    public void Same_national_id_is_definite()
    {
        var a = Make("A", "B", new DateOnly(1990, 1, 1), nid: "ET-1234");
        var b = Make("Different", "Person", new DateOnly(1980, 1, 1), nid: "et-1234");
        Assert.Equal(DuplicateStrength.Definite, Assert.Single(DuplicateMatcher.Score(a, [b])).Strength);
    }

    [Fact]
    public void Same_phone_requires_same_given_name()
    {
        var a = Make("Selam", "Tesfaye", new DateOnly(1990, 1, 1), phone: "+251 911 000001");
        var sibling = Make("Dawit", "Tesfaye", new DateOnly(1993, 1, 1), phone: "+251911000001");
        var same = Make("Selam", "Kebede", new DateOnly(1993, 1, 1), phone: "+251911000001");
        Assert.Empty(DuplicateMatcher.Score(a, [sibling]));
        Assert.Equal(DuplicateStrength.Probable, Assert.Single(DuplicateMatcher.Score(a, [same])).Strength);
    }

    [Fact]
    public void No_known_allergies_requires_version_and_no_active_allergies()
    {
        var p = Make("A", "B", new DateOnly(1990, 1, 1));
        Assert.Equal("version_mismatch", p.RecordNoKnownAllergies(99, false, Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("active_allergies_exist", p.RecordNoKnownAllergies(1, true, Guid.NewGuid(), Now).Error!.Code);
        Assert.True(p.RecordNoKnownAllergies(1, false, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal(AllergyStatus.NoKnownAllergies, p.AllergyStatus);
        Assert.Equal(2, p.Version);
    }

    [Fact]
    public void Recording_an_allergy_marks_patient_has_allergies()
    {
        var p = Make("A", "B", new DateOnly(1990, 1, 1));
        var a = Allergy.Record(p, "Penicillin", "Rash", AllergySeverity.Moderate, Guid.NewGuid(), Now);
        Assert.True(a.IsSuccess);
        Assert.Equal(AllergyStatus.HasAllergies, p.AllergyStatus);
        Assert.False(Allergy.Record(p, " ", null, null, Guid.NewGuid(), Now).IsSuccess);
    }

    [Theory]
    [InlineData(1990, 11, 9, 36)]
    [InlineData(1990, 11, 10, 35)]
    [InlineData(2026, 11, 9, 0)]
    public void Age_in_years_handles_birthdays(int y, int m, int d, int expected) =>
        Assert.Equal(expected, Patient.AgeInYears(new DateOnly(y, m, d), Today));
}
