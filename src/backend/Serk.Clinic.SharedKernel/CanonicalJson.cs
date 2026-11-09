using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serk.Clinic.SharedKernel;

/// <summary>
/// Deterministic JSON serialization used for immutable clinical snapshots and their SHA-256 hashes.
/// Property order follows declaration order of the typed record; numbers and dates use invariant formats.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("Snapshot JSON was null.");

    public static string Sha256Hex(string canonicalJson) => Sha256Hex(Encoding.UTF8.GetBytes(canonicalJson));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
