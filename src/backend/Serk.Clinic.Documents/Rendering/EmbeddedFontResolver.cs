using System.Reflection;
using PdfSharp.Fonts;

namespace Serk.Clinic.Documents.Rendering;

/// <summary>
/// Supplies the embedded Noto fonts to PDFsharp (no dependency on host-installed fonts, so output is reproducible
/// across servers). Italic is synthesized. Unknown families fall back to Noto Sans.
/// </summary>
public sealed class EmbeddedFontResolver : IFontResolver
{
    public const string Latin = "Noto Sans";
    public const string Ethiopic = "Noto Sans Ethiopic";

    private static readonly Lock InitLock = new();
    private static bool _installed;

    public static void EnsureInstalled()
    {
        lock (InitLock)
        {
            if (!_installed)
            {
                GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
                _installed = true;
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var ethiopic = string.Equals(familyName, Ethiopic, StringComparison.OrdinalIgnoreCase);
        var face = (ethiopic ? "NotoSansEthiopic" : "NotoSans") + (bold ? "-Bold" : "-Regular");
        return new FontResolverInfo(face, false, italic);
    }

    public byte[]? GetFont(string faceName)
    {
        var assembly = typeof(EmbeddedFontResolver).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("." + faceName + ".ttf", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded font not found: " + faceName);
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
