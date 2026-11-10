using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serk.Clinic.Identity.Application;
using Serk.Clinic.Identity.Domain;
using Serk.Clinic.Platform.Context;

namespace Serk.Clinic.Identity.Api;

/// <summary>Authenticates requests from the opaque, HttpOnly, SameSite=Strict session cookie against server-side sessions.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthService auth,
    RequestContext requestContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SerkSession";
    internal const string SessionItemKey = "serk.session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionPolicy.CookieName, out var token))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await auth.ValidateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("Session invalid or expired.");
        }

        Context.Items[SessionItemKey] = session;
        requestContext.Actor = session.Actor;
        requestContext.UserId = session.UserId;
        requestContext.PlatformAdminUserId = session.IsPlatformAdmin ? session.UserId : null;
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()), new Claim("sid", session.SessionId.ToString())],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>
/// CSRF defence for cookie-authenticated unsafe requests: the per-session token (returned in the session DTO, never in a
/// cookie) must be echoed in the X-CSRF-Token header. Complements SameSite=Strict.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase) { "/api/v1/auth/login" };

    public async Task InvokeAsync(HttpContext http)
    {
        var method = http.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
        if (unsafeMethod && http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !Exempt.Contains(http.Request.Path.Value ?? string.Empty)
            && http.Items[SessionAuthenticationHandler.SessionItemKey] is ValidatedSession session)
        {
            var provided = http.Request.Headers[SessionPolicy.CsrfHeader].ToString();
            if (!FixedEquals(provided, session.CsrfToken))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsJsonAsync(
                    new { type = "about:blank", title = "Forbidden", status = 403, detail = "Missing or invalid CSRF token.", code = "csrf_invalid" },
                    http.RequestAborted);
                return;
            }
        }

        await next(http);
    }

    private static bool FixedEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
