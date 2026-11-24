namespace BuildFree.Api;

// Today's date where BuildFree's contractors work (Pacific time). Servers run
// on UTC, which is already tomorrow by late afternoon in San Diego, so a daily
// log added in the evening would otherwise get tomorrow's date.
public static class Clock
{
    static readonly TimeZoneInfo Pacific = FindPacific();

    static TimeZoneInfo FindPacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    public static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific));
}
