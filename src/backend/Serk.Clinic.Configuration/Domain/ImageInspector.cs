using System.Buffers.Binary;
using System.IO.Compression;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Domain;

/// <summary>Validated image. <see cref="Canonical"/> is the metadata-free re-serialisation that is stored and served.</summary>
public sealed record ImageInfo(string ContentType, int Width, int Height, byte[] Canonical);

/// <summary>
/// Strict validation and canonicalisation of uploaded branding images (logo, stamp, signature). Only baseline/progressive
/// JPEG and 8-bit RGB/RGBA non-interlaced PNG are accepted. The file must be exactly one well-formed image: magic bytes,
/// chunk/segment structure, PNG CRCs, PNG pixel data that inflates to exactly the size the header declares (bounded, so a
/// decompression bomb is cut off), a complete JPEG marker walk to EOI, and no trailing bytes. The stored bytes are then
/// re-serialised keeping only the chunks/segments needed to draw the image: text, EXIF/GPS, XMP, ICC, comments and
/// thumbnails are removed, so metadata and HTML/script payloads hidden in comments never reach storage or PDFs.
/// SVG, GIF, WebP, BMP and anything else are rejected. Images are served back only as their sniffed type with nosniff.
/// </summary>
public static class ImageInspector
{
    public const int MaxBytes = 512 * 1024;
    public const int MinDimension = 16;
    public const int MaxDimension = 4096;

    /// <summary>At most 4 megapixels (16 MiB of RGBA when decoded) to bound memory in PDF rendering.</summary>
    public const long MaxPixels = 4L * 1024 * 1024;

    /// <summary>Upper bound on progressive JPEG scans (each scan costs a decoding pass).</summary>
    public const int MaxJpegScans = 32;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Result<ImageInfo> Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return Error.Validation("empty_file", "The file is empty.");
        }

        if (data.Length > MaxBytes)
        {
            return Error.Validation("file_too_large", $"Images must be at most {MaxBytes / 1024} KB.");
        }

        Result<ImageInfo> result = data.StartsWith(PngSignature) ? InspectPng(data)
            : data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF ? InspectJpeg(data)
            : Error.Validation("unsupported_format", "Only PNG or JPEG images are accepted.");
        if (!result.IsSuccess)
        {
            return result;
        }

        var (w, h) = (result.Value.Width, result.Value.Height);
        if (w < MinDimension || h < MinDimension || w > MaxDimension || h > MaxDimension)
        {
            return Error.Validation("bad_dimensions", $"Image dimensions must be between {MinDimension} and {MaxDimension} pixels.");
        }

        if ((long)w * h > MaxPixels)
        {
            return Error.Validation("bad_dimensions", "Images may have at most 4 megapixels; scale the image down (a logo rarely needs more than 1200 pixels wide).");
        }

        return result;
    }

    private static Result<ImageInfo> InspectPng(ReadOnlySpan<byte> d)
    {
        var pos = 8;
        int width = 0, height = 0, channels = 0;
        var first = true;
        var idat = new List<(int Start, int Length)>();
        var idatEnded = false;
        while (pos + 12 <= d.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]);
            if (length > int.MaxValue || pos + 12 + (long)length > d.Length)
            {
                return Malformed();
            }

            var type = d.Slice(pos + 4, 4);
            var body = d.Slice(pos + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(d[(pos + 8 + (int)length)..]);
            if (Crc32(d.Slice(pos + 4, 4 + (int)length)) != crc)
            {
                return Malformed();
            }

            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    return Malformed();
                }

                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                byte bitDepth = body[8], colorType = body[9], compression = body[10], filter = body[11], interlace = body[12];
                if (bitDepth != 8 || colorType is not (2 or 6) || interlace != 0 || compression != 0 || filter != 0)
                {
                    return Error.Validation("unsupported_png", "Save the PNG as 24-bit RGB or 32-bit RGBA, non-interlaced (palette and grayscale PNGs are not supported).");
                }

                channels = colorType == 6 ? 4 : 3;
                first = false;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (idatEnded)
                {
                    return Malformed(); // IDAT chunks must be consecutive
                }

                idat.Add((pos + 8, (int)length));
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                pos += 12 + (int)length;
                if (pos != d.Length)
                {
                    return Error.Validation("trailing_data", "The file contains data after the end of the image and was rejected.");
                }

                return idat.Count == 0 ? Malformed() : CanonicalPng(d, width, height, channels, idat);
            }
            else if ((type[0] & 0x20) == 0 && !type.SequenceEqual("PLTE"u8))
            {
                return Malformed(); // unknown critical chunk
            }

            if (idat.Count > 0 && !type.SequenceEqual("IDAT"u8))
            {
                idatEnded = true;
            }

            pos += 12 + (int)length;
        }

        return Malformed();
    }

    /// <summary>
    /// Inflates the pixel data with a hard output bound and checks it is exactly height × (1 + width × channels) bytes with
    /// valid row filter types; then rebuilds the file as IHDR + IDAT + IEND (all ancillary chunks dropped).
    /// </summary>
    private static Result<ImageInfo> CanonicalPng(ReadOnlySpan<byte> d, int width, int height, int channels, List<(int Start, int Length)> idat)
    {
        var compressed = new byte[idat.Sum(c => c.Length)];
        var offset = 0;
        foreach (var (start, length) in idat)
        {
            d.Slice(start, length).CopyTo(compressed.AsSpan(offset));
            offset += length;
        }

        var rowBytes = 1L + ((long)width * channels);
        var expected = rowBytes * height;
        try
        {
            using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if ((total + i) % rowBytes == 0 && buffer[i] > 4)
                    {
                        return Malformed(); // invalid row filter type
                    }
                }

                total += read;
                if (total > expected)
                {
                    return Malformed(); // more pixel data than declared (or decompression bomb): stop reading
                }
            }

            if (total != expected)
            {
                return Malformed();
            }
        }
        catch (InvalidDataException)
        {
            return Malformed();
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);
        output.Write(d.Slice(8, 25)); // IHDR chunk (validated as the first chunk, 13-byte body)
        foreach (var (start, length) in idat)
        {
            output.Write(d.Slice(start - 8, length + 12));
        }

        output.Write([0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]); // IEND
        return new ImageInfo("image/png", width, height, output.ToArray());
    }

    private static Result<ImageInfo> InspectJpeg(ReadOnlySpan<byte> d)
    {
        if (d[^2] != 0xFF || d[^1] != 0xD9)
        {
            return Error.Validation("trailing_data", "The JPEG does not end with an end-of-image marker (possible appended data).");
        }

        using var output = new MemoryStream(); // everything after SOI and the header written at the end
        var adobe = false;
        int width = 0, height = 0, scans = 0;
        var frameSeen = false;
        var pos = 2;
        while (pos + 2 <= d.Length)
        {
            if (d[pos] != 0xFF)
            {
                return Malformed();
            }

            var marker = d[pos + 1];
            if (marker == 0xFF)
            {
                pos++; // fill byte
                continue;
            }

            if (marker == 0xD9)
            {
                if (pos + 2 != d.Length || !frameSeen || scans == 0)
                {
                    return pos + 2 != d.Length ? Error.Validation("trailing_data", "The file contains data after the end of the image and was rejected.") : Malformed();
                }

                output.Write([0xFF, 0xD9]);
                // SOI, then a fresh minimal JFIF APP0 (no thumbnail, 1:1 aspect) unless an Adobe APP14 marker defines the colour
                // transform; PDF writers/readers rely on one of the two to identify the JPEG.
                byte[] header = adobe ? [0xFF, 0xD8] : [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];
                return new ImageInfo("image/jpeg", width, height, [.. header, .. output.ToArray()]);
            }

            if (marker is 0xD8 or (>= 0xD0 and <= 0xD7) or 0x01 || pos + 4 > d.Length)
            {
                return Malformed();
            }

            var len = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 2)..]);
            if (len < 2 || pos + 2 + len > d.Length)
            {
                return Malformed();
            }

            var segment = d.Slice(pos, 2 + len);
            if (marker is 0xC0 or 0xC2)
            {
                // SOF0 (baseline) or SOF2 (progressive), Huffman only; exactly one frame.
                if (frameSeen || len < 8)
                {
                    return Malformed();
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 7)..]);
                if (d[pos + 9] is not (1 or 3))
                {
                    return Error.Validation("unsupported_jpeg", "Only grayscale or RGB JPEGs are supported (not CMYK).");
                }

                frameSeen = true;
            }
            else if (marker is (>= 0xC1 and <= 0xCF) and not (0xC4 or 0xC8 or 0xCC))
            {
                return Error.Validation("unsupported_jpeg", "Only baseline or progressive JPEGs are supported.");
            }

            // Keep only what decoding needs: tables (DQT, DHT, DRI), the frame and scans, and a standard Adobe APP14 colour-transform
            // marker. APP0-APP13/APP15 (JFIF thumbnails, EXIF/GPS, XMP, ICC) and COM comments are dropped.
            var keep = marker is 0xC0 or 0xC2 or 0xC4 or 0xDB or 0xDD or 0xDA || (marker == 0xEE && len == 14);
            if (keep)
            {
                output.Write(segment);
                adobe |= marker == 0xEE;
            }

            pos += 2 + len;
            if (marker == 0xDA)
            {
                if (!frameSeen || ++scans > MaxJpegScans)
                {
                    return Malformed();
                }

                // Entropy-coded data runs until a marker other than stuffed 0xFF00 or a restart marker.
                var scanStart = pos;
                while (pos + 1 < d.Length && !(d[pos] == 0xFF && d[pos + 1] != 0x00 && d[pos + 1] is not (>= 0xD0 and <= 0xD7)))
                {
                    pos++;
                }

                output.Write(d[scanStart..pos]);
            }
        }

        return Malformed();
    }

    private static Error Malformed() => Error.Validation("malformed_image", "The image file is damaged or not a valid PNG/JPEG.");

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
