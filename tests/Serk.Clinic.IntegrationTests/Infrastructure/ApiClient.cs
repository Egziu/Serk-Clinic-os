using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serk.Clinic.IntegrationTests.Infrastructure;

/// <summary>Cookie-session HTTP client that behaves like the browser SPA (sends the CSRF header on unsafe requests).</summary>
public sealed class ApiClient(HttpClient http) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string? Csrf { get; set; }
    public HttpClient Http => http;

    public async Task<HttpResponseMessage> LoginRawAsync(string username, string password) =>
        await http.PostAsJsonAsync("/api/v1/auth/login", new { username, password });

    public async Task<JsonElement> LoginAsync(string username, string password)
    {
        var r = await LoginRawAsync(username, password);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Csrf = body.GetProperty("csrfToken").GetString();
        return body;
    }

    public async Task SelectFacilityAsync(Guid facilityId)
    {
        var session = await GetJsonAsync("/api/v1/auth/session");
        var affiliation = session.GetProperty("affiliations").EnumerateArray().Single(a => a.GetProperty("facilityId").GetGuid() == facilityId);
        var r = await SendAsync(HttpMethod.Post, "/api/v1/auth/session/affiliation", new { affiliationId = affiliation.GetProperty("id").GetGuid() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? idempotencyKey = null, string? ifMatch = null, bool includeCsrf = true)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: Json);
        }

        if (includeCsrf && Csrf is not null && method != HttpMethod.Get)
        {
            req.Headers.Add("X-CSRF-Token", Csrf);
        }

        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is not null)
        {
            req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await http.SendAsync(req);
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var r = await GetAsync(url);
        Assert.True(r.IsSuccessStatusCode, $"GET {url} -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public void Dispose() => http.Dispose();
}
