using System.Text.RegularExpressions;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Domain;

/// <summary>Validation for the clinic profile shown on letterheads, receipts and slips.</summary>
public static partial class ClinicProfileRules
{
    public static readonly IReadOnlyList<string> Languages = ["en", "am", "ti"];
    public static readonly IReadOnlyList<string> CalendarDisplays = ["Gregorian", "Ethiopian", "Both"];

    /// <summary>Field → maximum length (matches the database CHECK constraints).</summary>
    public static readonly IReadOnlyDictionary<string, int> MaxLengths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["displayName"] = 120,
        ["legalName"] = 200,
        ["motto"] = 140,
        ["addressLine1"] = 160,
        ["addressLine2"] = 160,
        ["city"] = 80,
        ["region"] = 80,
        ["country"] = 80,
        ["postalCode"] = 20,
        ["phone"] = 40,
        ["altPhone"] = 40,
        ["email"] = 160,
        ["website"] = 160,
        ["taxId"] = 40,
        ["registrationNo"] = 60,
        ["licenceNo"] = 60,
        ["openingHours"] = 200,
        ["receiptFooter"] = 240,
        ["documentFooter"] = 240,
    };

    public static string Clean(string? value) => (value ?? "").Trim().ReplaceLineEndings(" ");

    public static Result<bool> Validate(IReadOnlyDictionary<string, string> fields, string language, string calendar, string currency)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, max) in MaxLengths)
        {
            var value = fields.GetValueOrDefault(name, "");
            if (value.Length > max)
            {
                errors[name] = [$"At most {max} characters."];
            }
            else if (value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            {
                // Includes bidirectional overrides (U+202E etc.) that could make a printed letterhead read differently.
                errors[name] = ["Control or invisible formatting characters are not allowed."];
            }
        }

        if (fields.GetValueOrDefault("email", "") is { Length: > 0 } email && !EmailPattern().IsMatch(email))
        {
            errors["email"] = ["Enter a valid e-mail address."];
        }

        if (fields.GetValueOrDefault("website", "") is { Length: > 0 } web
            && !(Uri.TryCreate(web, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.', StringComparison.Ordinal)))
        {
            errors["website"] = ["Enter a full address starting with https://"];
        }

        foreach (var phoneField in new[] { "phone", "altPhone" })
        {
            if (fields.GetValueOrDefault(phoneField, "") is { Length: > 0 } phone && !PhonePattern().IsMatch(phone))
            {
                errors[phoneField] = ["Use digits, spaces, +, - and brackets only."];
            }
        }

        if (!Languages.Contains(language))
        {
            errors["defaultLanguage"] = ["Choose English, Amharic or Tigrinya."];
        }

        if (!CalendarDisplays.Contains(calendar))
        {
            errors["calendarDisplay"] = ["Choose Gregorian, Ethiopian or Both."];
        }

        if (!CurrencyPattern().IsMatch(currency))
        {
            errors["currency"] = ["Three capital letters, e.g. ETB."];
        }

        return errors.Count == 0 ? true : Error.Validation("invalid_profile", "Some profile fields need attention.", errors);
    }

    /// <summary>Share of the fields a patient-facing letterhead needs that are filled in (0–100).</summary>
    public static int Completeness(IReadOnlyDictionary<string, string> fields)
    {
        string[] important = ["displayName", "motto", "addressLine1", "city", "phone", "email", "licenceNo", "taxId", "openingHours"];
        return (int)Math.Round(100.0 * important.Count(f => fields.GetValueOrDefault(f, "").Length > 0) / important.Length);
    }

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,120}\.[A-Za-z]{2,24}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9+\-() ]{6,40}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
