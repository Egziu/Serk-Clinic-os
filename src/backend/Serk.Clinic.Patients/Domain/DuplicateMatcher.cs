namespace Serk.Clinic.Patients.Domain;

public enum DuplicateStrength
{
    Possible,
    Probable,
    Definite,
}

public sealed record DuplicateMatch(Patient Candidate, DuplicateStrength Strength, IReadOnlyList<string> Reasons);

/// <summary>
/// Deterministic duplicate scoring. A name alone is NEVER sufficient for a probable match (common Ethiopian names
/// repeat frequently); matches require name + birth date, phone + given name, or national ID.
/// Thresholds are provisional and require review by registration/medical-records leads.
/// </summary>
public static class DuplicateMatcher
{
    public static IReadOnlyList<DuplicateMatch> Score(Patient incoming, IEnumerable<Patient> candidates)
    {
        var results = new List<DuplicateMatch>();
        foreach (var c in candidates.Where(c => c.Id != incoming.Id))
        {
            var reasons = new List<string>();
            var strength = (DuplicateStrength?)null;
            if (incoming.NationalId is not null && incoming.NationalId == c.NationalId)
            {
                reasons.Add("same_national_id");
                strength = DuplicateStrength.Definite;
            }

            var sameName = incoming.NormalizedName == c.NormalizedName;
            var sameGivenAndFather = FirstTwo(incoming.NormalizedName) == FirstTwo(c.NormalizedName);
            var birthClose = incoming.BirthDate == c.BirthDate
                || ((incoming.BirthDateIsEstimated || c.BirthDateIsEstimated) && Math.Abs(incoming.BirthDate.DayNumber - c.BirthDate.DayNumber) <= 366);

            if (sameGivenAndFather && incoming.BirthDate == c.BirthDate)
            {
                reasons.Add("same_name_and_birth_date");
                strength = Max(strength, DuplicateStrength.Probable);
            }
            else if ((sameName || sameGivenAndFather) && birthClose)
            {
                reasons.Add("same_name_estimated_birth_close");
                strength = Max(strength, DuplicateStrength.Possible);
            }

            if (incoming.Phone.Length > 0 && incoming.Phone == c.Phone && FirstToken(incoming.NormalizedName) == FirstToken(c.NormalizedName))
            {
                reasons.Add("same_phone_and_given_name");
                strength = Max(strength, DuplicateStrength.Probable);
            }

            if (strength is not null)
            {
                results.Add(new DuplicateMatch(c, strength.Value, reasons));
            }
        }

        return results.OrderByDescending(r => r.Strength).ToList();
    }

    private static DuplicateStrength Max(DuplicateStrength? a, DuplicateStrength b) => a is null || b > a ? b : a.Value;

    private static string FirstToken(string normalized) => normalized.Split(' ')[0];

    private static string FirstTwo(string normalized) => string.Join(' ', normalized.Split(' ').Take(2));
}
