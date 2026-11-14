using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Finance.Application;
using Serk.Clinic.Platform.Outbox;

namespace Serk.Clinic.Finance;

public static class FinanceModule
{
    public static IServiceCollection AddFinanceModule(this IServiceCollection services)
    {
        services.AddScoped<Ledger>();
        services.AddScoped<BillingService>();
        services.AddScoped<TreasuryService>();
        services.AddScoped<CloseService>();
        services.AddScoped<OwnerService>();
        services.AddScoped<FinanceQueries>();
        services.AddScoped<Api.RequestContextAccessor>();
        services.AddSingleton<SandboxDigestChannel>();
        services.AddSingleton<IOwnerDigestChannel>(sp => sp.GetRequiredService<SandboxDigestChannel>());
        services.AddScoped<IOutboxHandler, DailyCloseLockedHandler>();
        return services;
    }
}
