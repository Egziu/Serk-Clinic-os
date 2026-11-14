using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Backup;

public static class BackupEndpoints
{
    private sealed class SizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => bytes;
    }

    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/backups").WithTags("Backups");

        g.MapGet("", async (BackupService s, CancellationToken ct) => Results.Ok(await s.HistoryAsync(ct)))
            .RequireCapability(Capabilities.BackupManage);

        g.MapPost("", async (CreateBackupRequest body, BackupService s, HttpResponse response, CancellationToken ct) =>
            (await s.CreateAsync(body, ct)).ToHttp(f =>
            {
                response.Headers.CacheControl = "no-store";
                response.Headers["X-Serk-Backup-Sha256"] = f.Sha256;
                response.Headers["X-Serk-Backup-Rows"] = f.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.File(f.Bytes, "application/octet-stream", f.FileName);
            }))
            .RequireCapability(Capabilities.BackupManage)
            .RequireRateLimiting("data-heavy");

        // Multipart: the backup file plus its passphrase. Nothing is stored; the result is a report.
        g.MapPost(BackupService.VerifyPath["/api/v1/backups".Length..], async (HttpRequest request, BackupService s, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return ResultHttp.Problem(Error.Validation("file_required", "Send the backup file as multipart form data."));
            }

            var form = await new Microsoft.AspNetCore.Http.Features.FormFeature(request, new Microsoft.AspNetCore.Http.Features.FormOptions
            {
                MultipartBodyLengthLimit = BackupService.MaxUploadBytes,
            }).ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            var passphrase = form["passphrase"].ToString();
            if (file is null || file.Length == 0 || file.Length > BackupService.MaxUploadBytes)
            {
                return ResultHttp.Problem(Error.Validation("file_required", $"Choose a .serkbak backup file (up to {BackupService.MaxUploadBytes / (1024 * 1024)} MB)."));
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return (await s.VerifyAsync(ms.ToArray(), passphrase, ct)).ToHttp(Results.Ok);
        })
            .DisableAntiforgery()
            .WithMetadata(new SizeLimit(BackupService.MaxUploadBytes))
            .RequireCapability(Capabilities.BackupManage)
            .RequireRateLimiting("data-heavy");
    }
}
