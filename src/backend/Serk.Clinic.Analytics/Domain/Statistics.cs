namespace Serk.Clinic.Analytics.Domain;

public readonly record struct Interval(double Low, double High);

public sealed record Trend(double SlopePerDay, double Intercept, double RSquared, int N);

/// <summary>Small, dependency-free statistics used by the management dashboard (deterministic, unit-tested).</summary>
public static class Statistics
{
    /// <summary>
    /// Wilson score interval for a binomial proportion (default 95%, z = 1.959964). Preferred over the normal (Wald)
    /// interval for small n and proportions near 0 or 1. Returns null when n = 0.
    /// </summary>
    public static Interval? Wilson(int successes, int n, double z = 1.959964)
    {
        if (n <= 0)
        {
            return null;
        }

        var p = (double)successes / n;
        var z2 = z * z;
        var denom = 1 + (z2 / n);
        var centre = (p + (z2 / (2 * n))) / denom;
        var half = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4.0 * n * n))) / denom;
        return new Interval(Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>Ordinary least squares fit y = a + b·x for x = 0..n-1. R² is 0 when variance is zero.</summary>
    public static Trend? LinearTrend(IReadOnlyList<double> y)
    {
        var n = y.Count;
        if (n < 2)
        {
            return null;
        }

        var meanX = (n - 1) / 2.0;
        var meanY = y.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = i - meanX;
            var dy = y[i] - meanY;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }

        var slope = sxy / sxx;
        var intercept = meanY - (slope * meanX);
        var r2 = syy == 0 ? 0 : (sxy * sxy) / (sxx * syy);
        return new Trend(slope, intercept, r2, n);
    }

    /// <summary>Trailing moving average; the first window-1 points average what is available (never NaN).</summary>
    public static IReadOnlyList<double> MovingAverage(IReadOnlyList<double> y, int window)
    {
        var result = new double[y.Count];
        double sum = 0;
        for (var i = 0; i < y.Count; i++)
        {
            sum += y[i];
            if (i >= window)
            {
                sum -= y[i - window];
            }

            result[i] = sum / Math.Min(i + 1, window);
        }

        return result;
    }

    /// <summary>Relative change in percent, null when the baseline is zero.</summary>
    public static double? PercentChange(double current, double previous) =>
        previous == 0 ? null : (current - previous) / previous * 100.0;
}
