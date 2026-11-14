using Npgsql;
using Serk.Clinic.Analytics.Domain;
using Serk.Clinic.Identity.Contracts;
using Serk.Clinic.Platform.Context;
using Serk.Clinic.Platform.Data;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Analytics.Application;

public sealed record InsightsDto(DateTimeOffset GeneratedAt, IReadOnlyList<Insight> Insights, IReadOnlyList<SetupItem> Setup, int SetupPercent);

/// <summary>
/// Gathers PHI-free facts from published views and applies <see cref="InsightRules"/>. Each caller only sees insights
/// for areas their permissions cover.
/// </summary>
public sealed class InsightsService(DbSession session, RequestContext context, IStaffDirectory staff, TimeProvider clock)
{
    public async Task<InsightsDto> GetAsync(CancellationToken ct)
    {
        var actor = context.RequireActor();
        var facility = await staff.GetFacilityAsync(actor.TenantId, actor.FacilityId, ct);
        var zone = BusinessTime.Resolve(facility?.TimeZone ?? BusinessTime.DefaultTimeZone);
        var now = clock.GetUtcNow();
        var today = BusinessTime.LocalDate(now, zone);
        DateTimeOffset Start(DateOnly d) => BusinessTime.DayRangeUtc(d, zone).StartUtc;

        var (setup, currency) = await SetupAsync(actor.FacilityId, ct);
        var facts = new InsightFacts { Today = today, Now = now, Currency = currency, Setup = setup };

        await using (var cmd = await Cmd(
            """
            SELECT max(created_at) FILTER (WHERE action = 'Created'), max(created_at) FILTER (WHERE action = 'Verified') FROM platform.backup_log
            """, ct))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            facts = facts with
            {
                LastBackupAt = r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0),
                LastVerifiedBackupAt = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1),
            };
        }

        await using (var cmd = await Cmd(
            """
            SELECT count(*) FILTER (WHERE starts_at >= @r0 AND starts_at < @r1 AND status <> 'Cancelled')::int,
                   count(*) FILTER (WHERE starts_at >= @r0 AND starts_at < @r1 AND status = 'NoShow')::int,
                   count(*) FILTER (WHERE starts_at >= @b0 AND starts_at < @r0 AND status <> 'Cancelled')::int,
                   count(*) FILTER (WHERE starts_at >= @b0 AND starts_at < @r0 AND status = 'NoShow')::int,
                   count(*) FILTER (WHERE starts_at >= @t0 AND starts_at < @t1 AND status <> 'Cancelled')::int,
                   count(*) FILTER (WHERE starts_at >= @a0 AND starts_at < @r1 AND status <> 'Cancelled')::int,
                   count(DISTINCT (starts_at AT TIME ZONE @tz)::date) FILTER (WHERE starts_at >= @a0 AND starts_at < @r1 AND status <> 'Cancelled')::int
            FROM scheduling.v_appointment_facts WHERE tenant_id = @t AND facility_id = @f AND starts_at >= @b0 AND starts_at < @t1
            """, ct))
        {
            cmd.Parameters.AddWithValue("r0", Start(today.AddDays(-7)));
            cmd.Parameters.AddWithValue("r1", Start(today));
            cmd.Parameters.AddWithValue("b0", Start(today.AddDays(-35)));
            cmd.Parameters.AddWithValue("t0", Start(today.AddDays(1)));
            cmd.Parameters.AddWithValue("t1", Start(today.AddDays(2)));
            cmd.Parameters.AddWithValue("a0", Start(today.AddDays(-28)));
            cmd.Parameters.AddWithValue("tz", zone.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var days = r.GetInt32(6);
            facts = facts with
            {
                RecentAppointments = r.GetInt32(0),
                RecentNoShows = r.GetInt32(1),
                BaselineAppointments = r.GetInt32(2),
                BaselineNoShows = r.GetInt32(3),
                TomorrowBooked = r.GetInt32(4),
                AverageDailyBooked = days == 0 ? 0 : (double)r.GetInt32(5) / days,
            };
        }

        if (actor.Has(Capabilities.FinanceRead))
        {
            facts = await FinanceFactsAsync(facts, today, now, ct);
        }

        var insights = InsightRules.Evaluate(facts, actor.Has);
        var percent = setup.Count == 0 ? 100 : (int)Math.Round(100.0 * setup.Count(s => s.Done) / setup.Count);
        return new InsightsDto(now, insights, actor.Has(Capabilities.BrandingManage) ? setup : [], percent);
    }

    private async Task<InsightFacts> FinanceFactsAsync(InsightFacts facts, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        var yesterday = today.AddDays(-1);
        await using (var cmd = await Cmd(
            """
            SELECT business_date, sum(amount - refunded_amount) FROM finance.v_payment_facts
            WHERE tenant_id = @t AND facility_id = @f AND status = 'Verified' AND business_date = ANY(@days)
            GROUP BY business_date
            """, ct))
        {
            var days = new[] { yesterday, yesterday.AddDays(-7), yesterday.AddDays(-14), yesterday.AddDays(-21), yesterday.AddDays(-28) };
            cmd.Parameters.AddWithValue("days", days);
            var byDay = new Dictionary<DateOnly, decimal>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                byDay[r.GetFieldValue<DateOnly>(0)] = r.GetDecimal(1);
            }

            var previous = days.Skip(1).Where(byDay.ContainsKey).Select(d => byDay[d]).Where(v => v > 0).ToList();
            facts = facts with
            {
                YesterdayRevenue = byDay.GetValueOrDefault(yesterday),
                SameWeekdayAverageRevenue = previous.Count >= 2 ? Math.Round(previous.Average(), 2) : null,
            };
        }

        await using (var cmd = await Cmd(
            """
            SELECT
              (SELECT count(*)::int FROM finance.v_invoice_facts WHERE tenant_id = @t AND facility_id = @f AND status IN ('Issued', 'PartiallyPaid') AND total_amount > paid_amount AND issued_at < @overdue),
              (SELECT coalesce(sum(total_amount - paid_amount), 0) FROM finance.v_invoice_facts WHERE tenant_id = @t AND facility_id = @f AND status IN ('Issued', 'PartiallyPaid') AND total_amount > paid_amount AND issued_at < @overdue),
              (SELECT count(*)::int FROM finance.v_session_facts WHERE tenant_id = @t AND facility_id = @f AND status = 'Open' AND business_date < @today)
            """, ct))
        {
            cmd.Parameters.AddWithValue("overdue", now.AddDays(-InsightRules.OverdueInvoiceDays));
            cmd.Parameters.AddWithValue("today", today);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            facts = facts with { OverdueInvoices = r.GetInt32(0), OverdueAmount = r.GetDecimal(1), StaleOpenDrawers = r.GetInt32(2) };
        }

        var unclosed = new List<DateOnly>();
        await using (var cmd = await Cmd(
            """
            SELECT DISTINCT p.business_date FROM finance.v_payment_facts p
            WHERE p.tenant_id = @t AND p.facility_id = @f AND p.status = 'Verified' AND p.business_date >= @from AND p.business_date < @today
              AND NOT EXISTS (SELECT 1 FROM finance.v_close_facts c WHERE c.tenant_id = @t AND c.facility_id = @f AND c.business_date = p.business_date AND c.status = 'Locked')
            ORDER BY 1
            """, ct))
        {
            cmd.Parameters.AddWithValue("from", today.AddDays(-14));
            cmd.Parameters.AddWithValue("today", today);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                unclosed.Add(r.GetFieldValue<DateOnly>(0));
            }
        }

        var variances = new List<(string, int)>();
        await using (var cmd = await Cmd(
            """
            SELECT coalesce(s.display_name, 'Unknown cashier'), count(*)::int
            FROM finance.v_session_facts v
            LEFT JOIN (SELECT DISTINCT user_id, display_name FROM identity.v_staff_reference) s ON s.user_id = v.cashier_user_id
            WHERE v.tenant_id = @t AND v.facility_id = @f AND v.counted_at >= @since AND v.variance <> 0
            GROUP BY 1 HAVING count(*) >= @min ORDER BY 2 DESC, 1
            """, ct))
        {
            cmd.Parameters.AddWithValue("since", now.AddDays(-30));
            cmd.Parameters.AddWithValue("min", InsightRules.VarianceRepeatThreshold);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                variances.Add((r.GetString(0), r.GetInt32(1)));
            }
        }

        return facts with { UnclosedDays = unclosed, FrequentVariances = variances };
    }

    private async Task<(IReadOnlyList<SetupItem> Items, string Currency)> SetupAsync(Guid facilityId, CancellationToken ct)
    {
        await using var cmd = await Cmd(
            """
            SELECT c.has_name, c.has_motto, c.has_address, c.has_phone, c.has_licence, c.has_tax_id, c.has_hours, c.has_logo, c.has_stamp,
                   c.has_document_printer, c.has_slip_printer, c.currency,
                   coalesce(f.service_count, 0), coalesce(f.active_recipients, 0),
                   EXISTS (SELECT 1 FROM platform.backup_log WHERE action = 'Created')
            FROM configuration.v_setup_facts c LEFT JOIN finance.v_setup_facts f ON f.tenant_id = c.tenant_id
            WHERE c.facility_id = @f
            """, ct);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return ([], "ETB");
        }

        bool B(int i) => r.GetBoolean(i);
        var items = new List<SetupItem>
        {
            new("name", "Clinic name", B(0), "The name printed on every document and receipt.", "/setup"),
            new("motto", "Motto", B(1), "A short line under your name, e.g. 'Gentle care, bright smiles'.", "/setup"),
            new("address", "Address", B(2), "Street, city and region for letterheads.", "/setup"),
            new("phone", "Phone", B(3), "Patients call this number from their slips.", "/setup"),
            new("licence", "Facility licence number", B(4), "Shown on clinical documents.", "/setup"),
            new("tax", "Tax ID (TIN)", B(5), "Shown on receipts.", "/setup"),
            new("hours", "Opening hours", B(6), "Printed on appointment slips.", "/setup"),
            new("logo", "Logo", B(7), "PNG or JPEG, shown on documents, slips and the app.", "/setup"),
            new("stamp", "Official stamp", B(8), "Applied to issued documents.", "/setup"),
            new("document-printer", "Document printer", B(9), "A4/A5 printer for clinical documents.", "/setup"),
            new("slip-printer", "Slip printer", B(10), "Thermal or A5 printer for appointment slips.", "/setup"),
            new("prices", "Price list", r.GetInt32(12) > 0, "Services and prices used on invoices.", "/finance"),
            new("recipients", "Owner report recipient", r.GetInt32(13) > 0, "Who receives the daily close summary.", "/owner"),
            new("backup", "First backup", B(14), "An encrypted copy of all your data, kept by you.", "/data"),
        };
        return (items, r.GetString(11));
    }

    private async Task<NpgsqlCommand> Cmd(string sql, CancellationToken ct)
    {
        var actor = context.RequireActor();
        var cmd = await session.CommandAsync(sql, ct);
        cmd.Parameters.AddWithValue("t", actor.TenantId);
        cmd.Parameters.AddWithValue("f", actor.FacilityId);
        return cmd;
    }
}
