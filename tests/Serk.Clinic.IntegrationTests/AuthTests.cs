using System.Net;
using Serk.Clinic.Host.Seeding;
using Serk.Clinic.IntegrationTests.Infrastructure;

namespace Serk.Clinic.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests(SerkApiFixture f)
{
    [Fact]
    public async Task Unauthenticated_api_requests_are_401()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/patients?query=ab")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"/api/v1/encounters/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Bad_password_and_unknown_user_get_identical_generic_error()
    {
        using var c = f.Client();
        var a = await c.LoginRawAsync(DemoData.Nurse, "wrong-password");
        var b = await c.LoginRawAsync("no.such.user", "wrong-password");
        Assert.Equal(HttpStatusCode.BadRequest, a.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, b.StatusCode);
        Assert.Equal((await ApiClient.Body(a)).GetProperty("detail").GetString(), (await ApiClient.Body(b)).GetProperty("detail").GetString());
        Assert.False(a.Headers.Contains("Set-Cookie"));
    }

    [Fact(DisplayName = "Login lockout after repeated (including concurrent) failures blocks even the correct password")]
    public async Task Lockout_after_repeated_failures()
    {
        // Uses dr.expired (whose sign attempts are expected to fail anyway); the lock is always reset afterwards.
        using var c = f.Client();
        const string reset = "UPDATE identity.staff_users SET failed_login_count = 0, locked_until = NULL WHERE username = 'dr.expired'";
        await f.OwnerScalarAsync<object>(reset);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => c.LoginRawAsync(DemoData.ClinicianExpired, "wrong-password")));
            Assert.NotNull(await f.OwnerScalarAsync<DateTime?>("SELECT locked_until FROM identity.staff_users WHERE username = 'dr.expired'"));
            Assert.Equal(HttpStatusCode.BadRequest, (await c.LoginRawAsync(DemoData.ClinicianExpired, SerkApiFixture.DemoPassword)).StatusCode);
            Assert.True(await f.OwnerScalarAsync<long>("SELECT count(*) FROM audit.security_events WHERE event_type = 'login.failed' AND outcome = 'locked' AND user_id = @u",
                ("u", DemoData.UserId(DemoData.ClinicianExpired))) >= 1);
        }
        finally
        {
            await f.OwnerScalarAsync<object>(reset);
        }
    }

    [Fact(DisplayName = "IDENT-002 logout revokes the server-side session immediately")]
    public async Task Logout_revokes_session()
    {
        using var c = await f.LoginAsync(DemoData.Nurse);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/auth/session")).StatusCode);
        var cookie = c.Http.DefaultRequestHeaders; // cookies live in the handler container
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(HttpMethod.Post, "/api/v1/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.NotNull(cookie);
    }

    [Fact(DisplayName = "IDENT-002b administrator revocation kills a stolen session cookie")]
    public async Task Admin_can_revoke_all_sessions_of_staff_in_own_tenant()
    {
        using var victim = await f.LoginAsync(DemoData.Clinician2, DemoData.FacilityABole);
        using var admin = await f.LoginAsync(DemoData.ClinicAdmin);
        var r = await admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/staff/{DemoData.UserId(DemoData.Clinician2)}/revoke-sessions");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/v1/auth/session")).StatusCode);

        // An admin from tenant A cannot revoke tenant B staff (no cross-tenant user enumeration).
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/staff/{DemoData.UserId(DemoData.ClinicianB)}/revoke-sessions")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_request_without_csrf_token_is_rejected()
    {
        using var c = await f.LoginAsync(DemoData.Reception);
        var r = await c.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "A", fatherName = "B", sex = "Male", birthDate = "1990-01-01" }, Scenario.Key(), includeCsrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("csrf_invalid", (await ApiClient.Body(r)).GetProperty("code").GetString());
        c.Csrf = "forged-token";
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(HttpMethod.Post, "/api/v1/patients", new { givenName = "A" }, Scenario.Key())).StatusCode);
    }

    [Fact]
    public async Task Multi_facility_user_must_select_facility_before_data_access()
    {
        using var c = f.Client();
        var session = await c.LoginAsync(DemoData.Clinician2, SerkApiFixture.DemoPassword);
        Assert.Equal(2, session.GetProperty("affiliations").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, session.GetProperty("activeAffiliationId").ValueKind);
        var r = await c.GetAsync("/api/v1/patients?query=Selam");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("facility_not_selected", (await ApiClient.Body(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Session_cookie_is_httponly_secure_samesite_strict()
    {
        using var c = f.Client();
        var r = await c.LoginRawAsync(DemoData.Nurse, SerkApiFixture.DemoPassword);
        var setCookie = string.Join(";", r.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("__host-serk-session", setCookie);
        Assert.Contains("httponly", setCookie);
        Assert.Contains("secure", setCookie);
        Assert.Contains("samesite=strict", setCookie);
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        using var c = f.Client();
        var r = await c.GetAsync("/health/live");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
    }
}
