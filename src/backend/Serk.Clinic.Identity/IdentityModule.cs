using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serk.Clinic.Identity.Api;
using Serk.Clinic.Identity.Application;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Identity.Infrastructure;

namespace Serk.Clinic.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddDbContext<IdentityDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<AuthService>();
        services.AddScoped<IStaffDirectory, StaffDirectory>();
        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();
        return services;
    }
}
