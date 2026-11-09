using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Clinical.Application;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Clinical.Api;

public static class EncounterEndpoints
{
    public static void MapEncounterEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/encounters").WithTags("Encounters");

        g.MapGet("/", async (DateOnly? day, EncounterService service, CancellationToken ct) =>
            (await service.ListWorklistAsync(day, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.EncounterRead);

        g.MapGet("/{id:guid}", async (Guid id, EncounterService service, HttpResponse response, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(e =>
            {
                if (e.Draft is not null)
                {
                    response.Headers.ETag = ResultHttp.ETag(e.Draft.Version);
                }

                return Results.Ok(e);
            }))
            .RequireCapability(Capabilities.EncounterRead);

        g.MapPut("/{id:guid}/note-draft", async (Guid id, NoteContentV1 body, HttpRequest http, HttpResponse response, EncounterService service, CancellationToken ct) =>
            (await service.SaveDraftAsync(id, body, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(d =>
            {
                response.Headers.ETag = ResultHttp.ETag(d.Version);
                return Results.Ok(d);
            }))
            .RequireCapability(Capabilities.EncounterRead, Capabilities.NoteWrite);

        g.MapPost("/{id:guid}/sign", async (Guid id, SignRequest body, HttpRequest http, EncounterService service, CancellationToken ct) =>
            (await service.SignAsync(id, body, IdempotencyKey.From(http), ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.EncounterRead, Capabilities.NoteSign);

        g.MapPost("/{id:guid}/addenda", async (Guid id, AddendumRequest body, HttpRequest http, EncounterService service, CancellationToken ct) =>
            (await service.AddAddendumAsync(id, body, IdempotencyKey.From(http), ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.EncounterRead, Capabilities.NoteSign);
    }
}
