using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class RateLimitTests(SerkApiFixture f)
{
    [Fact(DisplayName = "HARDEN-002 sign-in attempts beyond the per-client limit are refused with 429 (and the client explains it)")]
    public async Task Login_rate_limit_applies()
    {
        // A separate host with the shipped-style low limit (the shared test host raises it so other tests are not throttled).
        await using var limited = f.Factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:LoginPerMinute", "3"));
        using var http = limited.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await http.PostAsJsonAsync("/api/v1/auth/login", new { username = "nobody.here", password = "wrong-password-123" })).StatusCode);
        }

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }
}
