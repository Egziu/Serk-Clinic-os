using System.Globalization;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Dental.Domain;

public enum Dentition
{
    Permanent,
    Primary,
}

public enum ToothSurface
{
    Mesial,
    Distal,
    Occlusal,
    Incisal,
    Buccal,
    Lingual,
}

/// <summary>
/// FDI World Dental Federation two-digit notation (ISO 3950). First digit = quadrant (1-4 permanent, 5-8 primary),
/// second = tooth position from the midline (1-8 permanent, 1-5 primary). Quadrants: 1/5 upper right, 2/6 upper left,
/// 3/7 lower left, 4/8 lower right (patient's perspective).
/// </summary>
public readonly record struct ToothCode
{
    private static readonly string[] PermanentNames = ["central incisor", "lateral incisor", "canine", "first premolar", "second premolar", "first molar", "second molar", "third molar"];
    private static readonly string[] PrimaryNames = ["central incisor", "lateral incisor", "canine", "first molar", "second molar"];

    private ToothCode(int quadrant, int position)
    {
        Quadrant = quadrant;
        Position = position;
    }

    public int Quadrant { get; }
    public int Position { get; }

    public int Code => (Quadrant * 10) + Position;
    public Dentition Dentition => Quadrant <= 4 ? Dentition.Permanent : Dentition.Primary;

    /// <summary>Incisors and canines (positions 1-3) are anterior: they have an incisal edge, not an occlusal surface.</summary>
    public bool IsAnterior => Position <= 3;

    public bool IsUpper => Quadrant is 1 or 2 or 5 or 6;

    public IReadOnlyList<ToothSurface> ValidSurfaces => IsAnterior
        ? [ToothSurface.Mesial, ToothSurface.Distal, ToothSurface.Incisal, ToothSurface.Buccal, ToothSurface.Lingual]
        : [ToothSurface.Mesial, ToothSurface.Distal, ToothSurface.Occlusal, ToothSurface.Buccal, ToothSurface.Lingual];

    public string DisplayName
    {
        get
        {
            var side = Quadrant switch { 1 or 5 => "upper right", 2 or 6 => "upper left", 3 or 7 => "lower left", _ => "lower right" };
            var name = Dentition == Dentition.Permanent ? PermanentNames[Position - 1] : "primary " + PrimaryNames[Position - 1];
            return $"{Code.ToString(CultureInfo.InvariantCulture)} {side} {name}";
        }
    }

    public static Result<ToothCode> Parse(int? code)
    {
        if (code is null)
        {
            return Error.Validation("tooth_required", "Tooth number is required.");
        }

        var quadrant = code.Value / 10;
        var position = code.Value % 10;
        var valid = quadrant switch
        {
            >= 1 and <= 4 => position is >= 1 and <= 8,
            >= 5 and <= 8 => position is >= 1 and <= 5,
            _ => false,
        };
        return valid
            ? new ToothCode(quadrant, position)
            : Error.Validation("invalid_fdi_code", "Not a valid FDI tooth number (11-48 permanent, 51-85 primary).");
    }

    public static IEnumerable<ToothCode> All(Dentition dentition)
    {
        var (q0, max) = dentition == Dentition.Permanent ? (1, 8) : (5, 5);
        for (var q = q0; q < q0 + 4; q++)
        {
            for (var p = 1; p <= max; p++)
            {
                yield return new ToothCode(q, p);
            }
        }
    }
}
