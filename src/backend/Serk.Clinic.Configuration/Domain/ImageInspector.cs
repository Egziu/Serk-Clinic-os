using System.Buffers.Binary;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Domain;

public sealed record ImageInfo(string ContentType, int Width, int Height);

/// <summary>
/// Strict structural validation of uploaded branding images (logo, stamp, signature). Only baseline/progressive JPEG and
/// 8-bit RGB/RGBA non-interlaced PNG are accepted. The file must be exactly one well-formed image: magic bytes, chunk/segment
/// structure, PNG CRCs, and no trailing bytes after IEND/EOI (defeats polyglot files that are also HTML/ZIP/scripts).
/// SVG, GIF, WebP, BMP and anything else are rejected. Images are served back only as their sniffed type with nosniff.
/// </summary>
public static class ImageInspector
{
    public const int MaxBytes = 512 * 1024;
    public const int MinDimension = 16;
    public const int MaxDimension = 4096;

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

        return result;
    }

    private static Result<ImageInfo> InspectPng(ReadOnlySpan<byte> d)
    {
        var pos = 8;
        int width = 0, height = 0;
        var first = true;
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
                byte bitDepth = body[8], colorType = body[9], interlace = body[12];
                if (bitDepth != 8 || colorType is not (2 or 6) || interlace != 0)
                {
                    return Error.Validation("unsupported_png", "Save the PNG as 24-bit RGB or 32-bit RGBA, non-interlaced (palette and grayscale PNGs are not supported).");
                }

                first = false;
            }

            pos += 12 + (int)length;
            if (type.SequenceEqual("IEND"u8))
            {
                return pos == d.Length ? new ImageInfo("image/png", width, height) : Error.Validation("trailing_data", "The file contains data after the end of the image and was rejected.");
            }
        }

        return Malformed();
    }

    private static Result<ImageInfo> InspectJpeg(ReadOnlySpan<byte> d)
    {
        if (d[^2] != 0xFF || d[^1] != 0xD9)
        {
            return Error.Validation("trailing_data", "The JPEG does not end with an end-of-image marker (possible appended data).");
        }

        var pos = 2;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF)
            {
                return Malformed();
            }

            var marker = d[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }

            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7) or 0x01)
            {
                return Malformed();
            }

            var len = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 2)..]);
            if (len < 2 || pos + 2 + len > d.Length)
            {
                return Malformed();
            }

            // SOF0 (baseline) or SOF2 (progressive), Huffman only.
            if (marker is 0xC0 or 0xC2)
            {
                if (len < 8)
                {
                    return Malformed();
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(d[(pos + 7)..]);
                var components = d[pos + 9];
                return components is 1 or 3
                    ? new ImageInfo("image/jpeg", width, height)
                    : Error.Validation("unsupported_jpeg", "Only grayscale or RGB JPEGs are supported (not CMYK).");
            }

            if (marker is (>= 0xC1 and <= 0xCF) and not (0xC4 or 0xC8 or 0xCC))
            {
                return Error.Validation("unsupported_jpeg", "Only baseline or progressive JPEGs are supported.");
            }

            if (marker == 0xDA)
            {
                return Malformed(); // scan before frame header
            }

            pos += 2 + len;
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
