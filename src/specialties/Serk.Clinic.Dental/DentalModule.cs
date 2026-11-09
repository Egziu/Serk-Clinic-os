using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Dental.Application;

namespace Serk.Clinic.Dental;

public static class DentalModule
{
    public static IServiceCollection AddDentalModule(this IServiceCollection services) => services.AddScoped<DentalChartService>();
}
