using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Rendering;

public enum SlipPaper
{
    Thermal80,
    Thermal58,
    A5,
    A4,
}

/// <summary>Non-clinical appointment slip data (minimum necessary: no diagnosis, no allergies).</summary>
public sealed record AppointmentSlip(
    string FacilityName,
    string FacilityPhone,
    string PatientName,
    string Mrn,
    string ClinicianName,
    DateTimeOffset StartsAtUtc,
    string TimeZone,
    string Reference,
    byte[]? Logo);

/// <summary>
/// Renders appointment slips for receipt-style thermal roll printers (58/80 mm) or standard A5/A4 printers.
/// Thermal layout uses 3 mm margins and a 150 mm cut length; monochrome; larger time/date for legibility.
/// </summary>
public static class AppointmentSlipRenderer
{
    public static (double WidthMm, double HeightMm) Size(SlipPaper paper) => paper switch
    {
        SlipPaper.Thermal80 => (80, 150),
        SlipPaper.Thermal58 => (58, 150),
        SlipPaper.A5 => (148, 210),
        _ => (210, 297),
    };

    public static byte[] Render(AppointmentSlip slip, SlipPaper paper)
    {
        EmbeddedFontResolver.EnsureInstalled();
        var thermal = paper is SlipPaper.Thermal58 or SlipPaper.Thermal80;
        var narrow = paper == SlipPaper.Thermal58;
        var (w, h) = Size(paper);
        var zone = BusinessTime.Resolve(slip.TimeZone);
        var local = TimeZoneInfo.ConvertTime(slip.StartsAtUtc, zone);

        var doc = new Document();
        doc.Info.Title = "Appointment slip";
        doc.Info.Author = slip.FacilityName;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.Latin;
        normal.Font.Size = narrow ? 7.5 : thermal ? 8.5 : 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageWidth = Unit.FromMillimeter(w);
        section.PageSetup.PageHeight = Unit.FromMillimeter(h);
        var margin = Unit.FromMillimeter(thermal ? 3 : 15);
        section.PageSetup.LeftMargin = margin;
        section.PageSetup.RightMargin = margin;
        section.PageSetup.TopMargin = margin;
        section.PageSetup.BottomMargin = margin;

        if (slip.Logo is not null)
        {
            var logo = section.AddImage(ConsultationSummaryRenderer.Base64Image(slip.Logo));
            logo.Height = Unit.FromMillimeter(thermal ? 10 : 15);
            logo.LockAspectRatio = true;
        }

        Line(section, slip.FacilityName, bold: true, size: thermal ? 10 : 13, center: thermal);
        if (slip.FacilityPhone.Length > 0)
        {
            Line(section, slip.FacilityPhone, size: thermal ? 7 : 9, center: thermal);
        }

        Rule(section);
        Line(section, "APPOINTMENT SLIP", bold: true, size: thermal ? 9 : 12, center: thermal);
        Line(section, slip.PatientName, bold: true, size: thermal ? 10 : 12);
        Line(section, "MRN " + slip.Mrn);
        Rule(section);
        Line(section, local.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture) + " (Gregorian)", bold: true, size: thermal ? 11 : 13);
        Line(section, EthiopianDate.FromGregorian(DateOnly.FromDateTime(local.DateTime)).ToString());
        Line(section, local.ToString("HH:mm", CultureInfo.InvariantCulture) + " EAT (UTC+03:00)", bold: true, size: thermal ? 14 : 16);
        Line(section, "With: " + slip.ClinicianName);
        Rule(section);
        Line(section, "Ref " + slip.Reference, size: thermal ? 7 : 8);
        Line(section, "Please arrive 15 minutes early. This slip is not a medical document.", size: thermal ? 6.5 : 8);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void Line(Section section, string text, bool bold = false, double? size = null, bool center = false)
    {
        var p = section.AddParagraph();
        if (center)
        {
            p.Format.Alignment = ParagraphAlignment.Center;
        }

        ConsultationSummaryRenderer.Append(p, text, bold, size);
    }

    private static void Rule(Section section)
    {
        var p = section.AddParagraph();
        p.Format.Borders.Bottom.Width = Unit.FromPoint(0.5);
        p.Format.SpaceAfter = Unit.FromPoint(3);
    }
}
