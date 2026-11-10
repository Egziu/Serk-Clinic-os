using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Serk.Clinic.Host.Infrastructure;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.Platform.Migrations;
using Testcontainers.PostgreSql;

namespace Serk.Clinic.IntegrationTests.Infrastructure;

/// <summary>
/// Real PostgreSQL 16 (Testcontainers, or an external server via SERK_TEST_PG_ADMIN for CI service containers), real SQL
/// migrations, a dedicated non-owner runtime login, the synthetic seed, and the real ASP.NET Core pipeline on TestServer.
/// If no database can be provisioned the fixture FAILS — integration tests are never silently skipped.
/// </summary>
public sealed class SerkApiFixture : IAsyncLifetime
{
    public const string DemoPassword = "Integration-Synthetic-1!";
    public const string AppLogin = "serk_it_app";
    public const string AppPassword = "it-app-password-synthetic";

    private PostgreSqlContainer? _container;
    private WebApplicationFactory<Program>? _factory;

    public string OwnerConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";
    public string ObjectRoot { get; } = Path.Combine(Path.GetTempPath(), "serk-it-objects-" + Guid.NewGuid().ToString("N"));
    public CapturingLoggerProvider Logs { get; } = new();

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        var admin = Environment.GetEnvironmentVariable("SERK_TEST_PG_ADMIN");
        if (string.IsNullOrWhiteSpace(admin))
        {
            _container = new PostgreSqlBuilder("postgres:16.6-alpine").WithPassword("it-postgres-synthetic").Build();
            await _container.StartAsync();
            admin = _container.GetConnectionString();
        }

        var dbName = "serk_it_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {dbName}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        OwnerConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = dbName, Pooling = false }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = dbName, Username = AppLogin, Password = AppPassword, MaxPoolSize = 30 }.ConnectionString;

        await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).MigrateAsync(OwnerConnectionString, MigrationsDirectory());
        await DevDatabaseBootstrap.EnsureAppLoginAsync(OwnerConnectionString, AppLogin, AppPassword, CancellationToken.None);
        await DemoDataSeeder.SeedAsync(OwnerConnectionString, DemoPassword, CancellationToken.None, superAdminPassword: DemoPassword);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:App", AppConnectionString);
            b.UseSetting("ConnectionStrings:Migrations", OwnerConnectionString);
            b.UseSetting("Database:MigrateOnStartup", "false");
            b.UseSetting("Seed:Demo", "false");
            b.UseSetting("Outbox:Enabled", "false");
            b.UseSetting("ObjectStore:RootPath", ObjectRoot);
            b.UseSetting("RateLimits:LoginPerMinute", "100000");
            b.UseSetting("Documents:PublicBaseUrl", "https://verify.test");
            b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace)));
        });
        _ = _factory.Server; // start the host now
    }

    public ApiClient Client() => new(Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false }));

    public async Task<ApiClient> LoginAsync(string username, Guid? facility = null)
    {
        var c = Client();
        await c.LoginAsync(username, DemoPassword);
        if (facility is not null)
        {
            await c.SelectFacilityAsync(facility.Value);
        }

        return c;
    }

    public async Task<T?> OwnerScalarAsync<T>(string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v);
        }

        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default : (T)r;
    }

    public static string MigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "migrations")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("database/migrations not found"), "database", "migrations");
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (Directory.Exists(ObjectRoot))
        {
            Directory.Delete(ObjectRoot, true);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<SerkApiFixture>
{
    public const string Name = "api";
}

/// <summary>Captures every log line written by the host so tests can assert PHI is never logged (SEC-004).</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Capturing(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lines.Enqueue(category + " scope: " + state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
