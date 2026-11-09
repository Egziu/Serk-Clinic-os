namespace Serk.Clinic.Identity.Domain;

/// <summary>Session and lockout policy. Values are pilot defaults to be confirmed by the security owner.</summary>
public static class SessionPolicy
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const string CookieName = "__Host-serk-session";
    public const string CsrfHeader = "X-CSRF-Token";
}
