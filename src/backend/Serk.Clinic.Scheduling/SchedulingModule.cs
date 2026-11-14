using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Platform.Export;
using Serk.Clinic.Scheduling.Application;

namespace Serk.Clinic.Scheduling;

public static class SchedulingModule
{
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services)
    {
        services.AddScoped<AppointmentService>();
        services.AddScoped<Contracts.IAppointmentReader>(sp => sp.GetRequiredService<AppointmentService>());
        services.AddSingleton<NoShowSweeper>();
        services.AddHostedService<NoShowBackgroundService>();
        services.AddExportDatasets(SchedulingExports.Appointments, SchedulingExports.DailyVolume);
        return services;
    }
}
