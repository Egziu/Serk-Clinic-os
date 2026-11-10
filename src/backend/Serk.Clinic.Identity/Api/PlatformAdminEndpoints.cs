using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Identity.Application;
using Serk.Clinic.Platform.Http;

namespace Serk.Clinic.Identity.Api;

/// <summary>
/// System super-administration API (<c>/api/v1/platform/*</c>). Every route requires an active platform super administrator;
/// none of them returns patient data. Changes are written to the append-only admin change log.
/// </summary>
public static class PlatformAdminEndpoints
{
    public static void MapPlatformAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/platform").WithTags("System administration").RequirePlatformAdmin();

        p.MapGet("/overview", async (PlatformAdminService s, CancellationToken ct) => Results.Ok(await s.OverviewAsync(ct)));
        p.MapGet("/catalog", () => Results.Ok(PlatformAdminService.Catalog()));

        p.MapGet("/organisations", async (PlatformAdminService s, CancellationToken ct) => Results.Ok(await s.ListTenantsAsync(ct)));
        p.MapPost("/organisations", async (CreateTenantRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.CreateTenantAsync(body, ct)).ToHttp(t => Results.Created($"/api/v1/platform/organisations/{t.Id}", t)));
        p.MapPut("/organisations/{id:guid}", async (Guid id, RenameTenantRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.RenameTenantAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        p.MapPost("/facilities", async (FacilityRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.CreateFacilityAsync(body, ct)).ToHttp(f => Results.Created($"/api/v1/platform/facilities/{f.Id}", f)));
        p.MapPut("/facilities/{id:guid}", async (Guid id, FacilityRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.UpdateFacilityAsync(id, body, ct)).ToHttp(Results.Ok));

        p.MapGet("/staff", async (string? q, Guid? organisationId, PlatformAdminService s, CancellationToken ct) => Results.Ok(await s.ListStaffAsync(q, organisationId, ct)));
        p.MapPost("/staff", async (CreateStaffRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.CreateStaffAsync(body, ct)).ToHttp(u => Results.Created($"/api/v1/platform/staff/{u.Summary.Id}", u)));
        p.MapGet("/staff/{id:guid}", async (Guid id, PlatformAdminService s, CancellationToken ct) => (await s.GetStaffAsync(id, ct)).ToHttp(Results.Ok));
        p.MapPut("/staff/{id:guid}", async (Guid id, UpdateStaffRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.UpdateStaffAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        p.MapPost("/staff/{id:guid}/active", async (Guid id, SetActiveRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.SetActiveAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        p.MapPost("/staff/{id:guid}/unlock", async (Guid id, PlatformAdminService s, CancellationToken ct) =>
            (await s.UnlockAsync(id, ct)).ToHttp(_ => Results.NoContent()));
        p.MapPost("/staff/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.ResetPasswordAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        p.MapPost("/staff/{id:guid}/revoke-sessions", async (Guid id, PlatformAdminService s, CancellationToken ct) =>
            (await s.RevokeSessionsAsync(id, ct)).ToHttp(n => Results.Ok(new { revoked = n })));
        p.MapPut("/staff/{id:guid}/practitioner", async (Guid id, PractitionerProfileRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.UpsertPractitionerAsync(id, body, ct)).ToHttp(Results.Ok));
        p.MapPost("/staff/{id:guid}/assignments", async (Guid id, AssignmentRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.CreateAssignmentAsync(id, body, ct)).ToHttp(a => Results.Created($"/api/v1/platform/assignments/{a.Id}", a)));
        p.MapPut("/assignments/{id:guid}", async (Guid id, AssignmentRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.UpdateAssignmentAsync(id, body, ct)).ToHttp(Results.Ok));
        p.MapPost("/access-preview", async (PreviewRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.PreviewAsync(body, ct)).ToHttp(Results.Ok));

        p.MapGet("/organisations/{id:guid}/roles", async (Guid id, PlatformAdminService s, CancellationToken ct) => Results.Ok(await s.ListRolesAsync(id, ct)));
        p.MapPost("/roles", async (CreateRoleRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.CreateRoleAsync(body, ct)).ToHttp(r => Results.Created($"/api/v1/platform/roles/{r.Id}", r)));
        p.MapPut("/roles/{id:guid}", async (Guid id, UpdateRoleRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.UpdateRoleAsync(id, body, ct)).ToHttp(Results.Ok));

        p.MapGet("/super-admins", async (PlatformAdminService s, CancellationToken ct) => Results.Ok(await s.ListPlatformAdminsAsync(ct)));
        p.MapPost("/super-admins/grant", async (PlatformAdminRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.GrantPlatformAdminAsync(body, ct)).ToHttp(_ => Results.NoContent()));
        p.MapPost("/super-admins/revoke", async (PlatformAdminRequest body, PlatformAdminService s, CancellationToken ct) =>
            (await s.RevokePlatformAdminAsync(body, ct)).ToHttp(_ => Results.NoContent()));

        p.MapGet("/changes", async (Guid? targetId, DateTimeOffset? before, int? limit, PlatformAdminService s, CancellationToken ct) =>
            Results.Ok(await s.ChangesAsync(targetId, before, limit ?? 50, ct)));
    }
}
