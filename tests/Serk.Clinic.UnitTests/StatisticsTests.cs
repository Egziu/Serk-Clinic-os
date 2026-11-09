using Serk.Clinic.Analytics.Domain;

namespace Serk.Clinic.UnitTests;

public class StatisticsTests
{
    [Fact(DisplayName = "Wilson 95% interval matches published reference values")]
    public void Wilson_reference_values()
    {
        // Reference: Newcombe (1998) Table I / standard calculators: 81/263 -> 0.2553-0.3662 (rounded).
        var ci = Statistics.Wilson(81, 263)!.Value;
        Assert.Equal(0.2553, ci.Low, 3);
        Assert.Equal(0.3662, ci.High, 3);
        // 0/10 -> [0, 0.2775]
        var zero = Statistics.Wilson(0, 10)!.Value;
        Assert.Equal(0, zero.Low, 6);
        Assert.Equal(0.2775, zero.High, 3);
        Assert.Null(Statistics.Wilson(0, 0));
    }

    [Fact]
    public void Wilson_is_bounded_and_contains_point_estimate()
    {
        for (var n = 1; n <= 60; n++)
        {
            for (var k = 0; k <= n; k++)
            {
                var ci = Statistics.Wilson(k, n)!.Value;
                var p = (double)k / n;
                Assert.InRange(ci.Low, 0, p + 1e-12);
                Assert.InRange(ci.High, p - 1e-12, 1);
            }
        }
    }

    [Fact]
    public void Linear_trend_recovers_exact_line_and_r2()
    {
        var y = Enumerable.Range(0, 10).Select(i => 3 + (2.5 * i)).ToList();
        var t = Statistics.LinearTrend(y)!;
        Assert.Equal(2.5, t.SlopePerDay, 9);
        Assert.Equal(3, t.Intercept, 9);
        Assert.Equal(1, t.RSquared, 9);
        Assert.Equal(0, Statistics.LinearTrend([5, 5, 5])!.RSquared);
        Assert.Null(Statistics.LinearTrend([1]));
    }

    [Fact]
    public void Linear_trend_known_noisy_example()
    {
        // y = [2,4,5,4,5] over x = 0..4: slope 0.6, intercept 2.8, R^2 = 0.6
        var t = Statistics.LinearTrend([2, 4, 5, 4, 5])!;
        Assert.Equal(0.6, t.SlopePerDay, 9);
        Assert.Equal(2.8, t.Intercept, 9);
        Assert.Equal(0.6, t.RSquared, 9);
    }

    [Fact]
    public void Moving_average_trailing_window()
    {
        Assert.Equal([1, 1.5, 2, 3, 4], Statistics.MovingAverage([1, 2, 3, 4, 5], 3));
        Assert.Null(Statistics.PercentChange(5, 0));
        Assert.Equal(50, Statistics.PercentChange(15, 10));
    }
}
