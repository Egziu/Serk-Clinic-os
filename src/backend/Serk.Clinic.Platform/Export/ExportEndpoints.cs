using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Export;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/exports").WithTags("Data exports");

        // The catalogue only lists what the caller may actually run.
        g.MapGet("", (ExportService s) => Results.Ok(s.Catalogue())).RequireCapability();

        g.MapGet("/history", async (int? limit, ExportService s, CancellationToken ct) => Results.Ok(await s.HistoryAsync(limit ?? 50, ct)))
            .RequireCapability(Capabilities.AuditRead);

        // POST (not GET): the request may carry a password and purpose, and must never be cached or prefetched.
        g.MapPost("/{code}", async (string code, ExportRequest body, ExportService s, HttpResponse response, CancellationToken ct) =>
            (await s.RunAsync(code, body, ct)).ToHttp(f =>
            {
                response.Headers.CacheControl = "no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["X-Serk-Export-Sha256"] = f.Sha256;
                response.Headers["X-Serk-Export-Rows"] = f.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.File(f.Bytes, f.ContentType, f.FileName);
            }))
            .RequireCapability()
            .RequireRateLimiting("data-heavy");
    }
}
