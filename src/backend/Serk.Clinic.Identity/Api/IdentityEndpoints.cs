using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Identity.Application;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Api;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record SelectAffiliationRequest(Guid AffiliationId);

public sealed record PractitionerDto(Guid Id, string DisplayName, string ProfessionalTitle, string Specialty);

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");

        auth.MapPost("/login", async (LoginRequest body, AuthService service, HttpContext http, CancellationToken ct) =>
        {
            var result = await service.LoginAsync(body.Username, body.Password, ct);
            return result.ToHttp(outcome =>
            {
                http.Response.Cookies.Append(SessionPolicy.CookieName, outcome.SessionToken, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    IsEssential = true,
                });
                return Results.Ok(outcome.Session);
            });
        }).RequireRateLimiting("login");

        auth.MapGet("/session", async (HttpContext http, AuthService service, CancellationToken ct) =>
            http.Items[SessionAuthenticationHandler.SessionItemKey] is ValidatedSession s
                ? Results.Ok(await service.DescribeAsync(s, ct))
                : Results.Unauthorized()).RequireAuthorization();

        auth.MapPost("/session/affiliation", async (SelectAffiliationRequest body, HttpContext http, AuthService service, CancellationToken ct) =>
        {
            var session = (ValidatedSession)http.Items[SessionAuthenticationHandler.SessionItemKey]!;
            var result = await service.SelectAffiliationAsync(session, body.AffiliationId, ct);
            return result.ToHttp(Results.Ok);
        }).RequireAuthorization();

        auth.MapPost("/logout", async (HttpContext http, AuthService service, CancellationToken ct) =>
        {
            var session = (ValidatedSession)http.Items[SessionAuthenticationHandler.SessionItemKey]!;
            await service.RevokeAsync(session.SessionId, "logout", ct);
            http.Response.Cookies.Delete(SessionPolicy.CookieName, new CookieOptions { Secure = true, Path = "/", SameSite = SameSiteMode.Strict });
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/v1/admin/staff/{userId:guid}/revoke-sessions", async (Guid userId, AuthService service, RequestContext context, CancellationToken ct) =>
        {
            var result = await service.RevokeAllForUserAsync(context.RequireActor(), userId, ct);
            return result.ToHttp(count => Results.Ok(new { revoked = count }));
        }).WithTags("Administration").RequireCapability(Capabilities.StaffAdmin);

        app.MapGet("/api/v1/practitioners", async (IStaffDirectory directory, RequestContext context, CancellationToken ct) =>
        {
            var actor = context.RequireActor();
            var list = await directory.ListFacilityPractitionersAsync(actor.TenantId, actor.FacilityId, ct);
            return Results.Ok(list.Select(p => new PractitionerDto(p.Id, p.DisplayName, p.ProfessionalTitle, p.Specialty)));
        }).WithTags("Directory").RequireCapability(Capabilities.AppointmentRead);
    }
}
