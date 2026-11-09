using System.Globalization;
using System.Text.RegularExpressions;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Domain;

public enum BrandingAssetKind
{
    Logo,
    Stamp,
    Signature,
}

public enum PaperSize
{
    A3,
    A4,
    A5,
    Thermal80,
    Thermal58,
}

public enum PrinterKind
{
    Standard,
    Thermal,
}

public enum PrintPurpose
{
    ClinicalDocuments,
    AppointmentSlips,
}

/// <summary>Accent colour chosen by the clinic. Must keep white button text readable (WCAG 2.x contrast ≥ 4.5:1).</summary>
public static partial class AccentColor
{
    public const string Default = "#1e4d3a";
    public const double MinContrastOnWhite = 4.5;

    public static Result<string> Validate(string? hex)
    {
        if (hex is null || !HexPattern().IsMatch(hex))
        {
            return Error.Validation("invalid_color", "Use a colour in #RRGGBB form.", new Dictionary<string, string[]> { ["accentColor"] = ["Use #RRGGBB."] });
        }

        var normalized = hex.ToLowerInvariant();
        var contrast = ContrastRatio(normalized, "#ffffff");
        return contrast >= MinContrastOnWhite
            ? normalized
            : Error.Validation("insufficient_contrast",
                string.Create(CultureInfo.InvariantCulture, $"This colour has {contrast:0.0}:1 contrast against white text; at least {MinContrastOnWhite}:1 is required for readable buttons and headings. Choose a darker shade."),
                new Dictionary<string, string[]> { ["accentColor"] = ["Too light for readable white text."] });
    }

    /// <summary>WCAG 2.x contrast ratio between two #rrggbb colours.</summary>
    public static double ContrastRatio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + (i * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(1)) + (0.0722 * Channel(2));
    }

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}\z")]
    private static partial Regex HexPattern();
}

public static class PaperRules
{
    public static PrinterKind KindOf(PaperSize paper) => paper is PaperSize.Thermal58 or PaperSize.Thermal80 ? PrinterKind.Thermal : PrinterKind.Standard;

    /// <summary>Clinical documents (summaries, prescriptions) are never printed on thermal paper: it fades and is not archival.</summary>
    public static bool AllowedFor(PrintPurpose purpose, PaperSize paper) =>
        purpose == PrintPurpose.AppointmentSlips || KindOf(paper) == PrinterKind.Standard;

    public static (double WidthMm, double HeightMm) Dimensions(PaperSize paper) => paper switch
    {
        PaperSize.A3 => (297, 420),
        PaperSize.A4 => (210, 297),
        PaperSize.A5 => (148, 210),
        PaperSize.Thermal80 => (80, 150),
        PaperSize.Thermal58 => (58, 150),
        _ => throw new ArgumentOutOfRangeException(nameof(paper)),
    };
}
