using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Configuration.Application;
using Serk.Clinic.Configuration.Domain;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Configuration.Api;

public static class ConfigurationEndpoints
{
    public static void MapConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/facility").WithTags("Facility configuration");

        // Any member of the facility may read theme/branding metadata (needed to render the UI).
        g.MapGet("/settings", async (ConfigurationService service, HttpResponse response, CancellationToken ct) =>
            (await service.GetSettingsAsync(ct)).ToHttp(s =>
            {
                response.Headers.ETag = ResultHttp.ETag(s.Version);
                return Results.Ok(s);
            }))
            .RequireCapability();

        g.MapPut("/settings", async (UpdateSettingsRequest body, HttpRequest http, HttpResponse response, ConfigurationService service, CancellationToken ct) =>
            (await service.UpdateSettingsAsync(body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(s =>
            {
                response.Headers.ETag = ResultHttp.ETag(s.Version);
                return Results.Ok(s);
            }))
            .RequireCapability(Capabilities.BrandingManage);

        g.MapPost("/branding/{kind}", async (string kind, IFormFile? file, ConfigurationService service, CancellationToken ct) =>
        {
            if (!Enum.TryParse<BrandingAssetKind>(kind, ignoreCase: true, out var k) || k == BrandingAssetKind.Signature)
            {
                return ResultHttp.Problem(Error.Validation("invalid_kind", "Use 'logo' or 'stamp'."));
            }

            return (await UploadAsync(file, k, service, ct)).ToHttp(a => Results.Created($"/api/v1/branding-assets/{a.Id}/content", a));
        })
            .RequireCapability(Capabilities.BrandingManage)
            .DisableAntiforgery(); // CSRF enforced by the session CSRF header middleware

        g.MapGet("/printers", async (ConfigurationService service, CancellationToken ct) => (await service.ListPrintersAsync(ct)).ToHttp(Results.Ok))
            .RequireCapability();
        g.MapPost("/printers", async (PrinterProfileRequest body, ConfigurationService service, CancellationToken ct) =>
            (await service.CreatePrinterAsync(body, ct)).ToHttp(p => Results.Created($"/api/v1/facility/printers/{p.Id}", p)))
            .RequireCapability(Capabilities.PrintersManage);
        g.MapPost("/printers/{id:guid}/deactivate", async (Guid id, ConfigurationService service, CancellationToken ct) =>
            (await service.DeactivatePrinterAsync(id, ct)).ToHttp(_ => Results.NoContent()))
            .RequireCapability(Capabilities.PrintersManage);

        app.MapGet("/api/v1/me/signature", async (ConfigurationService service, CancellationToken ct) =>
            (await service.GetMySignatureAsync(ct)).ToHttp(a => a is null ? Results.NoContent() : Results.Ok(a)))
            .WithTags("Facility configuration").RequireCapability(Capabilities.NoteSign);
        app.MapPost("/api/v1/me/signature", async (IFormFile? file, ConfigurationService service, CancellationToken ct) =>
            (await UploadAsync(file, BrandingAssetKind.Signature, service, ct)).ToHttp(a => Results.Created($"/api/v1/branding-assets/{a.Id}/content", a)))
            .WithTags("Facility configuration").RequireCapability(Capabilities.NoteSign).DisableAntiforgery();

        app.MapGet("/api/v1/branding-assets/{id:guid}/content", async (Guid id, ConfigurationService service, HttpResponse response, CancellationToken ct) =>
            (await service.GetContentAsync(id, ct)).ToHttp(c =>
            {
                response.Headers.CacheControl = "private, max-age=300";
                response.Headers.ETag = "\"" + c.Sha256 + "\"";
                response.Headers.ContentDisposition = "inline";
                return Results.Bytes(c.Bytes, c.ContentType);
            }))
            .WithTags("Facility configuration").RequireCapability();
    }

    private static async Task<Result<AssetDto>> UploadAsync(IFormFile? file, BrandingAssetKind kind, ConfigurationService service, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Error.Validation("file_required", "Attach an image file in the 'file' form field.");
        }

        if (file.Length > ImageInspector.MaxBytes)
        {
            return Error.Validation("file_too_large", $"Images must be at most {ImageInspector.MaxBytes / 1024} KB.");
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await service.UploadAsync(kind, ms.ToArray(), ct);
    }
}
