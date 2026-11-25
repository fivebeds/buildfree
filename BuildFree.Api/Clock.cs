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

    // A moment (stored in UTC) as Pacific clock time, and the Pacific day it falls on.
    public static DateTime ToPacific(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Pacific);
    public static DateOnly PacificDay(DateTime utc) => DateOnly.FromDateTime(ToPacific(utc));

    // When a Pacific day starts, in UTC.
    public static DateTime StartOfDayUtc(DateOnly day) => FromPacific(day.ToDateTime(TimeOnly.MinValue));

    // A time sent to the API, in UTC. Times sent with a time zone (like the web
    // app's "2026-11-25T15:02:00.000Z") keep it; times without one are read as
    // Pacific clock time.
    public static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => FromPacific(d)
    };

    static DateTime FromPacific(DateTime d)
    {
        // A clock time skipped when the clocks spring forward (2:30 am that
        // night) doesn't exist; it's read as an hour later.
        if (Pacific.IsInvalidTime(d)) d = d.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), Pacific);
    }
}
