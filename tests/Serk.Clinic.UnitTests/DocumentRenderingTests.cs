using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Documents.Contracts;
using Serk.Clinic.Documents.Rendering;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Serk.Clinic.UnitTests;

/// <summary>DOC-002 / DOC-003 at renderer level (pure function of snapshot + mode).</summary>
public class DocumentRenderingTests
{
    internal static ConsultationDocumentSnapshotV1 Snapshot(string paper = "A4", string name = "Selam Tesfaye Bekele", string plan = "Fluids and rest")
    {
        var signedAt = new DateTimeOffset(2026, 11, 9, 7, 30, 0, TimeSpan.Zero);
        var note = new SignedNoteSnapshotV1(1, Guid.NewGuid(), Guid.NewGuid(), 1, "Original", null,
            new NoteContentV1("Cough for 3 days", "No fever.\nNon-smoker.", "Chest clear", "Viral upper respiratory infection", plan),
            null, null, Guid.NewGuid(), "Dr. Test Clinician", "MD, License SYN-1", signedAt, true, "HasAllergies", [new AllergySnapshot("Penicillin", "Rash", "Moderate")]);
        return new ConsultationDocumentSnapshotV1(1, ConsultationSummaryRenderer.TemplateCode, 1, paper, "REFabcdefghijklmnop", "http://localhost/verify/REFabcdefghijklmnop",
            signedAt.AddMinutes(5), new FacilityBlock("Synthetic Clinic", "Road 1", "+251", "LIC", "Africa/Addis_Ababa"),
            new PatientBlock("MRN-000001", name, "Female", new DateOnly(1990, 3, 14), false, 36), note.EncounterId, signedAt.AddMinutes(-20),
            [new SignedPart(Guid.NewGuid(), 1, new string('a', 64), note)]);
    }

    internal static string HorizontalText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Where(w => w.TextOrientation == TextOrientation.Horizontal).Select(w => w.Text))));
    }

    [Fact]
    public void Preview_and_final_have_identical_body_and_differ_only_in_validity_marking()
    {
        var s = Snapshot();
        var preview = ConsultationSummaryRenderer.Render(s, RenderMode.Preview);
        var final = ConsultationSummaryRenderer.Render(s, RenderMode.Final);
        var pt = HorizontalText(preview).Replace(ConsultationSummaryRenderer.PreviewFooter, "<FOOTER>", StringComparison.Ordinal);
        var ft = HorizontalText(final).Replace(ConsultationSummaryRenderer.FinalFooter, "<FOOTER>", StringComparison.Ordinal);
        Assert.Equal(ft, pt);
        Assert.Contains("Viral upper respiratory infection", ft);
        Assert.Contains("Penicillin", ft);
        Assert.Contains("MRN-000001", ft);
    }

    [Fact]
    public void Preview_is_watermarked_and_final_is_not()
    {
        var s = Snapshot();
        using var preview = PdfDocument.Open(ConsultationSummaryRenderer.Render(s, RenderMode.Preview));
        using var final = PdfDocument.Open(ConsultationSummaryRenderer.Render(s, RenderMode.Final));
        static string Rotated(PdfDocument d) => string.Concat(d.GetPages().SelectMany(p => p.Letters).Where(l => l.TextOrientation != TextOrientation.Horizontal).Select(l => l.Value));
        Assert.Contains("NOTISSUED", Rotated(preview).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Empty(Rotated(final));
        Assert.Contains(ConsultationSummaryRenderer.PreviewFooter, HorizontalText(ConsultationSummaryRenderer.Render(s, RenderMode.Preview)));
        Assert.DoesNotContain("DRAFT", HorizontalText(ConsultationSummaryRenderer.Render(s, RenderMode.Final)));
    }

    [Fact]
    public void Qr_code_image_is_embedded_and_contains_no_patient_data_in_metadata()
    {
        var bytes = ConsultationSummaryRenderer.Render(Snapshot(), RenderMode.Final);
        using var doc = PdfDocument.Open(bytes);
        Assert.NotEmpty(doc.GetPage(1).GetImages());
        Assert.DoesNotContain("Selam", doc.Information.Title ?? "");
        Assert.DoesNotContain("Selam", doc.Information.Subject ?? "");
        Assert.DoesNotContain("Image has no valid type", HorizontalText(bytes));
    }

    [Fact]
    public void A5_and_long_content_paginate_with_page_numbers()
    {
        var longPlan = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"Plan line {i}: synthetic follow-up instruction."));
        var bytes = ConsultationSummaryRenderer.Render(Snapshot("A5", plan: longPlan), RenderMode.Final);
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages > 1);
        var page1 = doc.GetPage(1);
        Assert.InRange(page1.Width, 418, 422); // A5 width in points
        Assert.Contains($"Page 1 of {doc.NumberOfPages}", HorizontalText(bytes));
        Assert.Contains("Plan line 120", HorizontalText(bytes));
    }

    [Fact]
    public void Ethiopic_script_names_render_with_embedded_font()
    {
        var bytes = ConsultationSummaryRenderer.Render(Snapshot(name: "ሰላም ተስፋዬ (Selam)"), RenderMode.Final);
        var text = HorizontalText(bytes);
        Assert.Contains("ሰላም", text);
        Assert.Contains("(Selam)", text);
        Assert.Contains("Megabit 5, 1982 E.C.", HorizontalText(ConsultationSummaryRenderer.Render(Snapshot(), RenderMode.Final))); // 1990-03-14 shown with E.C. date
    }

    [Fact]
    public void Script_segmentation_splits_ethiopic_and_latin_runs()
    {
        var runs = ConsultationSummaryRenderer.SegmentByScript("Dr. ሰላም ok").ToList();
        Assert.Equal([("Dr. ", false), ("ሰላም ", true), ("ok", false)], runs);
    }

    [Fact]
    public void Markup_like_patient_text_is_rendered_literally()
    {
        var bytes = ConsultationSummaryRenderer.Render(Snapshot(plan: "<script>alert(1)</script> \\b{bold} %PDF"), RenderMode.Final);
        Assert.Contains("<script>alert(1)</script>", HorizontalText(bytes));
    }
}

public class DocumentTemplateV2Tests
{
    [Fact(DisplayName = "HAZ-010/012 template v2 flags amendments before content and prints allergies from the latest signature")]
    public void Amendment_flag_and_latest_allergies()
    {
        var s = DocumentRenderingTests.Snapshot();
        var original = s.SignedParts[0];
        var addendumNote = original.Note with
        {
            VersionNo = 2, Kind = "Addendum", Content = null, AddendumText = "Plan revised.", AddendumReason = "Lab result",
            SignedAtUtc = original.Note.SignedAtUtc.AddHours(2), AllergyStatusAtSigning = "HasAllergies",
            ActiveAllergiesAtSigning = [new("Penicillin", "Rash", "Moderate"), new("Latex", "", "Severe")],
        };
        var amended = s with { SignedParts = [original, new SignedPart(Guid.NewGuid(), 2, new string('b', 64), addendumNote)] };
        var text = DocumentRenderingTests.HorizontalText(ConsultationSummaryRenderer.Render(amended, RenderMode.Final));
        Assert.True(text.IndexOf("AMENDED", StringComparison.Ordinal) < text.IndexOf("Presenting complaint", StringComparison.Ordinal));
        Assert.Contains("Latex - Severe", text);
        Assert.Contains("Addendum 1", text);
        Assert.DoesNotContain("AMENDED", DocumentRenderingTests.HorizontalText(ConsultationSummaryRenderer.Render(s, RenderMode.Final)));
    }

    [Fact(DisplayName = "HAZ-007 unknown allergy status prints an explicit warning, never a blank")]
    public void Unknown_allergy_status_is_explicit()
    {
        var s = DocumentRenderingTests.Snapshot();
        var unknown = s with { SignedParts = [s.SignedParts[0] with { Note = s.SignedParts[0].Note with { AllergyStatusAtSigning = "Unknown", ActiveAllergiesAtSigning = [] } }] };
        Assert.Contains("ALLERGY STATUS NOT RECORDED", DocumentRenderingTests.HorizontalText(ConsultationSummaryRenderer.Render(unknown, RenderMode.Final)));
    }
}
