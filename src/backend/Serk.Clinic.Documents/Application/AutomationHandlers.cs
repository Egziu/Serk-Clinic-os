using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Outbox;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Application;

/// <summary>
/// Automation: on <c>clinical.note-signed.v1</c>, prepare a DRAFT consultation summary so the clinician only has to review
/// the preview and finalize. It never finalizes, prints or sends anything, and a failure here never affects the signature
/// (the signature transaction committed before this message was dispatched). Each message runs in its own DI scope and
/// unit of work with a narrowly-scoped automation actor (document.create/read only) for the message's tenant and facility.
/// </summary>
public sealed class AutoPrepareSummaryHandler(IServiceScopeFactory scopes, ILogger<AutoPrepareSummaryHandler> logger) : IOutboxHandler
{
    public const string MessageType = "clinical.note-signed.v1";

    string IOutboxHandler.MessageType => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message.Payload);
        if (!doc.RootElement.TryGetProperty("facilityId", out var facilityEl) || !doc.RootElement.TryGetProperty("noteVersionId", out var noteEl))
        {
            logger.LogInformation("Outbox message {MessageId} has no facility context (pre-automation payload); skipped.", message.Id);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.CorrelationId = message.CorrelationId;
        context.Actor = new StaffActor(
            DocumentService.AutomationUserId,
            Guid.Empty,
            Guid.Empty,
            message.TenantId,
            facilityEl.GetGuid(),
            null,
            "SERK automation",
            new HashSet<string>(StringComparer.Ordinal) { Capabilities.DocumentCreate, Capabilities.DocumentRead });
        var service = scope.ServiceProvider.GetRequiredService<DocumentService>();
        var outcome = await service.PrepareAutomaticDraftAsync(noteEl.GetGuid(), ct);
        logger.LogInformation("Auto-prepare for outbox message {MessageId}: {Outcome}.", message.Id, outcome);
    }
}
