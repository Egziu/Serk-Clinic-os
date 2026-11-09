using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;
using Serk.Clinic.Analytics.Domain;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Platform.Audit;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Analytics.Application;

public sealed record DailyPoint(DateOnly Date, string EthiopianDate, int Appointments, int Attended, int NoShow, int Cancelled, double MovingAverage7);

public sealed record RateWithInterval(int Numerator, int Denominator, double? Rate, double? Low95, double? High95);

public sealed record DurationStats(int N, double? MedianMinutes, double? P90Minutes, double? MeanMinutes);

public sealed record HeatCell(int IsoWeekday, int Hour, int Count);

public sealed record ClinicianLoad(Guid PractitionerId, string Name, int Encounters, int Signed, double? MedianMinutesToSign);

public sealed record Bucket(string Label, int Count);

public sealed record AnalyticsOverview(
    DateOnly From,
    DateOnly To,
    string TimeZone,
    int Appointments,
    int PreviousPeriodAppointments,
    double? AppointmentsChangePercent,
    RateWithInterval AttendanceRate,
    RateWithInterval NoShowRate,
    int Cancelled,
    double? MedianBookingLeadDays,
    DurationStats TimeToSignedNote,
    int NewPatients,
    IReadOnlyList<Bucket> NewPatientsBySex,
    IReadOnlyList<Bucket> NewPatientsByAgeBand,
    int DocumentsFinalized,
    int DocumentsAutoPrepared,
    Trend? AppointmentTrend,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<HeatCell> ArrivalHeatmap,
    IReadOnlyList<ClinicianLoad> Clinicians,
    double ComputeMilliseconds);

/// <summary>
/// Aggregated management analytics for the caller's facility. Reads only published PHI-free fact views; returns no patient
/// identifiers. All day boundaries use the facility's time zone. Statistics: Wilson 95% intervals for rates, OLS trend.
/// </summary>
public sealed class AnalyticsService(DbSession session, RequestContext context, IStaffDirectory staff, AuditTrail audit, TimeProvider clock)
{
    public const int MaxRangeDays = 366;
    private static readonly string[] Sexes = ["Female", "Male", "Other", "Unknown"];
    private static readonly string[] AgeBands = ["0-4", "5-14", "15-24", "25-44", "45-64", "65+"];
    private static readonly Guid AutomationUserId = Guid.Parse("00000000-0000-7000-8000-00000000a070");

    public async Task<Result<AnalyticsOverview>> OverviewAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var actor = context.RequireActor();
        var facility = await staff.GetFacilityAsync(actor.TenantId, actor.FacilityId, ct);
        var tz = facility?.TimeZone ?? BusinessTime.DefaultTimeZone;
        var zone = BusinessTime.Resolve(tz);
        var today = BusinessTime.LocalDate(clock.GetUtcNow(), zone);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber + 1 > MaxRangeDays)
        {
            return Error.Validation("invalid_range", $"Choose a range of 1 to {MaxRangeDays} days with 'from' before 'to'.");
        }

        var (fromUtc, _) = BusinessTime.DayRangeUtc(start, zone);
        var (_, toUtc) = BusinessTime.DayRangeUtc(end, zone);
        var length = end.DayNumber - start.DayNumber + 1;
        var (prevFromUtc, _) = BusinessTime.DayRangeUtc(start.AddDays(-length), zone);
        var conn = await session.ConnectionAsync(ct);

        NpgsqlCommand Cmd(string sql)
        {
            var c = new NpgsqlCommand(sql, conn, session.Transaction);
            c.Parameters.AddWithValue("t", actor.TenantId);
            c.Parameters.AddWithValue("f", actor.FacilityId);
            c.Parameters.AddWithValue("from", fromUtc);
            c.Parameters.AddWithValue("to", toUtc);
            c.Parameters.AddWithValue("tz", tz);
            return c;
        }

        // Daily series (facility-local dates, zero-filled).
        var daily = new List<(DateOnly D, int Total, int Attended, int NoShow, int Cancelled)>();
        await using (var cmd = Cmd(
            """
            WITH a AS (
              SELECT (starts_at AT TIME ZONE @tz)::date AS d, status
              FROM scheduling.v_appointment_facts
              WHERE tenant_id = @t AND facility_id = @f AND starts_at >= @from AND starts_at < @to)
            SELECT g.d::date,
                   count(a.status)::int,
                   count(*) FILTER (WHERE a.status IN ('CheckedIn', 'Completed'))::int,
                   count(*) FILTER (WHERE a.status = 'NoShow')::int,
                   count(*) FILTER (WHERE a.status = 'Cancelled')::int
            FROM generate_series(@d0::date, @d1::date, interval '1 day') AS g(d)
            LEFT JOIN a ON a.d = g.d::date
            GROUP BY g.d ORDER BY g.d
            """))
        {
            cmd.Parameters.AddWithValue("d0", start);
            cmd.Parameters.AddWithValue("d1", end);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                daily.Add((r.GetFieldValue<DateOnly>(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
            }
        }

        int previousTotal;
        double? leadDays;
        await using (var cmd = Cmd(
            """
            SELECT
              (SELECT count(*)::int FROM scheduling.v_appointment_facts WHERE tenant_id = @t AND facility_id = @f AND starts_at >= @pfrom AND starts_at < @from),
              (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM starts_at - created_at) / 86400.0)
                 FROM scheduling.v_appointment_facts WHERE tenant_id = @t AND facility_id = @f AND starts_at >= @from AND starts_at < @to AND status <> 'Cancelled')
            """))
        {
            cmd.Parameters.AddWithValue("pfrom", prevFromUtc);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            previousTotal = r.GetInt32(0);
            leadDays = r.IsDBNull(1) ? null : Math.Max(0, r.GetDouble(1));
        }

        DurationStats toSign;
        await using (var cmd = Cmd(
            """
            SELECT count(*)::int,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY m), percentile_cont(0.9) WITHIN GROUP (ORDER BY m), avg(m)
            FROM (SELECT extract(epoch FROM signed_at - started_at) / 60.0 AS m
                  FROM clinical.v_encounter_facts
                  WHERE tenant_id = @t AND facility_id = @f AND started_at >= @from AND started_at < @to AND signed_at IS NOT NULL) x
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            toSign = new DurationStats(r.GetInt32(0), N(r, 1), N(r, 2), N(r, 3));
        }

        var heat = new List<HeatCell>();
        await using (var cmd = Cmd(
            """
            SELECT extract(isodow FROM checked_in_at AT TIME ZONE @tz)::int, extract(hour FROM checked_in_at AT TIME ZONE @tz)::int, count(*)::int
            FROM scheduling.v_appointment_facts
            WHERE tenant_id = @t AND facility_id = @f AND checked_in_at >= @from AND checked_in_at < @to
            GROUP BY 1, 2 ORDER BY 1, 2
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                heat.Add(new HeatCell(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            }
        }

        var loads = new List<(Guid Id, int Encounters, int Signed, double? Median)>();
        await using (var cmd = Cmd(
            """
            SELECT practitioner_id, count(*)::int, count(signed_at)::int,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM signed_at - started_at) / 60.0) FILTER (WHERE signed_at IS NOT NULL)
            FROM clinical.v_encounter_facts
            WHERE tenant_id = @t AND facility_id = @f AND started_at >= @from AND started_at < @to
            GROUP BY practitioner_id ORDER BY 2 DESC
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                loads.Add((r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), N(r, 3)));
            }
        }

        var bySex = new List<Bucket>();
        var byAge = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var cmd = Cmd(
            """
            SELECT sex,
                   CASE WHEN age_years_at_registration < 5 THEN '0-4' WHEN age_years_at_registration < 15 THEN '5-14'
                        WHEN age_years_at_registration < 25 THEN '15-24' WHEN age_years_at_registration < 45 THEN '25-44'
                        WHEN age_years_at_registration < 65 THEN '45-64' ELSE '65+' END,
                   count(*)::int
            FROM patients.v_registration_facts
            WHERE tenant_id = @t AND facility_id = @f AND created_at >= @from AND created_at < @to
            GROUP BY 1, 2
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var sexCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct))
            {
                sexCounts[r.GetString(0)] = sexCounts.GetValueOrDefault(r.GetString(0)) + r.GetInt32(2);
                byAge[r.GetString(1)] = byAge.GetValueOrDefault(r.GetString(1)) + r.GetInt32(2);
            }

            bySex.AddRange(Sexes.Select(s => new Bucket(s, sexCounts.GetValueOrDefault(s))));
        }

        int finalized, autoPrepared;
        await using (var cmd = Cmd(
            """
            SELECT count(*) FILTER (WHERE finalized_at >= @from AND finalized_at < @to)::int,
                   count(*) FILTER (WHERE created_by = @auto AND created_at >= @from AND created_at < @to)::int
            FROM documents.v_document_facts WHERE tenant_id = @t AND facility_id = @f AND created_at >= @from - interval '30 days'
            """))
        {
            cmd.Parameters.AddWithValue("auto", AutomationUserId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (finalized, autoPrepared) = (r.GetInt32(0), r.GetInt32(1));
        }

        var totals = daily.Select(d => (double)d.Total).ToList();
        var ma = Statistics.MovingAverage(totals, 7);
        var total = daily.Sum(d => d.Total);
        var attended = daily.Sum(d => d.Attended);
        var noShow = daily.Sum(d => d.NoShow);
        var cancelled = daily.Sum(d => d.Cancelled);
        var resolved = attended + noShow; // appointments with a known attendance outcome
        var clinicians = new List<ClinicianLoad>();
        foreach (var l in loads)
        {
            var p = await staff.GetPractitionerAsync(actor.TenantId, l.Id, ct);
            clinicians.Add(new ClinicianLoad(l.Id, p?.DisplayName ?? "Unknown", l.Encounters, l.Signed, l.Median));
        }

        await audit.RecordAsync(new AuditEntry("analytics.overview.read", "facility_analytics", actor.FacilityId,
            Metadata: new Dictionary<string, string> { ["from"] = start.ToString("O", CultureInfo.InvariantCulture), ["to"] = end.ToString("O", CultureInfo.InvariantCulture) }), ct);
        await session.CommitAsync(ct);

        return new AnalyticsOverview(
            start,
            end,
            tz,
            total,
            previousTotal,
            Statistics.PercentChange(total, previousTotal),
            Rate(attended, resolved),
            Rate(noShow, resolved),
            cancelled,
            leadDays,
            toSign,
            bySex.Sum(b => b.Count),
            bySex,
            AgeBands.Select(b => new Bucket(b, byAge.GetValueOrDefault(b))).ToList(),
            finalized,
            autoPrepared,
            Statistics.LinearTrend(totals),
            daily.Select((d, i) => new DailyPoint(d.D, EthiopianDate.FromGregorian(d.D).ToString(), d.Total, d.Attended, d.NoShow, d.Cancelled, Math.Round(ma[i], 2))).ToList(),
            heat,
            clinicians,
            Math.Round(sw.Elapsed.TotalMilliseconds, 3));
    }

    public async Task<Result<string>> DailyCsvAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var overview = await OverviewAsync(from, to, ct);
        if (!overview.IsSuccess)
        {
            return overview.Error!;
        }

        var sb = new StringBuilder();
        sb.AppendLine("date_gregorian,date_ethiopian,appointments,attended,no_show,cancelled,moving_average_7d");
        foreach (var d in overview.Value.Daily)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{d.Date:yyyy-MM-dd},\"{d.EthiopianDate}\",{d.Appointments},{d.Attended},{d.NoShow},{d.Cancelled},{d.MovingAverage7.ToString(CultureInfo.InvariantCulture)}\n");
        }

        // Export audited in its own transaction (the overview transaction already committed).
        await audit.RecordIndependentAsync(new AuditEntry("analytics.export.csv", "facility_analytics", context.RequireActor().FacilityId), ct);
        return sb.ToString();
    }

    private static RateWithInterval Rate(int k, int n)
    {
        var ci = Statistics.Wilson(k, n);
        return new RateWithInterval(k, n, n == 0 ? null : Math.Round((double)k / n, 4), ci is null ? null : Math.Round(ci.Value.Low, 4), ci is null ? null : Math.Round(ci.Value.High, 4));
    }

    private static double? N(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : Math.Round(r.GetDouble(i), 2);
}
