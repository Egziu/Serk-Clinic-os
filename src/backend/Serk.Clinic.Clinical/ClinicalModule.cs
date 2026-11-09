using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Clinical.Application;
using Serk.Clinic.Clinical.Contracts;

namespace Serk.Clinic.Clinical;

public static class ClinicalModule
{
    public static IServiceCollection AddClinicalModule(this IServiceCollection services)
    {
        services.AddScoped<EncounterService>();
        services.AddScoped<IEncounterLauncher>(sp => sp.GetRequiredService<EncounterService>());
        services.AddScoped<ISignedNoteReader>(sp => sp.GetRequiredService<EncounterService>());
        services.AddScoped<IEncounterAccess>(sp => sp.GetRequiredService<EncounterService>());
        return services;
    }
}
