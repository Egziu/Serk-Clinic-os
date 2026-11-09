using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Documents.Application;
using Serk.Clinic.Documents.Rendering;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Api;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/documents").WithTags("Documents");

        g.MapPost("/", async (CreateDocumentRequest body, DocumentService service, CancellationToken ct) =>
            (await service.CreateAsync(body, ct)).ToHttp(d => Results.Created($"/api/v1/documents/{d.Id}", d)))
            .RequireCapability(Capabilities.DocumentCreate);

        g.MapGet("/", async (Guid encounterId, DocumentService service, CancellationToken ct) =>
            (await service.ListForEncounterAsync(encounterId, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.DocumentRead);

        g.MapGet("/{id:guid}/preview", async (Guid id, DocumentService service, HttpResponse response, CancellationToken ct) =>
            (await service.PreviewAsync(id, ct)).ToHttp(pdf => Pdf(response, pdf, inline: true)))
            .RequireCapability(Capabilities.DocumentRead);

        g.MapPost("/{id:guid}/finalize", async (Guid id, HttpRequest http, DocumentService service, CancellationToken ct) =>
            (await service.FinalizeAsync(id, IdempotencyKey.From(http), ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.DocumentFinalize);

        g.MapGet("/{id:guid}/download", async (Guid id, DocumentService service, HttpResponse response, CancellationToken ct) =>
            (await service.DownloadAsync(id, ct)).ToHttp(pdf => Pdf(response, pdf, inline: true)))
            .RequireCapability(Capabilities.DocumentRead);

        g.MapPost("/{id:guid}/print-events", async (Guid id, PrintEventRequest body, DocumentService service, CancellationToken ct) =>
            (await service.RecordPrintEventAsync(id, body, ct)).ToHttp(_ => Results.NoContent()))
            .RequireCapability(Capabilities.DocumentRead);

        app.MapGet("/api/v1/appointments/{id:guid}/slip", async (Guid id, SlipPaper? paper, DocumentService service, HttpResponse response, CancellationToken ct) =>
            (await service.AppointmentSlipAsync(id, paper ?? SlipPaper.Thermal80, ct)).ToHttp(pdf => Pdf(response, pdf, inline: true)))
            .WithTags("Documents")
            .RequireCapability(Capabilities.AppointmentRead);

        // Public, unauthenticated verification by opaque reference. Returns no patient data. Rate limited.
        app.MapGet("/api/v1/verify/{reference}", async (string reference, DocumentService service, CancellationToken ct) =>
            Results.Ok(await service.VerifyAsync(reference, ct)))
            .WithTags("Verification")
            .AllowAnonymous()
            .RequireRateLimiting("verify");
    }

    private static IResult Pdf(HttpResponse response, RenderedPdf pdf, bool inline)
    {
        response.Headers.CacheControl = "no-store, private";
        if (pdf.SnapshotSha256.Length > 0)
        {
            response.Headers["X-Snapshot-Sha256"] = pdf.SnapshotSha256;
            response.Headers["X-Template-Version"] = pdf.TemplateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (pdf.PdfSha256 is not null)
        {
            response.Headers["X-Pdf-Sha256"] = pdf.PdfSha256;
        }

        response.Headers.ContentDisposition = (inline ? "inline" : "attachment") + "; filename=\"" + pdf.FileName + "\"";
        return Results.Bytes(pdf.Bytes, "application/pdf");
    }
}
