using Microsoft.AspNetCore.Http;

namespace Serk.Clinic.Platform.Http;

public static class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    public static string? From(HttpRequest request) =>
        request.Headers.TryGetValue(HeaderName, out var v) ? v.ToString() : null;
}
