using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Patients.Application;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Patients.Api;

public static class PatientEndpoints
{
    public static void MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/patients").WithTags("Patients");

        g.MapGet("/", async (string? query, PatientService service, CancellationToken ct) =>
            (await service.SearchAsync(query, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.PatientSearch);

        g.MapPost("/", async (RegisterPatientRequest body, HttpRequest http, PatientService service, CancellationToken ct) =>
            (await service.RegisterAsync(body, IdempotencyKey.From(http), ct)).ToHttp(r => r.Outcome == "Created"
                ? Results.Created($"/api/v1/patients/{r.Patient!.Id}", r)
                : Results.Conflict(r)))
            .RequireCapability(Capabilities.PatientRegister);

        g.MapGet("/{id:guid}", async (Guid id, PatientService service, HttpResponse response, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(p =>
            {
                response.Headers.ETag = ResultHttp.ETag(p.Version);
                return Results.Ok(p);
            }))
            .RequireCapability(Capabilities.PatientReadDemographics);

        g.MapPost("/{id:guid}/allergies", async (Guid id, AddAllergyRequest body, PatientService service, CancellationToken ct) =>
            (await service.AddAllergyAsync(id, body, ct)).ToHttp(a => Results.Created($"/api/v1/patients/{id}", a)))
            .RequireCapability(Capabilities.AllergyWrite);

        g.MapPost("/{id:guid}/allergies/{allergyId:guid}/status", async (Guid id, Guid allergyId, ChangeAllergyStatusRequest body, PatientService service, CancellationToken ct) =>
            (await service.ChangeAllergyStatusAsync(id, allergyId, body, ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.AllergyWrite);

        g.MapPost("/{id:guid}/allergies/no-known-allergies", async (Guid id, HttpRequest http, PatientService service, CancellationToken ct) =>
            (await service.RecordNoKnownAllergiesAsync(id, ResultHttp.ParseIfMatchVersion(http), ct)).ToHttp(Results.Ok))
            .RequireCapability(Capabilities.AllergyWrite);
    }
}
