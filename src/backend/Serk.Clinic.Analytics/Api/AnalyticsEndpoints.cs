using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Analytics.Application;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Analytics.Api;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/analytics").WithTags("Analytics");

        g.MapGet("/overview", async (DateOnly? from, DateOnly? to, AnalyticsService service, HttpResponse response, CancellationToken ct) =>
            (await service.OverviewAsync(from, to, ct)).ToHttp(o =>
            {
                response.Headers["Server-Timing"] = "analytics;dur=" + o.ComputeMilliseconds.ToString(CultureInfo.InvariantCulture);
                return Results.Ok(o);
            }))
            .RequireCapability(Capabilities.AnalyticsRead);

        g.MapGet("/daily.csv", async (DateOnly? from, DateOnly? to, AnalyticsService service, HttpResponse response, CancellationToken ct) =>
            (await service.DailyCsvAsync(from, to, ct)).ToHttp(csv =>
            {
                response.Headers.ContentDisposition = "attachment; filename=\"serk-daily-activity.csv\"";
                return Results.Text(csv, "text/csv; charset=utf-8");
            }))
            .RequireCapability(Capabilities.AnalyticsRead);
    }
}
