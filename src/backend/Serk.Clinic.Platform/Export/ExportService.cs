using System.Security.Cryptography;
using Npgsql;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Export;

public sealed record ExportDatasetDto(string Code, string Group, string Title, string Description, bool ContainsPatientData, bool UsesDateRange, IReadOnlyList<string> Columns);

public sealed record ExportRequest(string? Format, DateOnly? From, DateOnly? To, string? Purpose, string? Password);

public sealed record ExportFile(byte[] Bytes, string FileName, string ContentType, int RowCount, string Sha256);

public sealed record ExportLogDto(Guid Id, string Dataset, string Format, DateOnly? From, DateOnly? To, bool ContainsPatientData, string? Purpose, int RowCount, string Sha256, Guid UserId, DateTimeOffset CreatedAt);

/// <summary>
/// The single gate for every data export. Rules: the dataset's own read capabilities plus <c>data.export</c>
/// (or <c>data.export.patient</c> for patient-identifying data, which also needs a written purpose and the user's
/// password); at most 366 days and <see cref="MaxRows"/> rows; every export is logged with its SHA-256 in the same
/// transaction as its audit event. Nothing is kept on the server.
/// </summary>
public sealed class ExportService(
    IEnumerable<IExportDataset> datasets,
    DbSession session,
    RequestContext context,
    AuditTrail audit,
    IStepUpVerifier stepUp,
    IFacilityTimeZones timeZones,
    TimeProvider clock)
{
    public const int MaxRows = 100_000;
    public const int MaxDays = 366;

    private readonly Dictionary<string, IExportDataset> _byCode = datasets.ToDictionary(d => d.Code, StringComparer.Ordinal);

    public static bool CanRun(StaffActor actor, IExportDataset d) =>
        actor.Has(d.ContainsPatientData ? Capabilities.PatientDataExport : Capabilities.DataExport) && d.RequiredCapabilities.All(actor.Has);

    public IReadOnlyList<ExportDatasetDto> Catalogue()
    {
        var actor = context.RequireActor();
        return _byCode.Values.Where(d => CanRun(actor, d))
            .OrderBy(d => d.Group, StringComparer.Ordinal).ThenBy(d => d.Title, StringComparer.Ordinal)
            .Select(d => new ExportDatasetDto(d.Code, d.Group, d.Title, d.Description, d.ContainsPatientData, d.UsesDateRange, d.Columns))
            .ToList();
    }

    public async Task<Result<ExportFile>> RunAsync(string code, ExportRequest request, CancellationToken ct)
    {
        var actor = context.RequireActor();
        if (!_byCode.TryGetValue(code, out var dataset))
        {
            return Error.NotFound();
        }

        if (!CanRun(actor, dataset))
        {
            await audit.RecordSecurityEventAsync("authorization.denied", "denied:export:" + code, ct: ct);
            return Error.Forbidden("capability_required", "Your role does not permit this export.");
        }

        var format = (request.Format ?? "csv").Trim().ToLowerInvariant();
        if (format is not ("csv" or "json"))
        {
            return Error.Validation("invalid_format", "Choose CSV or JSON.", new Dictionary<string, string[]> { ["format"] = ["CSV or JSON."] });
        }

        var tz = await timeZones.GetAsync(actor.TenantId, actor.FacilityId, ct);
        var today = BusinessTime.LocalDate(clock.GetUtcNow(), BusinessTime.Resolve(tz));
        var to = request.To ?? today;
        var from = request.From ?? to.AddDays(-29);
        if (dataset.UsesDateRange && (from > to || to.DayNumber - from.DayNumber + 1 > MaxDays || to > today.AddDays(366)))
        {
            return Error.Validation("invalid_range", $"Choose a range of 1 to {MaxDays} days with 'from' on or before 'to'.",
                new Dictionary<string, string[]> { ["from"] = [$"1 to {MaxDays} days."] });
        }

        string? purpose = null;
        if (dataset.ContainsPatientData)
        {
            purpose = request.Purpose?.Trim();
            if (purpose is null || purpose.Length is < 10 or > 300)
            {
                return Error.Validation("purpose_required", "Patient data exports need a purpose (10–300 characters), e.g. 'Ministry quarterly report'.",
                    new Dictionary<string, string[]> { ["purpose"] = ["10–300 characters."] });
            }

            if (!await stepUp.VerifyAsync(actor.UserId, request.Password, "export:" + code, ct))
            {
                return Error.Forbidden("step_up_failed", "Confirm with your own current password to export patient data.");
            }
        }

        var rows = await dataset.ReadAsync(new ExportQuery(actor, from, to, tz), MaxRows + 1, ct);
        if (rows.Count > MaxRows)
        {
            return Error.Rule("too_many_rows", $"More than {MaxRows:N0} rows. Choose a shorter date range.");
        }

        var now = clock.GetUtcNow();
        var bytes = format == "csv" ? TabularWriter.Csv(dataset.Columns, rows) : TabularWriter.Json(dataset.Code, now, dataset.Columns, rows);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = Guid.CreateVersion7();
        await using (var cmd = await session.CommandAsync(
            """
            INSERT INTO platform.export_log (id, tenant_id, facility_id, user_id, dataset, format, range_from, range_to,
              contains_patient_data, purpose, row_count, sha256, created_at, correlation_id)
            VALUES (@id, @t, @f, @u, @d, @fmt, @from, @to, @phi, @purpose, @rows, @sha, @at, @corr)
            """, ct))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("t", actor.TenantId);
            cmd.Parameters.AddWithValue("f", actor.FacilityId);
            cmd.Parameters.AddWithValue("u", actor.UserId);
            cmd.Parameters.AddWithValue("d", dataset.Code);
            cmd.Parameters.AddWithValue("fmt", format);
            cmd.Parameters.AddWithValue("from", dataset.UsesDateRange ? from : DBNull.Value);
            cmd.Parameters.AddWithValue("to", dataset.UsesDateRange ? to : DBNull.Value);
            cmd.Parameters.AddWithValue("phi", dataset.ContainsPatientData);
            cmd.Parameters.AddWithValue("purpose", (object?)purpose ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rows", rows.Count);
            cmd.Parameters.AddWithValue("sha", sha);
            cmd.Parameters.AddWithValue("at", now);
            cmd.Parameters.AddWithValue("corr", context.CorrelationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await audit.RecordAsync(new AuditEntry("data.export", "export", id, Metadata: new Dictionary<string, string>
        {
            ["dataset"] = dataset.Code,
            ["format"] = format,
            ["rows"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sha256"] = sha,
            ["patientData"] = dataset.ContainsPatientData ? "true" : "false",
        }), ct);
        await session.CommitAsync(ct);
        var range = dataset.UsesDateRange ? $"-{from:yyyyMMdd}-{to:yyyyMMdd}" : $"-{today:yyyyMMdd}";
        return new ExportFile(bytes, $"serk-{dataset.Code.Replace('.', '-')}{range}.{format}", format == "csv" ? "text/csv; charset=utf-8" : "application/json", rows.Count, sha);
    }

    public async Task<IReadOnlyList<ExportLogDto>> HistoryAsync(int limit, CancellationToken ct)
    {
        context.RequireActor();
        await using var cmd = await session.CommandAsync(
            """
            SELECT id, dataset, format, range_from, range_to, contains_patient_data, purpose, row_count, sha256, user_id, created_at
            FROM platform.export_log WHERE facility_id = @f ORDER BY created_at DESC LIMIT @limit
            """, ct);
        cmd.Parameters.AddWithValue("f", context.RequireActor().FacilityId);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        var list = new List<ExportLogDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ExportLogDto(r.GetGuid(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4),
                r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetString(8), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10)));
        }

        return list;
    }

    /// <summary>Helper for datasets: runs a parameterised query and returns each row as an object array.</summary>
    public static async Task<IReadOnlyList<object?[]>> QueryAsync(DbSession session, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = await session.CommandAsync(sql, ct);
        bind(cmd.Parameters);
        var rows = new List<object?[]>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var values = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
            {
                values[i] = r.IsDBNull(i) ? null : r.GetValue(i) switch
                {
                    DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                    var v => v,
                };
            }

            rows.Add(values);
        }

        return rows;
    }
}
