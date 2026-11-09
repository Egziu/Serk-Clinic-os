using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;

namespace Serk.Clinic.Platform.Outbox;

/// <summary>Writes integration events in the same transaction as the business change. Payloads carry identifiers, not PHI.</summary>
public sealed class OutboxWriter(DbSession session, RequestContext context, TimeProvider clock)
{
    public async Task<Guid> EnqueueAsync<TPayload>(string messageType, TPayload payload, CancellationToken ct = default)
    {
        var tenant = context.EffectiveTenantId ?? throw new InvalidOperationException("Outbox messages require a tenant.");
        var id = Guid.CreateVersion7();
        await using var cmd = await session.CommandAsync(
            """
            INSERT INTO platform.outbox_messages (id, tenant_id, message_type, schema_version, payload, correlation_id, status, attempts, next_attempt_at, created_at)
            VALUES (@id, @tenant, @type, 1, @payload, @corr, 'Pending', 0, @now, @now)
            """,
            ct);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("type", messageType);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(payload) });
        cmd.Parameters.AddWithValue("corr", context.CorrelationId);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }
}

public sealed record OutboxMessage(Guid Id, Guid TenantId, string MessageType, string Payload, int Attempts, string CorrelationId);

/// <summary>Idempotent consumer. Handlers must tolerate redelivery (at-least-once semantics).</summary>
public interface IOutboxHandler
{
    string MessageType { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken ct);
}

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Claims due messages with <c>FOR UPDATE SKIP LOCKED</c> (safe with multiple workers), dispatches, and records the
/// outcome. Failures back off exponentially with jitter; after MaxAttempts the message is DeadLettered (never dropped).
/// </summary>
public sealed class OutboxProcessor(
    NpgsqlDataSource dataSource,
    IEnumerable<IOutboxHandler> handlers,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    private readonly Dictionary<string, IOutboxHandler> _handlers = handlers.ToDictionary(h => h.MessageType, StringComparer.Ordinal);

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var opts = options.Value;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var batch = new List<OutboxMessage>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, tenant_id, message_type, payload::text, attempts, correlation_id
            FROM platform.outbox_messages
            WHERE status = 'Pending' AND next_attempt_at <= @now
            ORDER BY next_attempt_at, id
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """,
            conn,
            tx))
        {
            select.Parameters.AddWithValue("now", clock.GetUtcNow());
            select.Parameters.AddWithValue("limit", opts.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                batch.Add(new OutboxMessage(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
            }
        }

        foreach (var message in batch)
        {
            string status;
            string? error = null;
            var attempts = message.Attempts + 1;
            var nextAttempt = clock.GetUtcNow();
            try
            {
                if (!_handlers.TryGetValue(message.MessageType, out var handler))
                {
                    throw new InvalidOperationException("No handler registered for message type.");
                }

                await handler.HandleAsync(message, ct);
                status = "Processed";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log type only: exception messages from integrations may echo payload data.
                error = ex.GetType().Name;
                status = attempts >= opts.MaxAttempts ? "DeadLettered" : "Pending";
                var backoff = opts.BaseBackoff * Math.Pow(2, Math.Min(attempts - 1, 10));
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Max(1, opts.BaseBackoff.TotalMilliseconds)));
                nextAttempt = clock.GetUtcNow() + backoff + jitter;
                logger.LogWarning("Outbox message {MessageId} of type {MessageType} failed attempt {Attempt} with {ErrorType}; status {Status}.", message.Id, message.MessageType, attempts, error, status);
            }

            await using var update = new NpgsqlCommand(
                """
                UPDATE platform.outbox_messages
                SET status = @status, attempts = @attempts, next_attempt_at = @next, last_error = @error,
                    processed_at = CASE WHEN @status = 'Processed' THEN @now ELSE processed_at END
                WHERE id = @id
                """,
                conn,
                tx);
            update.Parameters.AddWithValue("status", status);
            update.Parameters.AddWithValue("attempts", attempts);
            update.Parameters.AddWithValue("next", nextAttempt);
            update.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            update.Parameters.AddWithValue("now", clock.GetUtcNow());
            update.Parameters.AddWithValue("id", message.Id);
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return batch.Count;
    }
}

public sealed class OutboxBackgroundService(IServiceProvider services, IOptions<OutboxOptions> options, ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                var processed = await processor.ProcessBatchAsync(stoppingToken);
                if (processed > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Outbox polling failed with {ErrorType}; will retry.", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
