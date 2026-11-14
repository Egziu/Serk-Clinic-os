using System.Globalization;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Analytics.Domain;

public enum InsightSeverity
{
    Info,
    Attention,
    Urgent,
}

/// <summary>
/// One explainable observation: what was seen (<see cref="Detail"/> always states the numbers and the rule), how serious,
/// and where to act. Rule-based and deterministic — no machine learning, never a clinical judgement.
/// </summary>
public sealed record Insight(string Code, InsightSeverity Severity, string Area, string Title, string Detail, string Rule, string? ActionLabel, string? ActionPath);

public sealed record SetupItem(string Code, string Label, bool Done, string Hint, string Path);

/// <summary>Aggregated, PHI-free facts for one facility, gathered by <c>InsightsService</c>.</summary>
public sealed record InsightFacts
{
    public required DateOnly Today { get; init; }

    public required string Currency { get; init; }

    public IReadOnlyList<SetupItem> Setup { get; init; } = [];

    public DateTimeOffset? LastBackupAt { get; init; }

    public DateTimeOffset? LastVerifiedBackupAt { get; init; }

    public DateTimeOffset Now { get; init; }

    public int RecentAppointments { get; init; }

    public int RecentNoShows { get; init; }

    public int BaselineAppointments { get; init; }

    public int BaselineNoShows { get; init; }

    public decimal? YesterdayRevenue { get; init; }

    public decimal? SameWeekdayAverageRevenue { get; init; }

    public int OverdueInvoices { get; init; }

    public decimal OverdueAmount { get; init; }

    public int StaleOpenDrawers { get; init; }

    public IReadOnlyList<DateOnly> UnclosedDays { get; init; } = [];

    public IReadOnlyList<(string Cashier, int Count)> FrequentVariances { get; init; } = [];

    public int TomorrowBooked { get; init; }

    public double AverageDailyBooked { get; init; }
}

/// <summary>The rules. Thresholds are constants here so they are reviewable and tested.</summary>
public static class InsightRules
{
    public const int NoShowMinimumSample = 10;
    public const double NoShowRiseThreshold = 0.10;
    public const double NoShowFloor = 0.15;
    public const double RevenueChangeThreshold = 0.30;
    public const int OverdueInvoiceDays = 7;
    public const int BackupMaxAgeDays = 7;
    public const int VarianceRepeatThreshold = 3;
    public const double BusyDayFactor = 1.5;

    public static IReadOnlyList<Insight> Evaluate(InsightFacts f, Func<string, bool> can)
    {
        var list = new List<Insight>();
        var inv = CultureInfo.InvariantCulture;

        if (can(Capabilities.BackupManage))
        {
            if (f.LastBackupAt is null)
            {
                list.Add(new("backup.never", InsightSeverity.Urgent, "Data safety", "No backup has been taken",
                    "This organisation has no encrypted backup on record. If the server or database were lost, data could not be recovered by you.",
                    "Urgent when no backup exists.", "Create a backup", "/data"));
            }
            else if ((f.Now - f.LastBackupAt.Value).TotalDays > BackupMaxAgeDays)
            {
                list.Add(new("backup.stale", InsightSeverity.Attention, "Data safety", "Backup is out of date",
                    $"The last backup was taken {(int)(f.Now - f.LastBackupAt.Value).TotalDays} days ago.",
                    $"Attention when the newest backup is older than {BackupMaxAgeDays} days.", "Create a backup", "/data"));
            }

            if (f.LastBackupAt is not null && (f.LastVerifiedBackupAt is null || f.LastVerifiedBackupAt < f.LastBackupAt))
            {
                list.Add(new("backup.unverified", InsightSeverity.Info, "Data safety", "Latest backup not verified yet",
                    "A backup only counts once it has been opened and checked. Verify the newest file with its passphrase.",
                    "Info when the newest backup has not been verified since it was created.", "Verify a backup", "/data"));
            }
        }

        if (can(Capabilities.BrandingManage))
        {
            var missing = f.Setup.Where(s => !s.Done).ToList();
            if (missing.Count > 0)
            {
                list.Add(new("setup.incomplete", missing.Any(m => m.Code is "prices" or "name") ? InsightSeverity.Attention : InsightSeverity.Info, "Setup",
                    $"Clinic setup {f.Setup.Count - missing.Count} of {f.Setup.Count} complete",
                    "Still to do: " + string.Join(", ", missing.Select(m => m.Label)) + ".",
                    "Lists every unfinished setup step; attention when the clinic name or price list is missing.", "Open setup", "/setup"));
            }
        }

        if (can(Capabilities.AnalyticsRead) || can(Capabilities.AppointmentRead))
        {
            if (f.RecentAppointments >= NoShowMinimumSample)
            {
                var recent = (double)f.RecentNoShows / f.RecentAppointments;
                var baseline = f.BaselineAppointments > 0 ? (double)f.BaselineNoShows / f.BaselineAppointments : 0;
                if (recent >= NoShowFloor && recent - baseline >= NoShowRiseThreshold)
                {
                    list.Add(new("schedule.no-shows-rising", InsightSeverity.Attention, "Schedule", "More patients are missing appointments",
                        $"No-shows in the last 7 days: {f.RecentNoShows} of {f.RecentAppointments} ({recent.ToString("P0", inv)}), compared with {baseline.ToString("P0", inv)} in the 4 weeks before.",
                        $"Attention when the 7-day rate is at least {NoShowFloor:P0} and {NoShowRiseThreshold:P0} points above the previous 4 weeks (minimum {NoShowMinimumSample} appointments).",
                        "Review reminders", "/dashboard"));
                }
            }

            if (f.AverageDailyBooked >= 3 && f.TomorrowBooked >= f.AverageDailyBooked * BusyDayFactor)
            {
                list.Add(new("schedule.busy-tomorrow", InsightSeverity.Info, "Schedule", "Tomorrow is busier than usual",
                    $"{f.TomorrowBooked} appointments booked for tomorrow against an average of {f.AverageDailyBooked.ToString("0.#", inv)} per day over the last 4 weeks.",
                    $"Info when tomorrow has at least {BusyDayFactor}× the 4-week daily average.", "Open reception", "/"));
            }
        }

        if (can(Capabilities.FinanceRead))
        {
            if (f.StaleOpenDrawers > 0)
            {
                list.Add(new("finance.stale-drawers", InsightSeverity.Urgent, "Money", "Cash drawers left open from earlier days",
                    $"{f.StaleOpenDrawers} drawer session(s) opened before today are still open; cash for those days is not counted.",
                    "Urgent when a drawer opened on an earlier business day is still open.", "Open drawers", "/finance"));
            }

            if (f.UnclosedDays.Count > 0)
            {
                list.Add(new("finance.unclosed-days", InsightSeverity.Attention, "Money", "Business days without a locked close",
                    "Payments exist but no locked close for: " + string.Join(", ", f.UnclosedDays.Select(d => d.ToString("yyyy-MM-dd", inv))) + ".",
                    "Attention for any of the last 14 days (before today) with payments and no locked daily close.", "Close the day", "/finance"));
            }

            if (f.OverdueInvoices > 0)
            {
                list.Add(new("finance.overdue-invoices", InsightSeverity.Attention, "Money", "Unpaid invoices older than a week",
                    $"{f.OverdueInvoices} issued invoice(s) still owe {f.OverdueAmount.ToString("N2", inv)} {f.Currency}.",
                    $"Attention for issued invoices with a balance, issued more than {OverdueInvoiceDays} days ago.", "Export for follow-up", "/data"));
            }

            foreach (var (cashier, count) in f.FrequentVariances)
            {
                list.Add(new("finance.repeated-variance", InsightSeverity.Attention, "Money", "Repeated cash differences",
                    $"{cashier}: {count} drawer counts with a difference in the last 30 days.",
                    $"Attention at {VarianceRepeatThreshold} or more counted drawers with a non-zero difference in 30 days for one cashier. This is a pattern to look at, not proof of wrongdoing.",
                    "Review drawers", "/finance"));
            }

            if (f.YesterdayRevenue is { } y && f.SameWeekdayAverageRevenue is { } avg && avg > 0)
            {
                var change = (double)((y - avg) / avg);
                if (Math.Abs(change) >= RevenueChangeThreshold)
                {
                    list.Add(new(change < 0 ? "finance.revenue-down" : "finance.revenue-up", change < 0 ? InsightSeverity.Attention : InsightSeverity.Info, "Money",
                        change < 0 ? "Yesterday's takings were lower than usual" : "Yesterday's takings were higher than usual",
                        $"Verified payments yesterday: {y.ToString("N2", inv)} {f.Currency}; average for the same weekday over the previous 4 weeks: {avg.ToString("N2", inv)} {f.Currency} ({change.ToString("+0%;-0%", inv)}).",
                        $"Shown when yesterday differs by at least {RevenueChangeThreshold:P0} from the same weekday's 4-week average.", "Open finance", "/finance"));
                }
            }
        }

        return list.OrderByDescending(i => i.Severity).ThenBy(i => i.Area, StringComparer.Ordinal).ToList();
    }
}
