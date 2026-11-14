using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Serk.Clinic.Platform.Export;

/// <summary>
/// Writes export rows as CSV (RFC 4180, UTF-8 with BOM so spreadsheet programs show Amharic/Tigrinya correctly) or JSON.
/// CSV cells that a spreadsheet would execute as a formula (starting with = + - @ tab or carriage return) are prefixed
/// with an apostrophe (CWE-1236). Numbers and dates are written invariantly.
/// </summary>
public static class TabularWriter
{
    public static byte[] Csv(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', columns.Select(Cell)).Append("\r\n");
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(v => Cell(Format(v)))).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static byte[] Json(string dataset, DateTimeOffset generatedAt, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("dataset", dataset);
            w.WriteString("generatedAt", generatedAt);
            w.WriteNumber("rowCount", rows.Count);
            w.WriteStartArray("rows");
            foreach (var row in rows)
            {
                w.WriteStartObject();
                for (var i = 0; i < columns.Count; i++)
                {
                    WriteValue(w, columns[i], i < row.Length ? row[i] : null);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.00", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string[] a => string.Join(';', a),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Quotes when needed and neutralises spreadsheet formulas.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static void WriteValue(Utf8JsonWriter w, string name, object? v)
    {
        switch (v)
        {
            case null: w.WriteNull(name); break;
            case decimal m: w.WriteNumber(name, m); break;
            case int i: w.WriteNumber(name, i); break;
            case long l: w.WriteNumber(name, l); break;
            case bool b: w.WriteBoolean(name, b); break;
            case string[] a:
                w.WriteStartArray(name);
                foreach (var s in a)
                {
                    w.WriteStringValue(s);
                }

                w.WriteEndArray();
                break;
            default: w.WriteString(name, Format(v)); break;
        }
    }
}
