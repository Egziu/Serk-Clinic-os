using Microsoft.AspNetCore.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Http;

/// <summary>Maps application results to HTTP responses using RFC 9457 problem details (no PHI, no stack traces).</summary>
public static class ResultHttp
{
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error!);

    public static IResult Problem(Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            ErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError,
        };

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = error.Code };
        if (error.Fields is { Count: > 0 })
        {
            return Results.ValidationProblem(
                error.Fields.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                detail: error.Message,
                statusCode: status,
                extensions: extensions);
        }

        return Results.Problem(detail: error.Message, statusCode: status, title: Title(status), extensions: extensions);
    }

    private static string Title(int status) => status switch
    {
        400 => "Invalid request",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        412 => "Precondition failed",
        422 => "Business rule violated",
        _ => "Error",
    };

    /// <summary>Parses a weak/strong ETag of the form "v{n}" or "{n}" from If-Match.</summary>
    public static int? ParseIfMatchVersion(HttpRequest request)
    {
        var raw = request.Headers.IfMatch.ToString().Trim();
        if (raw.StartsWith("W/", StringComparison.Ordinal))
        {
            raw = raw[2..];
        }

        raw = raw.Trim('"');
        if (raw.StartsWith('v'))
        {
            raw = raw[1..];
        }

        return int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static string ETag(int version) => $"\"v{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";
}
