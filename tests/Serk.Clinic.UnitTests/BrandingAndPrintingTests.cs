using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Configuration.Contracts;
using Serk.Clinic.Configuration.Domain;
using Serk.Clinic.Documents.Contracts;
using Serk.Clinic.Documents.Rendering;
using UglyToad.PdfPig;

namespace Serk.Clinic.UnitTests;

public class BrandingAndPrintingTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Theory]
    [InlineData("logo.png", "image/png", 360, 120)]
    [InlineData("stamp.png", "image/png", 260, 260)]
    [InlineData("signature.jpg", "image/jpeg", 400, 120)]
    [InlineData("signature-progressive.jpg", "image/jpeg", 400, 120)]
    public void Valid_images_accepted_with_sniffed_type_and_dimensions(string file, string type, int w, int h)
    {
        var r = ImageInspector.Inspect(Fixture(file));
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal((type, w, h), (r.Value.ContentType, r.Value.Width, r.Value.Height));
        // The canonical form is itself a valid image of the same size, and canonicalising is idempotent.
        var again = ImageInspector.Inspect(r.Value.Canonical);
        Assert.True(again.IsSuccess, again.Error?.Message);
        Assert.Equal(r.Value.Canonical, again.Value.Canonical);
    }

    // ----- canonicalisation / hidden-payload cases built from the valid fixtures -----

    private static byte[] PngChunk(string type, byte[] body)
    {
        var chunk = new byte[12 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)body.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        body.CopyTo(chunk, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + body.Length), ImageInspector.Crc32(chunk.AsSpan(4, 4 + body.Length)));
        return chunk;
    }

    /// <summary>Inserts a chunk straight after IHDR (offset 33).</summary>
    private static byte[] WithChunkAfterIhdr(byte[] png, byte[] chunk) => [.. png[..33], .. chunk, .. png[33..]];

    [Fact]
    public void Png_text_and_exif_chunks_are_stripped_from_stored_bytes()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("Comment\0<script>alert(1)</script> GPS 9.03N 38.74E");
        var dirty = WithChunkAfterIhdr(WithChunkAfterIhdr(Fixture("logo.png"), PngChunk("tEXt", payload)), PngChunk("eXIf", [0x4D, 0x4D, 0, 42]));
        var r = ImageInspector.Inspect(dirty);
        Assert.True(r.IsSuccess, r.Error?.Message);
        var stored = System.Text.Encoding.Latin1.GetString(r.Value.Canonical);
        Assert.DoesNotContain("tEXt", stored);
        Assert.DoesNotContain("eXIf", stored);
        Assert.DoesNotContain("<script>", stored);
    }

    [Fact]
    public void Png_without_pixel_data_or_with_corrupt_pixel_data_is_rejected()
    {
        var png = Fixture("logo.png");
        var iend = PngChunk("IEND", []);
        // Header + IEND only (valid CRCs, no IDAT).
        Assert.Equal("malformed_image", ImageInspector.Inspect([.. png[..33], .. iend]).Error!.Code);
        // Valid CRC, but the zlib stream is garbage.
        Assert.Equal("malformed_image", ImageInspector.Inspect([.. png[..33], .. PngChunk("IDAT", [0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0x00, 0x13]), .. iend]).Error!.Code);
        // Valid zlib stream that inflates to far more data than the header declares (bomb shape).
        var bomb = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(bomb, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(new byte[8 * 1024 * 1024]);
        }

        Assert.Equal("malformed_image", ImageInspector.Inspect([.. png[..33], .. PngChunk("IDAT", bomb.ToArray()), .. iend]).Error!.Code);
    }

    [Fact]
    public void Jpeg_comment_and_exif_segments_are_stripped_from_stored_bytes()
    {
        var jpeg = Fixture("signature.jpg");
        var html = System.Text.Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");
        byte[] com = [0xFF, 0xFE, (byte)((html.Length + 2) >> 8), (byte)(html.Length + 2), .. html];
        var exifBody = System.Text.Encoding.ASCII.GetBytes("Exif\0\0MM GPSLatitude 9.03");
        byte[] app1 = [0xFF, 0xE1, (byte)((exifBody.Length + 2) >> 8), (byte)(exifBody.Length + 2), .. exifBody];
        var r = ImageInspector.Inspect([.. jpeg[..2], .. app1, .. com, .. jpeg[2..]]);
        Assert.True(r.IsSuccess, r.Error?.Message);
        var stored = System.Text.Encoding.Latin1.GetString(r.Value.Canonical);
        Assert.DoesNotContain("<script>", stored);
        Assert.DoesNotContain("Exif", stored);
        Assert.DoesNotContain("GPSLatitude", stored);
    }

    [Fact]
    public void Jpeg_without_scan_is_rejected()
    {
        var jpeg = Fixture("signature.jpg");
        var sos = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        Assert.Equal("malformed_image", ImageInspector.Inspect([.. jpeg[..sos], 0xFF, 0xD9]).Error!.Code);
    }

    [Theory]
    [InlineData("palette.png", "unsupported_png")]
    [InlineData("tiny.png", "bad_dimensions")]
    [InlineData("cmyk.jpg", "unsupported_jpeg")]
    [InlineData("anim.gif", "unsupported_format")]
    [InlineData("vector.svg", "unsupported_format")]
    [InlineData("polyglot.png", "trailing_data")]
    [InlineData("truncated.png", "malformed_image")]
    [InlineData("badcrc.png", "malformed_image")]
    public void Unsafe_or_unsupported_images_rejected(string file, string code) =>
        Assert.Equal(code, ImageInspector.Inspect(Fixture(file)).Error!.Code);

    [Fact]
    public void Oversize_and_empty_rejected()
    {
        Assert.Equal("file_too_large", ImageInspector.Inspect(new byte[ImageInspector.MaxBytes + 1]).Error!.Code);
        Assert.Equal("empty_file", ImageInspector.Inspect([]).Error!.Code);
        var jpeg = Fixture("signature.jpg");
        Assert.Equal("trailing_data", ImageInspector.Inspect([.. jpeg, 0x00]).Error!.Code);
    }

    [Theory]
    [InlineData("#1e4d3a", true)]   // default forest green, ~9.8:1
    [InlineData("#0f5132", true)]
    [InlineData("#767676", true)]   // ~4.54:1 boundary
    [InlineData("#777777", false)]  // ~4.48:1
    [InlineData("#ffcc00", false)]
    [InlineData("#FFFFFF", false)]
    [InlineData("green", false)]
    [InlineData("#12345", false)]
    public void Accent_colour_requires_readable_white_text(string hex, bool ok) => Assert.Equal(ok, AccentColor.Validate(hex).IsSuccess);

    [Fact]
    public void Contrast_ratio_known_values()
    {
        Assert.Equal(21, AccentColor.ContrastRatio("#000000", "#ffffff"), 2);
        Assert.Equal(1, AccentColor.ContrastRatio("#ffffff", "#ffffff"), 6);
    }

    [Fact]
    public void Thermal_paper_is_classified_and_never_allowed_for_clinical_documents()
    {
        Assert.Equal(PrinterKind.Thermal, PaperRules.KindOf(PaperSize.Thermal58));
        Assert.Equal(PrinterKind.Standard, PaperRules.KindOf(PaperSize.A3));
        Assert.False(PaperRules.AllowedFor(PrintPurpose.ClinicalDocuments, PaperSize.Thermal80));
        Assert.True(PaperRules.AllowedFor(PrintPurpose.AppointmentSlips, PaperSize.Thermal80));
        Assert.True(PaperRules.AllowedFor(PrintPurpose.ClinicalDocuments, PaperSize.A3));
        Assert.Equal((297, 420), PaperRules.Dimensions(PaperSize.A3));
    }

    private static (ConsultationDocumentSnapshotV1 Snap, Func<AssetRef, byte[]> Assets) Branded(string paper = "A4")
    {
        var s = DocumentRenderingTests.Snapshot(paper);
        var logo = new AssetRef(Guid.NewGuid(), new string('1', 64), "image/png");
        var stamp = new AssetRef(Guid.NewGuid(), new string('2', 64), "image/png");
        var sig = new AssetRef(Guid.NewGuid(), new string('3', 64), "image/jpeg");
        var map = new Dictionary<Guid, byte[]> { [logo.Id] = Fixture("logo.png"), [stamp.Id] = Fixture("stamp.png"), [sig.Id] = Fixture("signature.jpg") };
        var branding = new BrandingBlock("#0f5132", logo, stamp, new Dictionary<string, AssetRef> { [s.SignedParts[0].Note.SignerPractitionerId.ToString()] = sig });
        return (s with { Branding = branding }, r => map[r.Id]);
    }

    [Fact(DisplayName = "Template v3: final carries logo, signature, stamp and QR; preview carries logo and QR only, with placeholder")]
    public void Branded_final_and_preview()
    {
        var (s, assets) = Branded();
        using var final = PdfDocument.Open(ConsultationSummaryRenderer.Render(s, RenderMode.Final, assets));
        using var preview = PdfDocument.Open(ConsultationSummaryRenderer.Render(s, RenderMode.Preview, assets));
        Assert.Equal(4, final.GetPages().Sum(p => p.GetImages().Count()));
        Assert.Equal(2, preview.GetPages().Sum(p => p.GetImages().Count()));
        var previewText = DocumentRenderingTests.HorizontalText(ConsultationSummaryRenderer.Render(s, RenderMode.Preview, assets));
        Assert.Contains(ConsultationSummaryRenderer.SignaturePlaceholder, previewText);
        Assert.DoesNotContain(ConsultationSummaryRenderer.SignaturePlaceholder, DocumentRenderingTests.HorizontalText(ConsultationSummaryRenderer.Render(s, RenderMode.Final, assets)));
    }

    [Fact]
    public void Snapshots_without_branding_keep_their_original_canonical_json()
    {
        var s = DocumentRenderingTests.Snapshot();
        Assert.DoesNotContain("branding", Serk.Clinic.SharedKernel.CanonicalJson.Serialize(s), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A3", 841.9, 1190.6)]
    [InlineData("A4", 595.3, 841.9)]
    [InlineData("A5", 419.5, 595.3)]
    public void Clinical_paper_sizes(string paper, double w, double h)
    {
        var (s, assets) = Branded(paper);
        using var pdf = PdfDocument.Open(ConsultationSummaryRenderer.Render(s, RenderMode.Final, assets));
        Assert.Equal(w, pdf.GetPage(1).Width, 0);
        Assert.Equal(h, pdf.GetPage(1).Height, 0);
    }

    [Theory]
    [InlineData(SlipPaper.Thermal80, 226.8)]
    [InlineData(SlipPaper.Thermal58, 164.4)]
    [InlineData(SlipPaper.A5, 419.5)]
    public void Appointment_slip_sizes_and_content(SlipPaper paper, double widthPt)
    {
        var slip = new AppointmentSlip("Synthetic Clinic", "+251", "Selam Tesfaye", "MRN-000001", "Dr. Test", new DateTimeOffset(2026, 11, 10, 6, 30, 0, TimeSpan.Zero), "Africa/Addis_Ababa", "AB12CD34", Fixture("logo.png"));
        var bytes = AppointmentSlipRenderer.Render(slip, paper);
        using var pdf = PdfDocument.Open(bytes);
        Assert.Equal(widthPt, pdf.GetPage(1).Width, 0);
        var text = DocumentRenderingTests.HorizontalText(bytes);
        Assert.Contains("09:30 EAT", text);       // 06:30 UTC -> 09:30 Addis
        Assert.Contains("Hidar 1, 2019 E.C.", text); // 2026-11-10
        Assert.Contains("not a medical document", text);
        Assert.Single(pdf.GetPage(1).GetImages());
    }
}
