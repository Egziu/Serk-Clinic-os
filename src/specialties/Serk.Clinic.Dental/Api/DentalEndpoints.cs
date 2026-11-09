using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Dental.Application;
using Serk.Clinic.Dental.Domain;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Dental.Api;

public static class DentalEndpoints
{
    public static void MapDentalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/encounters/{encounterId:guid}/dental").WithTags("Dental");

        g.MapGet("/chart", async (Guid encounterId, DentalChartService service, CancellationToken ct) =>
            (await service.GetChartAsync(encounterId, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.DentalChartRead);

        g.MapGet("/teeth/{toothCode:int}/history", async (Guid encounterId, int toothCode, DentalChartService service, CancellationToken ct) =>
            (await service.GetToothHistoryAsync(encounterId, toothCode, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.DentalChartRead);

        g.MapPost("/findings", async (Guid encounterId, RecordFindingInput body, DentalChartService service, CancellationToken ct) =>
            (await service.RecordAsync(encounterId, body, ct)).ToHttp(f => Results.Created($"/api/v1/encounters/{encounterId}/dental/chart", f)))
            .RequireCapability(Capabilities.DentalChartRead, Capabilities.DentalChartWrite);
    }
}
