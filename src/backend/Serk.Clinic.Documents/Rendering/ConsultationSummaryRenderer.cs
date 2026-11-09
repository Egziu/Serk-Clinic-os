using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using QRCoder;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Configuration.Contracts;
using Serk.Clinic.Documents.Contracts;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Rendering;

public enum RenderMode
{
    /// <summary>Watermarked, footer states it is not valid. Same body as final.</summary>
    Preview,
    Final,
}

/// <summary>
/// Template "consultation-summary" version 1. Pure function of (snapshot, mode): no database or clock access, so the
/// preview and the final PDF are produced from identical inputs by identical code. All patient text is inserted as
/// plain text runs (no markup interpretation), so stored content cannot inject layout or scripts.
/// </summary>
public static class ConsultationSummaryRenderer
{
    public const string TemplateCode = "consultation-summary";
    public const int TemplateVersion = 3;
    public const string PreviewWatermark = "PREVIEW - NOT ISSUED";
    public const string PreviewFooter = "DRAFT PREVIEW - NOT VALID FOR CLINICAL OR LEGAL USE";
    public const string FinalFooter = "Final document";

    private static readonly Color Ink = new(0x1F, 0x24, 0x22);
    private static readonly Color Forest = new(0x1E, 0x4D, 0x3A);
    private static readonly Color Muted = new(0x5E, 0x66, 0x62);
    private static readonly Color Rule = new(0xC9, 0xD1, 0xCC);

    public const string SignaturePlaceholder = "[Signature image and stamp are applied only to the issued document]";

    /// <param name="assets">Resolves frozen branding images (already hash-verified). Null renders without images.</param>
    public static byte[] Render(ConsultationDocumentSnapshotV1 s, RenderMode mode, Func<AssetRef, byte[]>? assets = null)
    {
        EmbeddedFontResolver.EnsureInstalled();
        var accent = ParseColor(s.Branding?.AccentColor) ?? Forest;
        var wide = s.PaperSize == "A3";
        _headingColor = accent;
        var zone = BusinessTime.Resolve(s.Facility.TimeZone);
        var doc = new Document();
        doc.Info.Title = "Consultation summary";
        doc.Info.Author = s.Facility.Name;
        doc.Info.Subject = "SERK Clinic OS document " + s.VerificationRef;

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.Latin;
        normal.Font.Size = s.PaperSize switch { "A5" => 8, "A3" => 11, _ => 9.5 };
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        // PageFormat alone does not resize a cloned setup; set explicit dimensions (A3 297x420, A4 210x297, A5 148x210 mm).
        var (pw, ph) = s.PaperSize switch { "A5" => (148, 210), "A3" => (297, 420), _ => (210, 297) };
        section.PageSetup.PageFormat = s.PaperSize switch { "A5" => PageFormat.A5, "A3" => PageFormat.A3, _ => PageFormat.A4 };
        section.PageSetup.PageWidth = Unit.FromMillimeter(pw);
        section.PageSetup.PageHeight = Unit.FromMillimeter(ph);
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(s.PaperSize == "A5" ? 1.4 : 2.0);
        section.PageSetup.RightMargin = section.PageSetup.LeftMargin;
        section.PageSetup.HeaderDistance = Unit.FromCentimeter(1.0);
        section.PageSetup.FooterDistance = Unit.FromCentimeter(0.8);

        // Running header: issuing facility (letterhead), with the clinic logo when one is configured.
        if (s.Branding?.Logo is { } logoRef && assets is not null)
        {
            var logo = section.Headers.Primary.AddImage(Base64Image(assets(logoRef)));
            logo.Height = Unit.FromCentimeter(wide ? 1.6 : 1.2);
            logo.LockAspectRatio = true;
            logo.WrapFormat.Style = MigraDoc.DocumentObjectModel.Shapes.WrapStyle.Through;
            logo.RelativeHorizontal = MigraDoc.DocumentObjectModel.Shapes.RelativeHorizontal.Margin;
            logo.Left = MigraDoc.DocumentObjectModel.Shapes.ShapePosition.Right;
        }

        var header = section.Headers.Primary.AddParagraph();
        Append(header, s.Facility.Name, bold: true, size: 12, color: accent);
        var headerLine = section.Headers.Primary.AddParagraph();
        Append(headerLine, Join(" | ", s.Facility.AddressLine, s.Facility.Phone, s.Facility.LicenseNumber.Length > 0 ? "Facility license " + s.Facility.LicenseNumber : ""), size: 8, color: Muted);
        headerLine.Format.Borders.Bottom.Color = Rule;
        headerLine.Format.Borders.Bottom.Width = Unit.FromPoint(0.5);

        // Running footer: validity status + pagination + document reference.
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = mode == RenderMode.Preview ? new Color(0xA3, 0x1D, 0x1D) : Muted;
        footer.AddText((mode == RenderMode.Preview ? PreviewFooter : FinalFooter) + "  |  Ref " + s.VerificationRef + "  |  Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        var title = section.AddParagraph();
        Append(title, "Consultation Summary", bold: true, size: wide ? 18 : 15, color: accent);
        title.Format.SpaceAfter = Unit.FromPoint(6);

        // Patient identity block — always first, never hidden.
        var p = s.Patient;
        var idTable = KeyValueTable(section, s.PaperSize);
        KeyValue(idTable, "Patient", p.DisplayName);
        KeyValue(idTable, "MRN", p.Mrn);
        KeyValue(idTable, "Sex", p.Sex);
        KeyValue(idTable, "Date of birth", FormatDate(p.BirthDate) + (p.BirthDateIsEstimated ? " (estimated)" : ""));
        KeyValue(idTable, "Age at signing", p.AgeYearsAtSigning.ToString(CultureInfo.InvariantCulture) + " years");
        KeyValue(idTable, "Encounter date", FormatInstant(s.EncounterStartedAtUtc, zone));

        var original = s.SignedParts[0].Note;
        var latest = s.SignedParts[^1].Note;
        if (s.SignedParts.Count > 1)
        {
            // Template v2: amendments are flagged before the clinical content, not only after the signature (HAZ-012).
            var amended = section.AddParagraph();
            amended.Format.Borders.Left.Color = new Color(0xA3, 0x1D, 0x1D);
            amended.Format.Borders.Left.Width = Unit.FromPoint(2);
            amended.Format.LeftIndent = Unit.FromPoint(6);
            Append(amended, "AMENDED: this record has " + (s.SignedParts.Count - 1).ToString(CultureInfo.InvariantCulture)
                + " signed addendum(s) below. Read them before acting on the content.", bold: true, color: new Color(0xA3, 0x1D, 0x1D));
        }

        // Template v2: allergies as reviewed at the most recent signature (HAZ-010), with explicit wording per state (HAZ-007).
        Heading(section, "Allergies (as reviewed at the latest signature, " + FormatInstant(latest.SignedAtUtc, zone) + ")");
        var allergyText = latest.AllergyStatusAtSigning switch
        {
            "NoKnownAllergies" => "No known allergies (recorded).",
            "HasAllergies" when latest.ActiveAllergiesAtSigning.Count > 0 => string.Join("; ", latest.ActiveAllergiesAtSigning.Select(a =>
                a.Substance + " - " + a.Severity + (a.Reaction.Length > 0 ? " (" + a.Reaction + ")" : ""))),
            "HasAllergies" => "Allergies recorded but the list was unavailable at signing - CHECK THE CURRENT RECORD.",
            _ => "ALLERGY STATUS NOT RECORDED - check the current record.",
        };
        Body(section, allergyText);
        Body(section, "Always confirm allergies against the current record before prescribing or treating.");

        var content = original.Content ?? NoteContentV1.Empty;
        Section(section, "Presenting complaint", content.ChiefComplaint);
        Section(section, "History", content.History);
        Section(section, "Examination", content.Examination);
        Section(section, "Assessment", content.Assessment);
        Section(section, "Plan", content.Plan);
        SignatureBlock(section, s.SignedParts[0], zone, s, mode, assets);

        foreach (var part in s.SignedParts.Skip(1))
        {
            Heading(section, "Addendum " + (part.VersionNo - 1).ToString(CultureInfo.InvariantCulture));
            Body(section, "Reason: " + part.Note.AddendumReason);
            Body(section, part.Note.AddendumText ?? "");
            SignatureBlock(section, part, zone, s, mode, assets);
        }

        // Verification: QR contains only an opaque reference URL — never clinical data.
        Heading(section, "Verification");
        var verify = section.AddTable();
        verify.AddColumn(Unit.FromCentimeter(2.6));
        verify.AddColumn(Unit.FromCentimeter(s.PaperSize == "A5" ? 6.4 : 10.4));
        verify.AddColumn(Unit.FromCentimeter(3.0));
        var row = verify.AddRow();
        if (mode == RenderMode.Final && s.Branding?.Stamp is { } stampRef && assets is not null)
        {
            var stamp = row.Cells[2].AddImage(Base64Image(assets(stampRef)));
            stamp.Width = Unit.FromCentimeter(2.8);
            stamp.LockAspectRatio = true;
        }
        var image = row.Cells[0].AddImage("base64:" + Convert.ToBase64String(QrPng(s.VerificationUrl)));
        image.Width = Unit.FromCentimeter(2.3);
        image.LockAspectRatio = true;
        var vp = row.Cells[1].AddParagraph();
        Append(vp, "Document reference: " + s.VerificationRef, size: 8, color: Muted);
        var vp2 = row.Cells[1].AddParagraph();
        Append(vp2, "Verify at: " + s.VerificationUrl, size: 8, color: Muted);
        var vp3 = row.Cells[1].AddParagraph();
        Append(vp3, "Template " + s.TemplateCode + " v" + s.TemplateVersion.ToString(CultureInfo.InvariantCulture) + " | Generated " + FormatInstant(s.GeneratedAtUtc, zone), size: 8, color: Muted);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        var pdf = renderer.PdfDocument;
        pdf.Info.CreationDate = s.GeneratedAtUtc.UtcDateTime;
        pdf.Info.ModificationDate = s.GeneratedAtUtc.UtcDateTime;
        if (mode == RenderMode.Preview)
        {
            Watermark(pdf);
        }

        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }

    private static void SignatureBlock(Section section, SignedPart part, TimeZoneInfo zone, ConsultationDocumentSnapshotV1 s, RenderMode mode, Func<AssetRef, byte[]>? assets)
    {
        var n = part.Note;
        var signatureRef = s.Branding?.SignaturesByPractitioner.GetValueOrDefault(n.SignerPractitionerId.ToString());
        if (signatureRef is not null && assets is not null)
        {
            if (mode == RenderMode.Final)
            {
                var image = section.AddImage(Base64Image(assets(signatureRef)));
                image.Height = Unit.FromCentimeter(1.3);
                image.LockAspectRatio = true;
            }
            else
            {
                Append(section.AddParagraph(), SignaturePlaceholder, size: 8, color: Muted);
            }
        }

        var para = section.AddParagraph();
        para.Format.SpaceBefore = Unit.FromPoint(6);
        para.Format.Borders.Top.Color = Rule;
        para.Format.Borders.Top.Width = Unit.FromPoint(0.5);
        Append(para, "Electronically signed by " + n.SignerDisplayName + ", " + n.SignerCredential, bold: true);
        var when = section.AddParagraph();
        Append(when, "Signed " + FormatInstant(n.SignedAtUtc, zone) + " | Note version " + part.VersionNo.ToString(CultureInfo.InvariantCulture)
            + " | SHA-256 " + part.ContentSha256[..16], size: 8, color: Muted);
    }

    public static string Base64Image(byte[] bytes) => "base64:" + Convert.ToBase64String(bytes);

    public static Color? ParseColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : null;

    private static Table KeyValueTable(Section section, string paper)
    {
        var t = section.AddTable();
        t.AddColumn(Unit.FromCentimeter(paper == "A5" ? 3.0 : 3.6));
        t.AddColumn(Unit.FromCentimeter(paper == "A5" ? 9.0 : 13.0));
        t.Borders.Bottom.Color = Rule;
        t.Borders.Bottom.Width = Unit.FromPoint(0.25);
        return t;
    }

    private static void KeyValue(Table t, string key, string value)
    {
        var row = t.AddRow();
        Append(row.Cells[0].AddParagraph(), key, color: Muted);
        Append(row.Cells[1].AddParagraph(), value, bold: key is "Patient" or "MRN");
    }

    [ThreadStatic]
    private static Color? _headingColor;

    private static void Heading(Section section, string text)
    {
        var h = section.AddParagraph();
        h.Format.SpaceBefore = Unit.FromPoint(8);
        h.Format.KeepWithNext = true;
        Append(h, text, bold: true, size: 10.5, color: _headingColor ?? Forest);
    }

    private static void Section(Section section, string heading, string text)
    {
        Heading(section, heading);
        Body(section, string.IsNullOrWhiteSpace(text) ? "Not recorded." : text);
    }

    private static void Body(Section section, string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            Append(section.AddParagraph(), line);
        }
    }

    /// <summary>Appends text as plain runs, switching to the Ethiopic font for Ge'ez script code points.</summary>
    public static void Append(Paragraph paragraph, string text, bool bold = false, double? size = null, Color? color = null)
    {
        foreach (var (segment, ethiopic) in SegmentByScript(text))
        {
            var run = paragraph.AddFormattedText(segment);
            run.Font.Name = ethiopic ? EmbeddedFontResolver.Ethiopic : EmbeddedFontResolver.Latin;
            run.Bold = bold;
            if (size is { } sz)
            {
                run.Size = Unit.FromPoint(sz);
            }

            if (color is { } c)
            {
                run.Color = c;
            }
        }
    }

    public static IEnumerable<(string Segment, bool Ethiopic)> SegmentByScript(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var start = 0;
        var current = IsEthiopic(text[0]);
        for (var i = 1; i < text.Length; i++)
        {
            var e = IsEthiopic(text[i]);
            // Spaces and punctuation stay in the current run to avoid needless font switches.
            if (e != current && !char.IsWhiteSpace(text[i]))
            {
                yield return (text[start..i], current);
                start = i;
                current = e;
            }
        }

        yield return (text[start..], current);
    }

    public static bool IsEthiopic(char c) => c is (>= 'ሀ' and <= '᎟') or (>= 'ⶀ' and <= '⷟') or (>= '꬀' and <= '꬯');

    public static string FormatDate(DateOnly d) =>
        d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (Gregorian) / " + EthiopianDate.FromGregorian(d);

    public static string FormatInstant(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            + " UTC" + sign + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            + " (Gregorian) / " + EthiopianDate.FromGregorian(DateOnly.FromDateTime(local.DateTime));
    }

    private static string Join(string sep, params string[] parts) => string.Join(sep, parts.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static byte[] QrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        // RGBA output: PDFsharp's cross-platform build rejects QRCoder's default 1-bit palette PNG.
        return new PngByteQRCode(data).GetGraphic(8, [0, 0, 0, 255], [255, 255, 255, 255], drawQuietZones: true);
    }

    private static void Watermark(PdfDocument pdf)
    {
        var font = new XFont(EmbeddedFontResolver.Latin, 42, XFontStyleEx.Bold);
        var brush = new XSolidBrush(XColor.FromArgb(110, 163, 29, 29));
        foreach (var page in pdf.Pages)
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var center = new XPoint(page.Width.Point / 2, page.Height.Point / 2);
            gfx.RotateAtTransform(-35, center);
            gfx.DrawString(PreviewWatermark, font, brush, new XRect(0, center.Y - 40, page.Width.Point, 80), XStringFormats.Center);
        }
    }
}
