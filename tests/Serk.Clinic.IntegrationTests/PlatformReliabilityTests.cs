using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;
using Serk.Clinic.Platform.Migrations;
using Serk.Clinic.Platform.Outbox;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PlatformReliabilityTests(SerkApiFixture f)
{
    private sealed class FlakyHandler(string type, int failTimes) : IOutboxHandler
    {
        public int Calls;

        public string MessageType => type;

        public Task HandleAsync(OutboxMessage message, CancellationToken ct) =>
            Interlocked.Increment(ref Calls) <= failTimes ? throw new HttpRequestException("downstream unavailable (simulated)") : Task.CompletedTask;
    }

    private async Task<Guid> InsertMessageAsync(string type)
    {
        var id = Guid.CreateVersion7();
        await f.OwnerScalarAsync<object>(
            "INSERT INTO platform.outbox_messages (id, tenant_id, message_type, schema_version, payload, correlation_id, status, attempts, next_attempt_at, created_at) VALUES (@id, @t, @type, 1, '{}'::jsonb, 'it', 'Pending', 0, now() - interval '1 second', now())",
            ("id", id), ("t", DemoData.TenantA), ("type", type));
        return id;
    }

    private OutboxProcessor Processor(IOutboxHandler handler, int maxAttempts) =>
        new(f.Factory.Services.GetRequiredService<NpgsqlDataSource>(), [handler],
            Options.Create(new OutboxOptions { MaxAttempts = maxAttempts, BaseBackoff = TimeSpan.Zero }), TimeProvider.System, NullLogger<OutboxProcessor>.Instance);

    private async Task DrainAsync(OutboxProcessor p)
    {
        for (var i = 0; i < 10; i++)
        {
            await p.ProcessBatchAsync(CancellationToken.None);
            await Task.Delay(5);
        }
    }

    [Fact(DisplayName = "OPS-004 outbox retries transient failures then marks processed exactly once")]
    public async Task Outbox_retries_then_processes()
    {
        var type = "test.flaky." + Guid.NewGuid().ToString("N");
        var id = await InsertMessageAsync(type);
        var handler = new FlakyHandler(type, failTimes: 2);
        await DrainAsync(Processor(handler, maxAttempts: 5));
        Assert.Equal("Processed", await f.OwnerScalarAsync<string>("SELECT status FROM platform.outbox_messages WHERE id=@id", ("id", id)));
        Assert.Equal(3, await f.OwnerScalarAsync<int>("SELECT attempts FROM platform.outbox_messages WHERE id=@id", ("id", id)));
        Assert.Equal(3, handler.Calls);
    }

    [Fact(DisplayName = "OPS-004b poison message is dead-lettered, never silently dropped")]
    public async Task Outbox_dead_letters_poison_messages()
    {
        var type = "test.poison." + Guid.NewGuid().ToString("N");
        var id = await InsertMessageAsync(type);
        await DrainAsync(Processor(new FlakyHandler(type, failTimes: int.MaxValue), maxAttempts: 3));
        Assert.Equal("DeadLettered", await f.OwnerScalarAsync<string>("SELECT status FROM platform.outbox_messages WHERE id=@id", ("id", id)));
        Assert.Equal("HttpRequestException", await f.OwnerScalarAsync<string>("SELECT last_error FROM platform.outbox_messages WHERE id=@id", ("id", id)));
    }

    [Fact(DisplayName = "Outbox: two concurrent workers never process the same message twice")]
    public async Task Outbox_concurrent_workers_skip_locked()
    {
        var type = "test.concurrent." + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 30; i++)
        {
            await InsertMessageAsync(type);
        }

        var handler = new FlakyHandler(type, failTimes: 0);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DrainAsync(Processor(handler, 3))));
        Assert.Equal(30, handler.Calls);
        Assert.Equal(30L, await f.OwnerScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages WHERE message_type=@t AND status='Processed' AND attempts=1", ("t", type)));
    }

    [Fact(DisplayName = "OPS-001 migrations are repeatable (no-op on re-run) and detect edited history")]
    public async Task Migrations_are_idempotent_and_tamper_evident()
    {
        var migrator = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance);
        Assert.Empty(await migrator.MigrateAsync(f.OwnerConnectionString, SerkApiFixture.MigrationsDirectory()));

        var tmp = Directory.CreateTempSubdirectory("serk-mig-");
        foreach (var file in Directory.GetFiles(SerkApiFixture.MigrationsDirectory()))
        {
            File.Copy(file, Path.Combine(tmp.FullName, Path.GetFileName(file)));
        }

        await File.AppendAllTextAsync(Path.Combine(tmp.FullName, "0003_patients.sql"), "\n-- edited after release\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(f.OwnerConnectionString, tmp.FullName));
        Assert.Contains("0003_patients.sql", ex.Message);
        tmp.Delete(true);
    }

    [Fact(DisplayName = "Runtime role is not owner, not superuser and cannot bypass RLS")]
    public async Task Runtime_role_is_least_privilege()
    {
        await using var conn = new NpgsqlConnection(f.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT rolsuper, rolbypassrls, rolcreaterole, rolcreatedb FROM pg_roles WHERE rolname = current_user", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.False(r.GetBoolean(0));
        Assert.False(r.GetBoolean(1));
        Assert.False(r.GetBoolean(2));
        Assert.False(r.GetBoolean(3));
        await r.CloseAsync();
        await using var owns = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE tableowner = current_user", conn);
        Assert.Equal(0L, (long)(await owns.ExecuteScalarAsync())!);
        await using var ddl = new NpgsqlCommand("CREATE TABLE patients.evil (id int)", conn);
        await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
    }

    [Fact(DisplayName = "SEC-004 synthetic patient identifiers never appear in application logs")]
    public async Task Logs_do_not_contain_phi()
    {
        var marker = "Phimarker" + Guid.NewGuid().ToString("N")[..10];
        var phone = "+251 955 " + Random.Shared.Next(100000, 999999);
        using var r = await f.LoginAsync(DemoData.Reception);
        using var d = await f.LoginAsync(DemoData.Clinician);
        var pid = await Scenario.RegisterPatientAsync(r, given: marker, phone: phone);
        await r.GetAsync($"/api/v1/patients?query={marker}");
        await d.SendAsync(HttpMethod.Post, $"/api/v1/patients/{pid}/allergies", new { substance = marker + "-drug", severity = "Mild" });
        // Provoke error paths with the marker in the payload.
        await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = marker, fatherName = marker, sex = "Unknown", birthDate = "2999-01-01", phone }, Scenario.Key());
        await r.SendAsync(HttpMethod.Post, "/api/v1/patients", "{ not json " + marker, Scenario.Key());
        Assert.NotEmpty(f.Logs.Lines);
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains(marker, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains(phone.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal) || l.Contains(phone, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Request body limit and malformed JSON produce 4xx problem details, never 500")]
    public async Task Request_limits()
    {
        using var r = await f.LoginAsync(DemoData.Reception);
        var huge = new string('a', 2 * 1024 * 1024);
        var big = await r.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = huge }, Scenario.Key());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/patients") { Content = new StringContent("{ broken", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-CSRF-Token", r.Csrf);
        req.Headers.Add("Idempotency-Key", Scenario.Key());
        var bad = await r.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact(DisplayName = "Health endpoints report liveness and database readiness")]
    public async Task Health()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
    }

    [Fact(DisplayName = "OpenAPI contract is generated and matches the committed snapshot")]
    public async Task OpenApi_contract_snapshot()
    {
        using var c = f.Client();
        var json = await (await c.GetAsync("/openapi/v1.json")).Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        foreach (var expected in new[] { "/api/v1/patients", "/api/v1/encounters/{id}/sign", "/api/v1/documents/{id}/finalize", "/api/v1/appointments/{id}/check-in" })
        {
            Assert.Contains(expected, paths);
        }

        var root = Directory.GetParent(SerkApiFixture.MigrationsDirectory())!.Parent!.FullName;
        var snapshotPath = Path.Combine(root, "docs", "api", "openapi-paths-v1.txt");
        var current = string.Join("\n", paths) + "\n";
        if (Environment.GetEnvironmentVariable("SERK_UPDATE_API_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, current);
            await File.WriteAllTextAsync(Path.Combine(root, "docs", "api", "openapi-v1.json"), json);
        }

        Assert.True(File.Exists(snapshotPath), "Run with SERK_UPDATE_API_SNAPSHOT=1 to create the API snapshot, then review the diff.");
        Assert.Equal(await File.ReadAllTextAsync(snapshotPath), current);
    }
}
