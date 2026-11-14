using Microsoft.Extensions.DependencyInjection;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Export;

/// <summary>
/// Declarative dataset owned by a module. The SQL runs inside the request's tenant-scoped transaction (row-level security
/// applies) and may use: <c>@t</c> tenant, <c>@f</c> facility, <c>@from_utc</c>/<c>@to_utc</c> (start of the first and
/// end of the last facility-local day), <c>@dfrom</c>/<c>@dto</c> (dates), <c>@tz</c> (IANA zone) and <c>@limit</c>.
/// Columns must match the SELECT order.
/// </summary>
public sealed record ExportDefinition(
    string Code,
    string Group,
    string Title,
    string Description,
    IReadOnlyList<string> RequiredCapabilities,
    bool ContainsPatientData,
    bool UsesDateRange,
    IReadOnlyList<string> Columns,
    string Sql);

public sealed class SqlExportDataset(DbSession session, ExportDefinition definition) : IExportDataset
{
    public ExportDefinition Definition => definition;

    public string Code => definition.Code;

    public string Group => definition.Group;

    public string Title => definition.Title;

    public string Description => definition.Description;

    public IReadOnlyList<string> RequiredCapabilities => definition.RequiredCapabilities;

    public bool ContainsPatientData => definition.ContainsPatientData;

    public bool UsesDateRange => definition.UsesDateRange;

    public IReadOnlyList<string> Columns => definition.Columns;

    public Task<IReadOnlyList<object?[]>> ReadAsync(ExportQuery query, int limit, CancellationToken ct)
    {
        var zone = BusinessTime.Resolve(query.TimeZone);
        var (fromUtc, _) = BusinessTime.DayRangeUtc(query.From, zone);
        var (_, toUtc) = BusinessTime.DayRangeUtc(query.To, zone);
        return ExportService.QueryAsync(session, definition.Sql, p =>
        {
            p.AddWithValue("t", query.Actor.TenantId);
            p.AddWithValue("f", query.Actor.FacilityId);
            p.AddWithValue("from_utc", fromUtc);
            p.AddWithValue("to_utc", toUtc);
            p.AddWithValue("dfrom", query.From);
            p.AddWithValue("dto", query.To);
            p.AddWithValue("tz", query.TimeZone);
            p.AddWithValue("limit", limit);
        }, ct);
    }
}

public static class ExportRegistration
{
    public static IServiceCollection AddExportDatasets(this IServiceCollection services, params ExportDefinition[] definitions)
    {
        foreach (var d in definitions)
        {
            services.AddScoped<IExportDataset>(sp => new SqlExportDataset(sp.GetRequiredService<DbSession>(), d));
        }

        return services;
    }
}
