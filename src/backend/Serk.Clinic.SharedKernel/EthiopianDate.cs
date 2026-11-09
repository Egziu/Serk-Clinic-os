using System.Globalization;

namespace Serk.Clinic.SharedKernel;

/// <summary>
/// Ethiopian (Ge'ez) calendar date used for DISPLAY ONLY. Machine timestamps are stored in UTC and civil dates as Gregorian
/// <see cref="DateOnly"/>. Conversion uses Julian Day Numbers with the Amete Mihret epoch (Meskerem 1, 1 E.C. = JDN 1724221).
/// Calendar: 12 months of 30 days + Pagume (month 13) with 5 days, 6 in a leap year (year % 4 == 3).
/// </summary>
public readonly record struct EthiopianDate
{
    // JDN offset used by the standard Amete Mihret conversion (Meskerem 1, year 1 E.C. = JDN 1724221 = offset + 365).
    private const int JdnOffset = 1723856;

    public static readonly IReadOnlyList<string> MonthNamesLatin =
    [
        "Meskerem", "Tikimt", "Hidar", "Tahsas", "Tir", "Yekatit", "Megabit", "Miyazya", "Ginbot", "Sene", "Hamle", "Nehase", "Pagume",
    ];

    public EthiopianDate(int year, int month, int day)
    {
        if (year < 1 || month is < 1 or > 13 || day < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Invalid Ethiopian date component.");
        }

        var maxDay = month == 13 ? (IsLeapYear(year) ? 6 : 5) : 30;
        if (day > maxDay)
        {
            throw new ArgumentOutOfRangeException(nameof(day), "Day exceeds the length of the Ethiopian month.");
        }

        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }
    public int Month { get; }
    public int Day { get; }

    public static bool IsLeapYear(int ethiopianYear) => ethiopianYear % 4 == 3;

    public static EthiopianDate FromGregorian(DateOnly date)
    {
        var jdn = GregorianToJdn(date.Year, date.Month, date.Day);
        var r = (jdn - JdnOffset) % 1461;
        var n = (r % 365) + (365 * (r / 1460));
        var year = (4 * ((jdn - JdnOffset) / 1461)) + (r / 365) - (r / 1460);
        var month = (n / 30) + 1;
        var day = (n % 30) + 1;
        return new EthiopianDate(year, month, day);
    }

    public DateOnly ToGregorian()
    {
        var jdn = JdnOffset + 365 + (365 * (Year - 1)) + (Year / 4) + (30 * Month) + Day - 31;
        return JdnToGregorian(jdn);
    }

    /// <summary>Unambiguous display, always labelled with the calendar, e.g. "Tikimt 30, 2019 E.C.".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{MonthNamesLatin[Month - 1]} {Day}, {Year} E.C.");

    private static int GregorianToJdn(int y, int m, int d)
    {
        var a = (14 - m) / 12;
        var yy = y + 4800 - a;
        var mm = m + (12 * a) - 3;
        return d + (((153 * mm) + 2) / 5) + (365 * yy) + (yy / 4) - (yy / 100) + (yy / 400) - 32045;
    }

    private static DateOnly JdnToGregorian(int jdn)
    {
        var a = jdn + 32044;
        var b = ((4 * a) + 3) / 146097;
        var c = a - (146097 * b / 4);
        var d = ((4 * c) + 3) / 1461;
        var e = c - (1461 * d / 4);
        var m = ((5 * e) + 2) / 153;
        var day = e - (((153 * m) + 2) / 5) + 1;
        var month = m + 3 - (12 * (m / 10));
        var year = (100 * b) + d - 4800 + (m / 10);
        return new DateOnly(year, month, day);
    }
}
