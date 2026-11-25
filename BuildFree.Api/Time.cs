using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildFree.Api;

// The time clock: crews clock in and out of jobs from their phones, and the
// office reviews the hours, fixes mistakes, approves them and downloads them
// for payroll.
//
// Everyone clocks themselves in and out and sees their own time. The owner and
// office see everyone's, add or fix time, approve it and download it. Approved
// time is locked until it's unapproved. Times are stored in UTC; days and
// weeks are Pacific time, and a whole shift counts on the day it started.
public static class TimeClock
{
    const int MaxRangeDays = 62;       // one timesheet request covers up to about 2 months
    const int MaxShiftHours = 24;
    const int MaxNotes = 1000;
    const int MaxApprove = 1000;
    static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(5);   // phone clocks run a little fast
    static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

    public static void MapTimeClock(this WebApplication app)
    {
        var time = app.MapGroup("/api/time").RequireAuthorization();

        // ---------- Clocking in and out (everyone, for themselves) ----------

        // Am I on the clock? { entry } is the open entry, or null.
        time.MapGet("/current", async (HttpContext ctx, AppDbContext db) =>
            Results.Ok(new { entry = await Open(db, Me(ctx)) }));

        time.MapPost("/clock-in", async (ClockInRequest r, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var (job, bad) = await PickJob(db, me, r.ProjectId);
            if (bad is not null) return bad;
            if (Clean(r.Notes) is { Length: > MaxNotes }) return Bad("Notes are too long.");
            if (await Open(db, me) is TimeEntry open)
                return Results.Conflict(new { error = $"You're already clocked in to {open.JobName}. Clock out or switch jobs first." });
            var entry = Start(me, job!, DateTime.UtcNow, r.Notes);
            db.TimeEntries.Add(entry);
            Feed.Add(db, me, "time", "clocked in", job, officeOnly: true);
            return await SaveClock(db, () => Results.Ok(entry));
        });

        time.MapPost("/clock-out", async (ClockOutRequest r, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var open = await Open(db, me);
            if (open is null) return Results.Conflict(new { error = "You're not clocked in." });
            var now = DateTime.UtcNow;
            if (CheckBreak(r.BreakMinutes, open.ClockIn, now) is IResult badBreak) return badBreak;
            if (Clean(r.Notes) is { Length: > MaxNotes }) return Bad("Notes are too long.");
            open.ClockOut = now;
            open.BreakMinutes = r.BreakMinutes ?? 0;
            if (Clean(r.Notes) is string notes) open.Notes = notes;
            open.UpdatedAt = now;
            await Feed.Add(db, me, "time", $"clocked out ({Duration(open.Minutes ?? 0)})", open.ProjectId ?? Guid.Empty, officeOnly: true);
            await db.SaveChangesAsync();
            return Results.Ok(open);
        });

        // Clocks out of the current job and into another one at the same moment.
        time.MapPost("/switch", async (ClockInRequest r, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var open = await Open(db, me);
            if (open is null) return Results.Conflict(new { error = "You're not clocked in. Clock in instead." });
            var (job, bad) = await PickJob(db, me, r.ProjectId);
            if (bad is not null) return bad;
            if (open.ProjectId == job!.Id) return Bad("You're already clocked in to that job.");
            var now = DateTime.UtcNow;
            if (CheckBreak(r.BreakMinutes, open.ClockIn, now) is IResult badBreak) return badBreak;
            if (Clean(r.Notes) is { Length: > MaxNotes }) return Bad("Notes are too long.");

            // Two saves in one transaction: the old entry has to be closed
            // before the new one opens, or the one-open-entry rule refuses it.
            await using var tx = await db.Database.BeginTransactionAsync();
            open.ClockOut = now;
            open.BreakMinutes = r.BreakMinutes ?? 0;
            open.UpdatedAt = now;
            await db.SaveChangesAsync();
            var entry = Start(me, job, now, r.Notes);
            db.TimeEntries.Add(entry);
            Feed.Add(db, me, "time", $"switched to this job from {open.JobName}", job, officeOnly: true);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Results.Conflict(new { error = "You're already clocked in." });   // nothing is kept
            }
            await tx.CommitAsync();
            return Results.Ok(entry);
        });

        // ---------- Timesheets ----------

        // Time between two days (this week, Monday to Sunday, by default),
        // oldest first, with each person's totals. Field crew get only their own.
        time.MapGet("/", async (DateOnly? from, DateOnly? to, Guid? userId, Guid? projectId, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            if (!Range(from, to, out var start, out var end, out var rangeError)) return Bad(rangeError);
            var list = await Query(db, me, start, end, userId, projectId).ToListAsync();
            return Results.Ok(new { from = start, to = end, entries = list, people = Totals(list), jobs = JobTotals(list) });
        });

        // Who's on the clock right now, across the company.
        time.MapGet("/now", async (HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            return Results.Ok(await db.TimeEntries.Where(e => e.AccountId == me.AccountId && e.ClockOut == null)
                .OrderBy(e => e.ClockIn).ToListAsync());
        }).AddEndpointFilter(OfficeOnly);

        // The same time as a spreadsheet (CSV): one row per shift, then each
        // person's totals with California-style overtime.
        time.MapGet("/export", async (DateOnly? from, DateOnly? to, Guid? userId, Guid? projectId, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            if (!Range(from, to, out var start, out var end, out var rangeError)) return Bad(rangeError);
            var list = await Query(db, me, start, end, userId, projectId).ToListAsync();
            var csv = new StringBuilder();
            Row(csv, "Date", "Person", "Job number", "Job", "Clock in", "Clock out", "Break (min)", "Hours", "Approved", "Notes");
            var jobNumbers = await db.Projects.Where(p => p.AccountId == me.AccountId)
                .ToDictionaryAsync(p => p.Id, p => p.Number);
            foreach (var e in list)
            {
                var number = e.ProjectId is Guid pid && jobNumbers.GetValueOrDefault(pid) is int n ? n.ToString(US) : "";
                Row(csv, e.Day.ToString("yyyy-MM-dd", US), e.UserName, number, e.JobName,
                    Clock.ToPacific(e.ClockIn).ToString("yyyy-MM-dd HH:mm", US),
                    e.ClockOut is DateTime o ? Clock.ToPacific(o).ToString("yyyy-MM-dd HH:mm", US) : "still on the clock",
                    e.BreakMinutes.ToString(US), e.Minutes is int m ? Hours(m) : "", e.Approved ? "Yes" : "No", e.Notes ?? "");
            }
            csv.AppendLine();
            Row(csv, "Person", "Total hours", "Regular", "Overtime", "Double time", "Approved hours");
            foreach (var p in Totals(list))
                Row(csv, p.Name, Hours(p.Minutes), Hours(p.Regular), Hours(p.Overtime), Hours(p.DoubleTime), Hours(p.Approved));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"timesheet-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.csv");
        }).AddEndpointFilter(OfficeOnly);

        // ---------- Adding, fixing and approving time (owner and office) ----------

        time.MapPost("/", async (TimeEntryRequest r, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var (bad, who, job) = await Check(db, me, r, null);
            if (bad is not null) return bad;
            var entry = new TimeEntry { AccountId = me.AccountId, UserId = who!.Id, UserName = who.Name };
            Apply(entry, r, job, me);
            db.TimeEntries.Add(entry);
            Feed.Add(db, me, "time", $"added {Duration(entry.Minutes ?? 0)} for {entry.UserName} on {Feed.Day(entry.Day)}", job, officeOnly: true);
            await db.SaveChangesAsync();
            return Results.Created($"/api/time/{entry.Id}", entry);
        }).AddEndpointFilter(OfficeOnly);

        time.MapPut("/{id:guid}", async (Guid id, TimeEntryRequest r, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var entry = await db.TimeEntries.FirstOrDefaultAsync(e => e.Id == id && e.AccountId == me.AccountId);
            if (entry is null) return Results.NotFound();
            if (entry.Approved) return Locked();
            var (bad, who, job) = await Check(db, me, r, entry);
            if (bad is not null) return bad;
            if (who is not null) { entry.UserId = who.Id; entry.UserName = who.Name; }
            Apply(entry, r, job, me);
            Feed.Add(db, me, "time", $"changed {entry.UserName}'s time on {Feed.Day(entry.Day)}", job, officeOnly: true);
            await db.SaveChangesAsync();
            return Results.Ok(entry);
        }).AddEndpointFilter(OfficeOnly);

        time.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var entry = await db.TimeEntries.FirstOrDefaultAsync(e => e.Id == id && e.AccountId == me.AccountId);
            if (entry is null) return Results.NotFound();
            if (entry.Approved) return Locked();
            db.TimeEntries.Remove(entry);
            var job = entry.ProjectId is Guid pid ? await db.Projects.FirstOrDefaultAsync(p => p.Id == pid && p.AccountId == me.AccountId) : null;
            Feed.Add(db, me, "time", $"deleted {entry.UserName}'s time on {Feed.Day(entry.Day)}", job, officeOnly: true);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).AddEndpointFilter(OfficeOnly);

        // Approves (or unapproves, with "approved": false) a list of entries,
        // usually one person's week.
        time.MapPost("/approve", async (ApproveTimeRequest r, HttpContext ctx, AppDbContext db) =>
        {
            var me = Me(ctx);
            var ids = r.EntryIds?.Distinct().ToList() ?? new();
            if (ids.Count == 0) return Bad("Pick the time to approve.");
            if (ids.Count > MaxApprove) return Bad($"Approve up to {MaxApprove} entries at a time.");
            var list = await db.TimeEntries.Where(e => e.AccountId == me.AccountId && ids.Contains(e.Id)).ToListAsync();
            if (list.Count != ids.Count) return Results.NotFound(new { error = "Some of that time wasn't found." });
            if (r.Approved && list.Any(e => e.ClockOut is null)) return Bad("Time that's still on the clock can't be approved yet.");
            var now = DateTime.UtcNow;
            foreach (var e in list)
            {
                e.Approved = r.Approved;
                e.ApprovedBy = r.Approved ? me.Name : null;
                e.ApprovedAt = r.Approved ? now : null;
            }
            foreach (var person in list.GroupBy(e => e.UserName))
            {
                var total = Duration(person.Sum(e => e.Minutes ?? 0));
                Feed.Add(db, me, "time", (r.Approved ? "approved " : "unapproved ") + $"{total} of {person.Key}'s time", officeOnly: true);
            }
            await db.SaveChangesAsync();
            return Results.Ok(list.OrderBy(e => e.ClockIn));
        }).AddEndpointFilter(OfficeOnly);
    }

    // ---------- Totals ----------

    public record PersonTotals(Guid UserId, string Name, int Minutes, int Regular, int Overtime, int DoubleTime, int Approved, int Entries, bool OnTheClock);

    // Each person's minutes, split California-style: over 8 hours in a day is
    // overtime and over 12 is double time; regular time past 40 hours in a
    // Monday-to-Sunday week is overtime too. Open entries aren't counted yet.
    // (Payroll should still check special cases, like the 7th day in a row.)
    public static List<PersonTotals> Totals(IEnumerable<TimeEntry> entries) =>
        entries.GroupBy(e => e.UserId).Select(person =>
        {
            int regular = 0, overtime = 0, doubleTime = 0;
            var closed = person.Where(e => e.Minutes is not null).ToList();
            foreach (var week in closed.GroupBy(e => WorkWeek.Monday(e.Day)))
            {
                var weekRegular = 0;
                foreach (var day in week.GroupBy(e => e.Day).OrderBy(d => d.Key))
                {
                    var m = day.Sum(e => e.Minutes!.Value);
                    int dayRegular = Math.Min(m, 480), dayOver = Math.Clamp(m - 480, 0, 240), dayDouble = Math.Max(m - 720, 0);
                    var pastForty = Math.Clamp(weekRegular + dayRegular - 2400, 0, dayRegular);
                    dayRegular -= pastForty;
                    dayOver += pastForty;
                    weekRegular += dayRegular;
                    regular += dayRegular; overtime += dayOver; doubleTime += dayDouble;
                }
            }
            var latest = person.OrderByDescending(e => e.ClockIn).First();
            return new PersonTotals(person.Key, latest.UserName, closed.Sum(e => e.Minutes!.Value), regular, overtime, doubleTime,
                closed.Where(e => e.Approved).Sum(e => e.Minutes!.Value), person.Count(), person.Any(e => e.ClockOut is null));
        }).OrderBy(p => p.Name).ToList();

    static List<object> JobTotals(IEnumerable<TimeEntry> entries) =>
        entries.Where(e => e.Minutes is not null).GroupBy(e => (e.ProjectId, e.JobName))
            .Select(j => (object)new { projectId = j.Key.ProjectId, jobName = j.Key.JobName, minutes = j.Sum(e => e.Minutes!.Value) })
            .ToList();

    // ---------- Helpers ----------

    static Task<TimeEntry?> Open(AppDbContext db, User me) =>
        db.TimeEntries.FirstOrDefaultAsync(e => e.AccountId == me.AccountId && e.UserId == me.Id && e.ClockOut == null);

    static TimeEntry Start(User me, Project job, DateTime at, string? notes) => new()
    {
        AccountId = me.AccountId, UserId = me.Id, UserName = me.Name,
        ProjectId = job.Id, JobName = JobNumbers.Label(job), ClockIn = at, Notes = Clean(notes)
    };

    static async Task<(Project?, IResult?)> PickJob(AppDbContext db, User me, Guid? projectId)
    {
        if (projectId is not Guid pid) return (null, Bad("Pick the job you're working on."));
        var job = await db.Projects.FirstOrDefaultAsync(p => p.Id == pid && p.AccountId == me.AccountId);
        return job is null ? (null, Bad("That job wasn't found.")) : (job, null);
    }

    // Two taps on "Clock in" at once: the database lets only one through.
    static async Task<IResult> SaveClock(AppDbContext db, Func<IResult> ok)
    {
        try
        {
            await db.SaveChangesAsync();
            return ok();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new { error = "You're already clocked in." });
        }
    }

    static IResult? CheckBreak(int? breakMinutes, DateTime from, DateTime to)
    {
        if (breakMinutes is not int b) return null;
        if (b < 0) return Bad("The break can't be negative.");
        if (b > (to - from).TotalMinutes) return Bad("The break is longer than the time worked.");
        return null;
    }

    // Checks time the office adds or fixes. Returns the person and job it's for.
    static async Task<(IResult?, User?, Project?)> Check(AppDbContext db, User me, TimeEntryRequest r, TimeEntry? editing)
    {
        // The person: someone on the team, or (when fixing old time) the person
        // it already belongs to, even if they've since left the team.
        if (r.UserId is not Guid uid) return (Bad("Pick who worked."), null, null);
        var who = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.AccountId == me.AccountId);
        if (who is null && uid != editing?.UserId) return (Bad("That person isn't on your team."), null, null);

        // The job: one of the company's, or none only when it was deleted.
        Project? job = null;
        if (r.ProjectId is Guid pid)
        {
            job = await db.Projects.FirstOrDefaultAsync(p => p.Id == pid && p.AccountId == me.AccountId);
            if (job is null) return (Bad("That job wasn't found."), null, null);
        }
        else if (editing is null || editing.ProjectId is not null) return (Bad("Pick the job."), null, null);

        if (r.ClockIn is not DateTime inRaw) return (Bad("Enter when they clocked in."), null, null);
        var clockIn = Clock.AsUtc(inRaw);
        DateTime? clockOut = r.ClockOut is DateTime outRaw ? Clock.AsUtc(outRaw) : null;
        if (clockOut is null && (editing is null || editing.ClockOut is not null))
            return (Bad("Enter when they clocked out."), null, null);
        var now = DateTime.UtcNow;
        if (clockIn > now + FutureSlack || clockOut > now + FutureSlack) return (Bad("Time can't be in the future."), null, null);
        var end = clockOut ?? now;
        if (end <= clockIn) return (Bad("Clock out has to be after clock in."), null, null);
        if (clockOut is not null && end - clockIn > TimeSpan.FromHours(MaxShiftHours))
            return (Bad($"A shift can be up to {MaxShiftHours} hours. Enter longer time as separate shifts."), null, null);
        if (CheckBreak(r.BreakMinutes, clockIn, end) is IResult badBreak) return (badBreak, null, null);
        if (Clean(r.Notes) is { Length: > MaxNotes }) return (Bad("Notes are too long."), null, null);

        // One person can't be in two places at once.
        var editingId = editing?.Id;
        var clash = await db.TimeEntries
            .Where(e => e.AccountId == me.AccountId && e.UserId == uid && e.Id != editingId &&
                        e.ClockIn < end && (e.ClockOut ?? now) > clockIn)
            .OrderBy(e => e.ClockIn).FirstOrDefaultAsync();
        if (clash is not null)
        {
            var name = who?.Name ?? editing!.UserName;
            var span = Clock.ToPacific(clash.ClockIn).ToString("h:mm tt", US) + " to " +
                (clash.ClockOut is DateTime o ? Clock.ToPacific(o).ToString("h:mm tt", US) : "now");
            return (Bad($"This overlaps {name}'s time on {Feed.Day(clash.Day)} ({span}, {clash.JobName})."), null, null);
        }
        return (null, who, job);
    }

    static void Apply(TimeEntry entry, TimeEntryRequest r, Project? job, User me)
    {
        if (job is not null) { entry.ProjectId = job.Id; entry.JobName = JobNumbers.Label(job); }
        entry.ClockIn = Clock.AsUtc(r.ClockIn!.Value);
        entry.ClockOut = r.ClockOut is DateTime o ? Clock.AsUtc(o) : null;
        entry.BreakMinutes = r.BreakMinutes ?? 0;
        entry.Notes = Clean(r.Notes);
        entry.EditedBy = me.Name;
        entry.UpdatedAt = DateTime.UtcNow;
    }

    // Field crew see only their own time, whatever they ask for.
    static IQueryable<TimeEntry> Query(AppDbContext db, User me, DateOnly start, DateOnly end, Guid? userId, Guid? projectId)
    {
        var from = Clock.StartOfDayUtc(start);
        var until = Clock.StartOfDayUtc(end.AddDays(1));
        var q = db.TimeEntries.Where(e => e.AccountId == me.AccountId && e.ClockIn >= from && e.ClockIn < until);
        if (me.Role == UserRole.Field) q = q.Where(e => e.UserId == me.Id);
        else if (userId is Guid uid) q = q.Where(e => e.UserId == uid);
        if (projectId is Guid pid) q = q.Where(e => e.ProjectId == pid);
        return q.OrderBy(e => e.ClockIn);
    }

    // The days a timesheet covers: this week, Monday to Sunday, unless asked.
    static bool Range(DateOnly? from, DateOnly? to, out DateOnly start, out DateOnly end, out string error)
    {
        start = from ?? WorkWeek.Monday(Clock.Today());
        end = to ?? start.AddDays(6);
        error = end < start ? "\"to\" can't be before \"from\"."
            : end.DayNumber - start.DayNumber >= MaxRangeDays ? $"Ask for up to {MaxRangeDays} days at a time." : "";
        return error.Length == 0;
    }

    // "7 h 30 min", "8 h", "45 min".
    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    // Decimal hours for payroll: "7.50".
    static string Hours(int minutes) => (minutes / 60m).ToString("0.00", US);

    // One CSV row. A cell starting with = + - or @ gets a leading apostrophe so
    // spreadsheet apps don't run it as a formula.
    static void Row(StringBuilder csv, params string[] cells) =>
        csv.AppendLine(string.Join(",", cells.Select(c =>
        {
            if (c.Length > 0 && "=+-@\t\r".Contains(c[0])) c = "'" + c;
            return c.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c;
        })));

    static IResult Locked() =>
        Results.Conflict(new { error = "This time is approved. Unapprove it before changing it." });

    static async ValueTask<object?> OfficeOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var me = ctx.HttpContext.Items["me"] as User;
        if (me is null || me.Role == UserRole.Field)
            return Results.Json(new { error = "Only the owner or office staff can do that." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    static User Me(HttpContext ctx) => (User)ctx.Items["me"]!;
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
