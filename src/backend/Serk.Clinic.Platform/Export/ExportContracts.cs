using Serk.Clinic.Platform.Context;

namespace Serk.Clinic.Platform.Export;

/// <summary>A date-bounded export request already validated by <see cref="ExportService"/>.</summary>
public sealed record ExportQuery(StaffActor Actor, DateOnly From, DateOnly To, string TimeZone);

/// <summary>
/// One downloadable dataset, implemented by the module that owns the data (modules never read each other's tables).
/// Registered in DI; <see cref="ExportService"/> enforces capabilities, purpose, step-up, logging and the row cap.
/// </summary>
public interface IExportDataset
{
    /// <summary>Stable code, e.g. <c>finance.payments</c>.</summary>
    string Code { get; }

    string Group { get; }

    string Title { get; }

    string Description { get; }

    /// <summary>Capabilities needed to read the source data, in addition to the export capability itself.</summary>
    IReadOnlyList<string> RequiredCapabilities { get; }

    /// <summary>True when rows identify patients: needs <c>data.export.patient</c>, a purpose and password confirmation.</summary>
    bool ContainsPatientData { get; }

    /// <summary>False for snapshot datasets (e.g. staff access list); the date range is then ignored.</summary>
    bool UsesDateRange { get; }

    IReadOnlyList<string> Columns { get; }

    /// <summary>Rows in column order. Must return at most <paramref name="limit"/> rows (more means "too many").</summary>
    Task<IReadOnlyList<object?[]>> ReadAsync(ExportQuery query, int limit, CancellationToken ct);
}

/// <summary>Re-confirms the signed-in user's own password before sensitive actions (failures count towards lockout).</summary>
public interface IStepUpVerifier
{
    Task<bool> VerifyAsync(Guid userId, string? password, string purpose, CancellationToken ct);
}

/// <summary>Resolves a facility's IANA time zone (for local-day export ranges).</summary>
public interface IFacilityTimeZones
{
    Task<string> GetAsync(Guid tenantId, Guid facilityId, CancellationToken ct);
}
