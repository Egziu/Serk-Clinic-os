using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Serk.Clinic.Analytics;
using Serk.Clinic.Analytics.Api;
using Serk.Clinic.Clinical;
using Serk.Clinic.Clinical.Api;
using Serk.Clinic.Configuration;
using Serk.Clinic.Configuration.Api;
using Serk.Clinic.Dental;
using Serk.Clinic.Dental.Api;
using Serk.Clinic.Documents;
using Serk.Clinic.Documents.Api;
using Serk.Clinic.Documents.Application;
using Serk.Clinic.Host.Infrastructure;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.Identity;
using Serk.Clinic.Identity.Api;
using Serk.Clinic.Patients;
using Serk.Clinic.Patients.Api;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.Platform.Http;
using Serk.Clinic.Platform.Idempotency;
using Serk.Clinic.Platform.Migrations;
using Serk.Clinic.Platform.Outbox;
using Serk.Clinic.Platform.Storage;
using Serk.Clinic.Scheduling;
using Serk.Clinic.Scheduling.Api;

namespace Serk.Clinic.Host;

public static class SerkHost
{
    public const long MaxRequestBodyBytes = 1024 * 1024;

    /// <summary>Builds the app. Returns null when a one-shot CLI command (e.g. "migrate") was executed instead.</summary>
    public static async Task<WebApplication?> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var config = builder.Configuration;

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxRequestBodyBytes);

        // Resolved lazily so configuration overrides applied at Build() (hosting, tests) are honoured. Fail fast at startup below.
        builder.Services.AddSingleton(sp =>
        {
            var appConnection = sp.GetRequiredService<IConfiguration>().GetConnectionString("App");
            if (string.IsNullOrWhiteSpace(appConnection))
            {
                throw new InvalidOperationException("ConnectionStrings:App (runtime, non-owner role) is required.");
            }

            return new NpgsqlDataSourceBuilder(appConnection).Build();
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<RequestContext>();
        builder.Services.AddScoped<DbSession>();
        builder.Services.AddScoped<AuditTrail>();
        builder.Services.AddScoped<OutboxWriter>();
        builder.Services.AddScoped<IdempotencyStore>();
        builder.Services.AddScoped<OutboxProcessor>();
        builder.Services.Configure<OutboxOptions>(config.GetSection("Outbox"));
        builder.Services.AddHostedService<OutboxBackgroundService>();
        builder.Services.Configure<FileSystemObjectStoreOptions>(config.GetSection("ObjectStore"));
        builder.Services.AddSingleton<IObjectStore, FileSystemObjectStore>();
        builder.Services.Configure<DocumentOptions>(config.GetSection("Documents"));

        builder.Services.AddIdentityModule().AddPatientsModule().AddClinicalModule().AddSchedulingModule().AddDocumentsModule().AddDentalModule().AddConfigurationModule().AddAnalyticsModule();
        builder.Services.AddOptions<Serk.Clinic.Scheduling.Application.NoShowOptions>().Bind(config.GetSection("Scheduling:NoShow"))
            .Validate(o => o.Grace >= Serk.Clinic.Scheduling.Application.NoShowOptions.MinimumGrace && o.Interval >= TimeSpan.FromSeconds(10), "Scheduling:NoShow:Grace must be at least 30 minutes and Interval at least 10 seconds.")
            .ValidateOnStart();

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)); // enums only by name
            o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // mass-assignment guard
        });
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
            c.ProblemDetails.Extensions["correlationId"] = c.HttpContext.RequestServices.GetRequiredService<RequestContext>().CorrelationId);
        builder.Services.AddOpenApi("v1");
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimits:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("verify", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var app = builder.Build();

        var publicBase = app.Configuration["Documents:PublicBaseUrl"] ?? "";
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")
            && (!Uri.TryCreate(publicBase, UriKind.Absolute, out var baseUri) || baseUri.IsLoopback || baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Documents:PublicBaseUrl must be an absolute public https URL outside Development (QR verification).");
        }

        var ownerConnection = app.Configuration.GetConnectionString("Migrations");
        _ = app.Services.GetRequiredService<NpgsqlDataSource>(); // fail fast on missing runtime connection string
        if (args.Contains("migrate"))
        {
            await RunMigrationsAsync(app, ownerConnection);
            return null;
        }

        if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
        {
            await RunMigrationsAsync(app, ownerConnection);
        }

        if (app.Configuration.GetValue("Seed:Demo", false))
        {
            if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Synthetic demo seeding is only allowed in Development/Testing.");
            }

            await DemoDataSeeder.SeedAsync(ownerConnection!, app.Configuration["Seed:DemoPassword"] ?? throw new InvalidOperationException("Seed:DemoPassword required."), CancellationToken.None);
        }

        Configure(app);
        return app;
    }

    private static async Task RunMigrationsAsync(WebApplication app, string? ownerConnection)
    {
        if (string.IsNullOrWhiteSpace(ownerConnection))
        {
            throw new InvalidOperationException("ConnectionStrings:Migrations (schema owner) is required to migrate.");
        }

        var migrator = new DatabaseMigrator(app.Services.GetRequiredService<ILogger<DatabaseMigrator>>());
        var dir = app.Configuration["Database:MigrationsPath"] ?? Path.Combine(AppContext.BaseDirectory, "migrations");
        await migrator.MigrateAsync(ownerConnection, dir);
        var appLogin = app.Configuration["Database:EnsureAppLogin:Username"];
        if (!string.IsNullOrWhiteSpace(appLogin))
        {
            if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Automatic app login provisioning is only allowed in Development/Testing.");
            }

            await DevDatabaseBootstrap.EnsureAppLoginAsync(ownerConnection, appLogin, app.Configuration["Database:EnsureAppLogin:Password"]!, CancellationToken.None);
        }
    }

    private static void Configure(WebApplication app)
    {
        app.UseExceptionHandler(handler => handler.Run(async http =>
        {
            var feature = http.Features.Get<IExceptionHandlerFeature>();
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Serk.Unhandled");
            // Exception messages can echo SQL parameter values (PHI). Log the type and correlation only.
            if (feature?.Error is BadHttpRequestException bad)
            {
                // Malformed JSON, oversize body, bad route values: client error, not a server fault.
                http.Response.StatusCode = bad.StatusCode;
                await Results.Problem(title: "Invalid request", detail: "The request body or parameters could not be read.", statusCode: bad.StatusCode).ExecuteAsync(http);
                return;
            }

            logger.LogError("Unhandled {ExceptionType} on {Method} {Endpoint}.", feature?.Error.GetType().Name, http.Request.Method, http.GetEndpoint()?.DisplayName);
            if (!app.Environment.IsProduction() && app.Configuration.GetValue("Diagnostics:LogExceptionDetails", false))
            {
                // Synthetic-data environments only: full detail aids debugging. Never enabled in Production.
                logger.LogError("Exception detail (non-production diagnostics): {Detail}", feature?.Error.ToString());
            }

            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await Results.Problem(
                title: "Unexpected error",
                detail: "The request could not be completed. Quote the correlation ID when reporting this problem.",
                statusCode: 500,
                extensions: new Dictionary<string, object?> { ["correlationId"] = http.RequestServices.GetRequiredService<RequestContext>().CorrelationId })
                .ExecuteAsync(http);
        }));
        app.UseMiddleware<ServerTimingMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseMiddleware<RequestSizeLimitMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
        if (app.Configuration.GetValue("OpenApi:Expose", app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
        {
            app.MapOpenApi("/openapi/{documentName}.json");
        }

        app.MapIdentityEndpoints();
        app.MapPatientEndpoints();
        app.MapAppointmentEndpoints();
        app.MapEncounterEndpoints();
        app.MapDocumentEndpoints();
        app.MapDentalEndpoints();
        app.MapConfigurationEndpoints();
        app.MapAnalyticsEndpoints();
        app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: 404, title: "Not found"));
        app.MapFallbackToFile("index.html");
    }
}
