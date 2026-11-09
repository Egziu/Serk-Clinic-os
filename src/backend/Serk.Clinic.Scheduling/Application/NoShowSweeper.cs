using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Serk.Clinic.Identity.Contracts;

namespace Serk.Clinic.Scheduling.Application;

public sealed class NoShowOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Time after the appointment END before a still-booked appointment is marked NoShow (pilot default 2 h).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>
/// Automation: marks appointments that are still "Booked" long after they ended as "NoShow", so day lists and analytics
/// stay accurate without manual clean-up. Runs per tenant with RLS context; every change is audited as the automation actor.
/// Idempotent (only Booked rows change) and safe with several instances (row-level updates under normal locking).
/// </summary>
public sealed class NoShowSweeper(NpgsqlDataSource dataSource, IServiceScopeFactory scopes, IOptions<NoShowOptions> options, TimeProvider clock, ILogger<NoShowSweeper> logger)
{
    public static readonly Guid AutomationUserId = Guid.Parse("00000000-0000-7000-8000-00000000a070");

    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IStaffDirectory>().ListTenantIdsAsync(ct);
        }

        var now = clock.GetUtcNow();
        var cutoff = now - options.Value.Grace;
        var total = 0;
        foreach (var tenant in tenants)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
            {
                set.Parameters.AddWithValue("t", tenant.ToString());
                await set.ExecuteNonQueryAsync(ct);
            }

            var changed = new List<(Guid Id, Guid Patient, Guid Facility)>();
            await using (var update = new NpgsqlCommand(
                """
                UPDATE scheduling.appointments SET status = 'NoShow', version = version + 1
                WHERE tenant_id = @t AND status = 'Booked' AND ends_at < @cutoff
                RETURNING id, patient_id, facility_id
                """,
                conn,
                tx))
            {
                update.Parameters.AddWithValue("t", tenant);
                update.Parameters.AddWithValue("cutoff", cutoff);
                await using var reader = await update.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    changed.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
                }
            }

            foreach (var (id, patient, facility) in changed)
            {
                await using var audit = new NpgsqlCommand(
                    """
                    INSERT INTO audit.audit_events (id, tenant_id, facility_id, actor_user_id, actor_session_id, action, resource_type, resource_id, patient_id, outcome, reason, correlation_id, occurred_at)
                    VALUES (@id, @t, @f, @actor, '00000000-0000-0000-0000-000000000000', 'appointment.no_show.auto', 'appointment', @rid, @p, 'success', 'grace_elapsed', @corr, @at)
                    """,
                    conn,
                    tx);
                audit.Parameters.AddWithValue("id", Guid.CreateVersion7());
                audit.Parameters.AddWithValue("t", tenant);
                audit.Parameters.AddWithValue("f", facility);
                audit.Parameters.AddWithValue("actor", AutomationUserId);
                audit.Parameters.AddWithValue("rid", id);
                audit.Parameters.AddWithValue("p", patient);
                audit.Parameters.AddWithValue("corr", "no-show-sweep");
                audit.Parameters.AddWithValue("at", now);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            total += changed.Count;
        }

        if (total > 0)
        {
            logger.LogInformation("No-show sweep marked {Count} appointments.", total);
        }

        return total;
    }
}

public sealed class NoShowBackgroundService(NoShowSweeper sweeper, IOptions<NoShowOptions> options, ILogger<NoShowBackgroundService> logger) : BackgroundService
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
                await sweeper.SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("No-show sweep failed with {ErrorType}; will retry.", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(options.Value.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
