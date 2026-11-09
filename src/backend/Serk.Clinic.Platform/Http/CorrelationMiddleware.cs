using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serk.Clinic.Platform.Context;

namespace Serk.Clinic.Platform.Http;

/// <summary>Accepts a well-formed inbound X-Correlation-ID or generates one; echoes it and adds it to the log scope.</summary>
public sealed partial class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext http, RequestContext context)
    {
        var inbound = http.Request.Headers[HeaderName].ToString();
        context.CorrelationId = SafeId().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString("N");
        http.Response.Headers[HeaderName] = context.CorrelationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = context.CorrelationId }))
        {
            await next(http);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();
}
