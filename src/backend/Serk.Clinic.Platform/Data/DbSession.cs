using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serk.Clinic.Platform.Context;

namespace Serk.Clinic.Platform.Data;

/// <summary>
/// One PostgreSQL connection + transaction per request (unit of work). All module DbContexts in a request share it,
/// so state changes, audit records and outbox messages commit atomically.
/// Tenant context is applied with transaction-local <c>set_config(..., true)</c>; it is discarded on commit/rollback,
/// so a pooled connection never carries tenant context into the next request (RLS fails closed when unset).
/// Disposing without <see cref="CommitAsync"/> rolls back.
/// </summary>
public sealed class DbSession(NpgsqlDataSource dataSource, RequestContext requestContext) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Type, DbContext> _contexts = new();
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _completed;

    public bool IsStarted => _transaction is not null;

    public async Task<NpgsqlConnection> ConnectionAsync(CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        return _connection!;
    }

    public NpgsqlTransaction Transaction => _transaction ?? throw new InvalidOperationException("Session not started.");

    public async Task<TContext> ContextAsync<TContext>(CancellationToken ct = default)
        where TContext : DbContext
    {
        await EnsureStartedAsync(ct);
        if (_contexts.TryGetValue(typeof(TContext), out var existing))
        {
            return (TContext)existing;
        }

        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(_connection!, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery))
            .Options;
        var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        await context.Database.UseTransactionAsync(_transaction, ct);
        _contexts[typeof(TContext)] = context;
        return context;
    }

    public async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken ct = default)
    {
        var conn = await ConnectionAsync(ct);
        return new NpgsqlCommand(sql, conn, _transaction);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null)
        {
            return;
        }

        foreach (var ctx in _contexts.Values)
        {
            await ctx.SaveChangesAsync(ct);
        }

        await _transaction.CommitAsync(ct);
        _completed = true;
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The database session has already been committed.");
        }

        if (_transaction is not null)
        {
            return;
        }

        _connection = await dataSource.OpenConnectionAsync(ct);
        _transaction = await _connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        var tenantId = requestContext.EffectiveTenantId;
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true), set_config('app.user_id', @user, true)",
            _connection,
            _transaction);
        cmd.Parameters.AddWithValue("tenant", tenantId?.ToString() ?? string.Empty);
        cmd.Parameters.AddWithValue("user", requestContext.Actor?.UserId.ToString() ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts.Values)
        {
            await ctx.DisposeAsync();
        }

        if (_transaction is not null)
        {
            if (!_completed)
            {
                try
                {
                    await _transaction.RollbackAsync();
                }
                catch (InvalidOperationException)
                {
                    // Transaction already completed by the server (e.g. connection broken); nothing to roll back.
                }
            }

            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
