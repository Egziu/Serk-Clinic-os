using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serk.Clinic.Identity.Application;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Api;

/// <summary>Organisation-scoped staff and role administration (same shapes as /api/v1/platform, confined to one organisation).</summary>
public static class OrganisationAdminEndpoints
{
    public static void MapOrganisationAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var o = app.MapGroup("/api/v1/organisation/admin").WithTags("Organisation administration").RequireCapability(Capabilities.StaffManage);
        o.MapGet("/catalog", () => Results.Ok(PlatformAdminService.Catalog()));
        o.MapGet("/organisations", async (OrganisationAdminService s, CancellationToken ct) => Results.Ok(await s.OrganisationsAsync(ct)));
        o.MapGet("/staff", async (string? q, OrganisationAdminService s, CancellationToken ct) => Results.Ok(await s.ListStaffAsync(q, ct)));
        o.MapPost("/staff", async (OrganisationCreateStaffRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.CreateStaffAsync(body, ct)).ToHttp(u => Results.Created($"/api/v1/organisation/admin/staff/{u.Summary.Id}", u)));
        o.MapGet("/staff/{id:guid}", async (Guid id, OrganisationAdminService s, CancellationToken ct) => (await s.GetStaffAsync(id, ct)).ToHttp(Results.Ok));
        o.MapPut("/staff/{id:guid}", async (Guid id, UpdateStaffRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.UpdateStaffAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        o.MapPost("/staff/{id:guid}/active", async (Guid id, SetActiveRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.SetActiveAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        o.MapPost("/staff/{id:guid}/unlock", async (Guid id, OrganisationAdminService s, CancellationToken ct) =>
            (await s.UnlockAsync(id, ct)).ToHttp(_ => Results.NoContent()));
        o.MapPost("/staff/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.ResetPasswordAsync(id, body, ct)).ToHttp(_ => Results.NoContent()));
        o.MapPost("/staff/{id:guid}/revoke-sessions", async (Guid id, OrganisationAdminService s, CancellationToken ct) =>
            (await s.RevokeSessionsAsync(id, ct)).ToHttp(n => Results.Ok(new { revoked = n })));
        o.MapPut("/staff/{id:guid}/practitioner", async (Guid id, PractitionerProfileRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.UpsertPractitionerAsync(id, body, ct)).ToHttp(Results.Ok));
        o.MapPost("/staff/{id:guid}/assignments", async (Guid id, AssignmentRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.CreateAssignmentAsync(id, body, ct)).ToHttp(a => Results.Created($"/api/v1/organisation/admin/assignments/{a.Id}", a)));
        o.MapPut("/assignments/{id:guid}", async (Guid id, AssignmentRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.UpdateAssignmentAsync(id, body, ct)).ToHttp(Results.Ok));
        o.MapPost("/access-preview", async (PreviewRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.PreviewAsync(body, ct)).ToHttp(Results.Ok));
        o.MapGet("/organisations/{id:guid}/roles", async (Guid id, OrganisationAdminService s, CancellationToken ct) => Results.Ok(await s.ListRolesAsync(ct)));
        o.MapPost("/roles", async (CreateRoleRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.CreateRoleAsync(body, ct)).ToHttp(r => Results.Created($"/api/v1/organisation/admin/roles/{r.Id}", r)));
        o.MapPut("/roles/{id:guid}", async (Guid id, UpdateRoleRequest body, OrganisationAdminService s, CancellationToken ct) =>
            (await s.UpdateRoleAsync(id, body, ct)).ToHttp(Results.Ok));
        o.MapGet("/changes", async (Guid? targetId, int? limit, OrganisationAdminService s, CancellationToken ct) =>
            Results.Ok(await s.ChangesAsync(targetId, limit ?? 50, ct)));
    }
}
