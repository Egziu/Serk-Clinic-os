using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Configuration.Application;
using Serk.Clinic.Configuration.Contracts;

namespace Serk.Clinic.Configuration;

public static class ConfigurationModule
{
    public static IServiceCollection AddConfigurationModule(this IServiceCollection services)
    {
        services.AddScoped<ConfigurationService>();
        services.AddScoped<IBrandingReader>(sp => sp.GetRequiredService<ConfigurationService>());
        return services;
    }
}
