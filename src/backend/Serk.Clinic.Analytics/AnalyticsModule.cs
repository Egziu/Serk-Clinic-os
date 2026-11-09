using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Analytics.Application;

namespace Serk.Clinic.Analytics;

public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services) => services.AddScoped<AnalyticsService>();
}
