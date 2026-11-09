using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Clinical.Application;
using Serk.Clinic.Clinical.Contracts;
using Serk.Clinic.Platform.Outbox;

namespace Serk.Clinic.Clinical;

public static class ClinicalModule
{
    public static IServiceCollection AddClinicalModule(this IServiceCollection services)
    {
        services.AddScoped<EncounterService>();
        services.AddScoped<IEncounterLauncher>(sp => sp.GetRequiredService<EncounterService>());
        services.AddScoped<ISignedNoteReader>(sp => sp.GetRequiredService<EncounterService>());
        services.AddScoped<IEncounterAccess>(sp => sp.GetRequiredService<EncounterService>());
        services.AddScoped<IOutboxHandler, NoteSignedHandler>();
        return services;
    }
}

/// <summary>
/// Consumer of note-signed events. Today it only records operational telemetry (no PHI); it is the integration point
/// for downstream work (notifications, FHIR export) that must never block or falsify the clinical signature.
/// </summary>
internal sealed class NoteSignedHandler(Microsoft.Extensions.Logging.ILogger<NoteSignedHandler> logger) : IOutboxHandler
{
    public string MessageType => EncounterService.NoteSignedMessage;

    public Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Processed note-signed event {MessageId}.", message.Id);
        return Task.CompletedTask;
    }
}
