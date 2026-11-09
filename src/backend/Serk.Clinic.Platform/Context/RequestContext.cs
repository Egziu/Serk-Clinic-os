namespace Serk.Clinic.Platform.Context;

/// <summary>
/// The authenticated staff member for the current request. Tenant and facility come exclusively from a server-side,
/// validated session affiliation — never from client-supplied headers or bodies.
/// </summary>
public sealed record StaffActor(
    Guid UserId,
    Guid SessionId,
    Guid AffiliationId,
    Guid TenantId,
    Guid FacilityId,
    Guid? PractitionerId,
    string DisplayName,
    IReadOnlySet<string> Capabilities)
{
    public bool Has(string capability) => Capabilities.Contains(capability);
}

/// <summary>Scoped per-request ambient context populated by authentication and correlation middleware.</summary>
public sealed class RequestContext
{
    public StaffActor? Actor { get; set; }

    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Tenant override used only by trusted background workers (outbox) after validating the message tenant.</summary>
    public Guid? SystemTenantId { get; set; }

    public Guid? EffectiveTenantId => Actor?.TenantId ?? SystemTenantId;

    public StaffActor RequireActor() =>
        Actor ?? throw new InvalidOperationException("No authenticated staff actor in the request context.");
}
