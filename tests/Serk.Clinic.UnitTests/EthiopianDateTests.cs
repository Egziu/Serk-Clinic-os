using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.UnitTests;

/// <summary>APPT-003 (calendar display). Reference dates are widely published civil observances.</summary>
public class EthiopianDateTests
{
    [Theory]
    [InlineData(2007, 9, 12, 2000, 1, 1)] // Ethiopian Millennium (Enkutatash 2000 E.C.)
    [InlineData(2023, 9, 12, 2016, 1, 1)] // New year after leap year 2015 (Pagume 6)
    [InlineData(2024, 9, 11, 2017, 1, 1)]
    [InlineData(2025, 9, 11, 2018, 1, 1)]
    [InlineData(2026, 9, 11, 2019, 1, 1)]
    [InlineData(2019, 1, 7, 2011, 4, 29)] // Genna (Ethiopian Christmas) = Tahsas 29
    [InlineData(2026, 11, 9, 2019, 2, 30)] // Tikimt 30, 2019
    [InlineData(2023, 9, 11, 2015, 13, 6)] // Pagume 6 in leap year 2015
    [InlineData(1995, 5, 5, 1987, 8, 27)]
    public void Converts_known_gregorian_dates(int gy, int gm, int gd, int ey, int em, int ed)
    {
        var e = EthiopianDate.FromGregorian(new DateOnly(gy, gm, gd));
        Assert.Equal((ey, em, ed), (e.Year, e.Month, e.Day));
        Assert.Equal(new DateOnly(gy, gm, gd), new EthiopianDate(ey, em, ed).ToGregorian());
    }

    [Fact]
    public void Round_trips_every_day_from_1900_to_2100()
    {
        for (var d = new DateOnly(1900, 3, 1); d <= new DateOnly(2100, 12, 31); d = d.AddDays(1))
        {
            Assert.Equal(d, EthiopianDate.FromGregorian(d).ToGregorian());
        }
    }

    [Fact]
    public void Consecutive_days_never_skip_or_repeat()
    {
        var prev = EthiopianDate.FromGregorian(new DateOnly(1999, 1, 1));
        for (var d = new DateOnly(1999, 1, 2); d <= new DateOnly(2031, 1, 1); d = d.AddDays(1))
        {
            var cur = EthiopianDate.FromGregorian(d);
            var expectedNext = prev.Day < (prev.Month == 13 ? (EthiopianDate.IsLeapYear(prev.Year) ? 6 : 5) : 30)
                ? (prev.Year, prev.Month, prev.Day + 1)
                : prev.Month < 13 ? (prev.Year, prev.Month + 1, 1) : (prev.Year + 1, 1, 1);
            Assert.Equal(expectedNext, (cur.Year, cur.Month, cur.Day));
            prev = cur;
        }
    }

    [Fact]
    public void Rejects_invalid_pagume_day()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EthiopianDate(2016, 13, 6)); // 2016 is not leap
        _ = new EthiopianDate(2015, 13, 6);
    }

    [Fact]
    public void Display_is_explicitly_labelled()
    {
        Assert.Equal("Tikimt 30, 2019 E.C.", EthiopianDate.FromGregorian(new DateOnly(2026, 11, 9)).ToString());
    }

    [Fact]
    public void Facility_day_range_uses_addis_ababa_offset()
    {
        var zone = BusinessTime.Resolve("Africa/Addis_Ababa");
        var (start, end) = BusinessTime.DayRangeUtc(new DateOnly(2026, 11, 9), zone);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 21, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero), end);
        Assert.Equal(new DateOnly(2026, 11, 10), BusinessTime.LocalDate(new DateTimeOffset(2026, 11, 9, 22, 30, 0, TimeSpan.Zero), zone));
    }
}
