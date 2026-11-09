using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Serk.Clinic.Host.Infrastructure;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "SAMEORIGIN";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
            "frame-src 'self' blob:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'self'; connect-src 'self'";
        if (http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            h.CacheControl = "no-store";
        }

        return next(http);
    }
}

/// <summary>
/// Server-independent request size guard (Kestrel also enforces MaxRequestBodySize; reverse proxies or other servers may not).
/// </summary>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.Request.ContentLength > SerkHost.MaxRequestBodyBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await Results.Problem(title: "Payload too large", detail: "The request body exceeds the allowed size.", statusCode: 413).ExecuteAsync(http);
            return;
        }

        var feature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = SerkHost.MaxRequestBodyBytes;
        }

        // Chunked bodies have no Content-Length: cap the bytes actually read.
        http.Request.Body = new LimitedReadStream(http.Request.Body, SerkHost.MaxRequestBodyBytes);
        await next(http);
    }

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int n)
        {
            _read += n;
            return _read > limit ? throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge) : n;
        }
    }
}

/// <summary>Adds a W3C Server-Timing header with total server processing time (ms, microsecond resolution) to API responses.</summary>
public sealed class ServerTimingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        http.Response.OnStarting(() =>
        {
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            http.Response.Headers.Append("Server-Timing", "app;dur=" + ms.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        });
        await next(http);
    }
}

public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable: " + ex.GetType().Name);
        }
    }
}

/// <summary>Development/test only: provisions the runtime login role (member of serk_app_role, non-owner, no BYPASSRLS).</summary>
public static class DevDatabaseBootstrap
{
    public static async Task EnsureAppLoginAsync(string ownerConnection, string username, string password, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-z_][a-z0-9_]{2,40}$"))
        {
            throw new ArgumentException("Invalid role name.", nameof(username));
        }

        await using var conn = new NpgsqlConnection(ownerConnection);
        await conn.OpenAsync(ct);
        string verb;
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @r", conn))
        {
            exists.Parameters.AddWithValue("r", username);
            verb = await exists.ExecuteScalarAsync(ct) is null ? "CREATE" : "ALTER";
        }

        // Identifiers/passwords cannot be bound in DDL: let PostgreSQL quote them via format(%I, %L) from bound parameters.
        string ddl;
        await using (var build = new NpgsqlCommand("SELECT format('%s ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L', @v::text, @r::text, @p::text)", conn))
        {
            build.Parameters.AddWithValue("v", verb);
            build.Parameters.AddWithValue("r", username);
            build.Parameters.AddWithValue("p", password);
            ddl = (string)(await build.ExecuteScalarAsync(ct))!;
        }

        await using (var create = new NpgsqlCommand(ddl, conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var grant = new NpgsqlCommand($"GRANT serk_app_role TO \"{username}\"", conn);
        await grant.ExecuteNonQueryAsync(ct);
    }
}
