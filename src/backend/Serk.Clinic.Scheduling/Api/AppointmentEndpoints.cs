using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.Scheduling.Application;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Scheduling.Api;

public static class AppointmentEndpoints
{
    public static void MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/appointments").WithTags("Appointments");

        g.MapGet("/", async (DateOnly? day, AppointmentService service, CancellationToken ct) =>
            (await service.ListDayAsync(day, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.AppointmentRead);

        g.MapPost("/", async (BookAppointmentRequest body, HttpRequest http, AppointmentService service, CancellationToken ct) =>
            (await service.BookAsync(body, IdempotencyKey.From(http), ct)).ToHttp(a => Results.Created($"/api/v1/appointments/{a.Id}", a)))
            .RequireCapability(Capabilities.AppointmentBook);

        g.MapPost("/{id:guid}/check-in", async (Guid id, HttpRequest http, AppointmentService service, CancellationToken ct) =>
            (await service.CheckInAsync(id, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.AppointmentCheckIn);

        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpRequest http, AppointmentService service, CancellationToken ct) =>
            (await service.CancelAsync(id, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.AppointmentBook);
    }
}
