namespace Serk.Clinic.SharedKernel;

/// <summary>
/// Facility-local civil time helpers. Instants are stored in UTC; "today" and day views are computed in the facility's
/// IANA time zone (default Africa/Addis_Ababa, UTC+3, no DST).
/// </summary>
public static class BusinessTime
{
    public const string DefaultTimeZone = "Africa/Addis_Ababa";

    public static TimeZoneInfo Resolve(string? ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(ianaId) ? DefaultTimeZone : ianaId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);
        }
    }

    public static DateOnly LocalDate(DateTimeOffset instantUtc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instantUtc, zone).DateTime);

    /// <summary>[start, end) of a facility-local civil day, expressed in UTC.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DayRangeUtc(DateOnly day, TimeZoneInfo zone)
    {
        var startLocal = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero));
    }
}
