using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Patients.Application;
using Serk.Clinic.Patients.Contracts;
using Serk.Clinic.Platform.Export;

namespace Serk.Clinic.Patients;

public static class PatientsModule
{
    public static IServiceCollection AddPatientsModule(this IServiceCollection services)
    {
        services.AddScoped<PatientService>();
        services.AddScoped<IPatientDirectory>(sp => sp.GetRequiredService<PatientService>());
        services.AddExportDatasets(PatientExports.Register);
        return services;
    }
}
