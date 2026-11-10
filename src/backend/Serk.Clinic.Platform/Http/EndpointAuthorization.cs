using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Http;

/// <summary>
/// Capability gate for endpoints. Requires an authenticated session with an active facility affiliation, then checks
/// that the affiliation grants every listed capability. Denials are recorded as security events. Object-level (ABAC)
/// checks — same facility, treatment relationship, attending clinician — remain the application service's job.
/// </summary>
public static class EndpointAuthorization
{
    public static TBuilder RequireCapability<TBuilder>(this TBuilder builder, params string[] capabilities)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var services = ctx.HttpContext.RequestServices;
            var request = services.GetRequiredService<RequestContext>();
            if (request.Actor is null)
            {
                return ResultHttp.Problem(Error.Forbidden("facility_not_selected", "Select an active facility affiliation first."));
            }

            var missing = capabilities.FirstOrDefault(c => !request.Actor.Has(c));
            if (missing is not null)
            {
                var audit = services.GetRequiredService<AuditTrail>();
                await audit.RecordSecurityEventAsync("authorization.denied", "denied:" + missing, ct: ctx.HttpContext.RequestAborted);
                return ResultHttp.Problem(Error.Forbidden("capability_required", "Your role does not permit this action."));
            }

            return await next(ctx);
        });
        return builder;
    }

    /// <summary>
    /// Gate for platform super-administration endpoints. Requires an authenticated, active platform administrator; tenant
    /// affiliation is not required and not consulted. Denials are recorded as security events.
    /// </summary>
    public static TBuilder RequirePlatformAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var services = ctx.HttpContext.RequestServices;
            var request = services.GetRequiredService<RequestContext>();
            if (request.PlatformAdminUserId is null)
            {
                var audit = services.GetRequiredService<AuditTrail>();
                await audit.RecordSecurityEventAsync("authorization.denied", "denied:platform.admin", ct: ctx.HttpContext.RequestAborted);
                return ResultHttp.Problem(Error.Forbidden("platform_admin_required", "Only a system super administrator can do this."));
            }

            return await next(ctx);
        });
        return builder;
    }
}
