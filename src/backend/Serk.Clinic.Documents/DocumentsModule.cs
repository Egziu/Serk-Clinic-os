using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Documents.Application;

namespace Serk.Clinic.Documents;

public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services)
    {
        services.AddScoped<DocumentService>();
        services.AddScoped<Platform.Outbox.IOutboxHandler, AutoPrepareSummaryHandler>();
        return services;
    }
}
