using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Scheduling.Application;

namespace Serk.Clinic.Scheduling;

public static class SchedulingModule
{
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services) => services.AddScoped<AppointmentService>();
}
