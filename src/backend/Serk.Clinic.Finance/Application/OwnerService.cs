using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serk.Clinic.Finance.Infrastructure;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Outbox;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Finance.Application;

/// <summary>
/// Outbound channel for owner digests. Only a SANDBOX implementation exists: nothing is sent to Telegram or e-mail. A real
/// connector needs the owner's bot token / mail domain, a security review and explicit approval (SRS v2 §8.1).
/// </summary>
public interface IOwnerDigestChannel
{
    /// <summary>Returns the provider's message id when the provider ACCEPTED the message (not proof that anyone read it).</summary>
    Task<string> SendAsync(string channel, string address, string text, CancellationToken ct);
}

public sealed class SandboxDigestChannel : IOwnerDigestChannel
{
    private readonly List<(string Channel, string Address, string Text)> _sent = [];

    /// <summary>Test hook: when set, sends fail (simulating a provider outage).</summary>
    public bool Failing { get; set; }

    public IReadOnlyList<(string Channel, string Address, string Text)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    public Task<string> SendAsync(string channel, string address, string text, CancellationToken ct)
    {
        if (Failing)
        {
            throw new HttpRequestException("Sandbox provider outage (simulated).");
        }

        lock (_sent)
        {
            _sent.Add((channel, address, text));
        }

        return Task.FromResult("sandbox-" + Guid.NewGuid().ToString("N")[..12]);
    }
}

public sealed record StartSubscriptionRequest(string? Channel);

public sealed record StartSubscriptionDto(Guid Id, string Channel, string ChallengeCode, DateTimeOffset ExpiresAt, string Instructions);

public sealed record VerifySubscriptionCallback(Guid TenantId, string? ChallengeCode, string? Address, string? ChatType);

public sealed record SubscriptionDto(Guid Id, string Channel, string? MaskedAddress, string Status, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt);

public sealed record DeliveryDto(Guid Id, Guid CloseId, string CloseSerial, DateOnly BusinessDate, string Channel, string Status, int Attempts, string? ProviderMessageId,
    string? LastError, DateTimeOffset UpdatedAt);

/// <summary>
/// Owner digest recipients and deliveries. A recipient address is only ever set by a verified one-time challenge started
/// by the signed-in owner; group chats are refused. Delivery failures never affect the locked close: they are recorded,
/// retried with backoff by the outbox, and end in a visible dead-letter state an operator can retry.
/// </summary>
public sealed class OwnerService(DbSession session, RequestContext context, Ledger ledger, AuditTrail audit, IOwnerDigestChannel channel, TimeProvider clock)
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);

    public async Task<Result<StartSubscriptionDto>> StartAsync(StartSubscriptionRequest body, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (body.Channel is not ("Telegram" or "Email"))
        {
            return Ledger.Invalid("invalid_channel", "Choose Telegram or Email.", "channel");
        }

        var db = await ledger.DbAsync(ct);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var now = clock.GetUtcNow();
        var row = new OwnerSubscriptionRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            FacilityId = actor.FacilityId,
            UserId = actor.UserId,
            Channel = body.Channel,
            Status = "PendingVerification",
            ChallengeSha256 = CanonicalJson.Sha256Hex(code),
            ChallengeExpiresAt = now + ChallengeLifetime,
            CreatedAt = now,
        };
        db.Subscriptions.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("owner.subscription.start", "owner_subscription", row.Id), ct);
        await session.CommitAsync(ct);
        return new StartSubscriptionDto(row.Id, row.Channel, code, row.ChallengeExpiresAt.Value,
            body.Channel == "Telegram"
                ? "Open a PRIVATE chat with the clinic's bot and send this code within 10 minutes. Group chats are refused."
                : "Open the verification e-mail and confirm this code within 10 minutes.");
    }

    /// <summary>
    /// Called by the channel connector (authenticated by the endpoint's shared secret) when the recipient presented the
    /// challenge code from their private chat / mailbox. Binds that exact address; wrong, expired or reused codes change nothing.
    /// </summary>
    public async Task<Result<bool>> VerifyAsync(VerifySubscriptionCallback cb, CancellationToken ct)
    {
        context.SystemTenantId = cb.TenantId;
        var address = (cb.Address ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cb.ChallengeCode) || address.Length is < 3 or > 200)
        {
            return Error.Validation("invalid_verification", "Challenge code and address are required.");
        }

        var db = await ledger.DbAsync(ct);
        var hash = CanonicalJson.Sha256Hex(cb.ChallengeCode.Trim().ToUpperInvariant());
        var row = await db.Subscriptions.SingleOrDefaultAsync(s => s.TenantId == cb.TenantId && s.Status == "PendingVerification" && s.ChallengeSha256 == hash, ct);
        if (row is null || row.ChallengeExpiresAt < clock.GetUtcNow())
        {
            return Error.NotFound("challenge_not_found", "The verification code is wrong or has expired.");
        }

        if (row.Channel == "Telegram" && !string.Equals(cb.ChatType, "private", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Rule("private_chat_required", "Owner digests are only delivered to a private one-to-one chat.");
        }

        row.Address = address;
        row.Status = "Verified";
        row.VerifiedAt = clock.GetUtcNow();
        row.ChallengeSha256 = null;
        row.ChallengeExpiresAt = null;
        await db.SaveChangesAsync(ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<SubscriptionDto>> ListSubscriptionsAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var rows = await db.Subscriptions.AsNoTracking().Where(s => s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.Status != "Revoked")
            .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        return rows.Select(s => new SubscriptionDto(s.Id, s.Channel, Mask(s.Address), s.Status, s.CreatedAt, s.VerifiedAt)).ToList();
    }

    public async Task<Result<bool>> RevokeAsync(Guid id, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id && s.TenantId == actor.TenantId && s.FacilityId == actor.FacilityId && s.Status != "Revoked", ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        row.Status = "Revoked";
        row.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("owner.subscription.revoke", "owner_subscription", row.Id), ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<DeliveryDto>> ListDeliveriesAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        return await (from d in db.Deliveries.AsNoTracking()
                      join c in db.DailyCloses.AsNoTracking() on d.CloseId equals c.Id
                      where d.TenantId == actor.TenantId && c.FacilityId == actor.FacilityId
                      orderby d.CreatedAt descending
                      select new DeliveryDto(d.Id, d.CloseId, c.Serial, c.BusinessDate, d.Channel, d.Status, d.Attempts, d.ProviderMessageId, d.LastError, d.UpdatedAt)).Take(100).ToListAsync(ct);
    }

    /// <summary>Operator retry of a failed or dead-lettered delivery (audited). Never creates a second delivery row.</summary>
    public async Task<Result<DeliveryDto>> RetryAsync(Guid deliveryId, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var db = await ledger.DbAsync(ct);
        var row = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId && d.TenantId == actor.TenantId, ct);
        if (row is null)
        {
            return Error.NotFound();
        }

        if (row.Status == "ProviderAccepted")
        {
            return Error.Conflict("already_delivered", "The provider already accepted this digest; it is not sent twice.");
        }

        await AttemptAsync(db, row, ct);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry("owner.delivery.retry", "delivery_attempt", row.Id, Outcome: row.Status), ct);
        var committed = (await ListDeliveriesAsync(ct)).Single(d => d.Id == row.Id);
        await session.CommitAsync(ct);
        return committed;
    }

    /// <summary>Sends every not-yet-accepted digest of a close. Returns how many are still waiting for a retry.</summary>
    public async Task<int> DispatchAsync(Guid closeId, CancellationToken ct)
    {
        var db = await ledger.DbAsync(ct);
        var rows = await db.Deliveries.Where(d => d.CloseId == closeId && (d.Status == "Queued" || d.Status == "RetryPending")).ToListAsync(ct);
        foreach (var row in rows)
        {
            await AttemptAsync(db, row, ct);
        }

        await db.SaveChangesAsync(ct);
        await session.CommitAsync(ct);
        return rows.Count(r => r.Status == "RetryPending");
    }

    private async Task AttemptAsync(FinanceDbContext db, DeliveryAttemptRow row, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == row.SubscriptionId, ct);
        row.Attempts++;
        row.UpdatedAt = clock.GetUtcNow();
        if (subscription.Status != "Verified" || subscription.Address is null)
        {
            row.Status = "DeadLetter";
            row.LastError = "Recipient is no longer verified.";
            return;
        }

        try
        {
            row.ProviderMessageId = await channel.SendAsync(row.Channel, subscription.Address, row.Payload, ct);
            row.Status = "ProviderAccepted";
            row.LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            row.Status = row.Attempts >= MaxAttempts ? "DeadLetter" : "RetryPending";
            row.LastError = ex.GetType().Name; // type only: provider messages may echo addresses
        }
    }

    private static string? Mask(string? address) =>
        address is null ? null : address.Length <= 4 ? "****" : new string('*', address.Length - 4) + address[^4..];
}

/// <summary>Outbox consumer: dispatches digests after a close is locked. Throws while retries remain so the outbox backs off.</summary>
public sealed class DailyCloseLockedHandler(IServiceScopeFactory scopes, ILogger<DailyCloseLockedHandler> logger) : IOutboxHandler
{
    string IOutboxHandler.MessageType => CloseService.LockedMessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<DailyCloseLockedEvent>(message.Payload)
            ?? throw new InvalidOperationException("Invalid close-locked payload.");
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.CorrelationId = message.CorrelationId;
        context.SystemTenantId = message.TenantId;
        var pending = await scope.ServiceProvider.GetRequiredService<OwnerService>().DispatchAsync(payload.CloseId, ct);
        if (pending > 0)
        {
            logger.LogWarning("Owner digest delivery pending retry for {Count} recipient(s) of close {CloseId}.", pending, payload.CloseId);
            throw new InvalidOperationException("Owner digest delivery pending retry.");
        }
    }
}
