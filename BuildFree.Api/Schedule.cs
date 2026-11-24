using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace BuildFree.Api;

// The schedule: what's happening on each job and when, and the company's work
// calendar (which weekdays it works, plus holidays and other days off).
//
// Everyone sees the schedule. The owner and office plan it (add, edit, delete,
// and set the work calendar). Anyone can check an item off.
public static class Schedule
{
    const int MaxAssignees = 20;
    const int MaxSpanDays = 3 * 366;   // one item can run up to about 3 years
    const int MaxAgendaDays = 62;      // one agenda request covers up to about 2 months
    const int MaxNonWorkdays = 300;

    public static void MapSchedule(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/projects").RequireAuthorization();
        var items = app.MapGroup("/api/schedule").RequireAuthorization();

        // A job's schedule, in date order.
        jobs.MapGet("/{id:guid}/schedule", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            if (!await db.Projects.AnyAsync(p => p.Id == id && p.AccountId == accountId)) return Results.NotFound();
            return Results.Ok(await db.ScheduleItems.Where(i => i.ProjectId == id && i.AccountId == accountId)
                .OrderBy(i => i.StartDate).ThenBy(i => i.EndDate).ThenBy(i => i.Title).ToListAsync());
        });

        jobs.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var job = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
            if (job is null) return Results.NotFound();
            if (await Check(db, accountId, r) is IResult bad) return bad;
            var item = new ScheduleItem { AccountId = accountId, ProjectId = id };
            Apply(item, r);
            db.ScheduleItems.Add(item);
            Feed.Add(db, Me(ctx), "schedule", $"scheduled {item.Title} for {When(item)}", job);
            await db.SaveChangesAsync();
            return Results.Created($"/api/schedule/{item.Id}", item);
        }).AddEndpointFilter(OfficeOnly);

        items.MapPut("/{id:guid}", async (Guid id, ScheduleRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var item = await db.ScheduleItems.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId);
            if (item is null) return Results.NotFound();
            if (await Check(db, accountId, r) is IResult bad) return bad;
            var oldWhen = When(item);
            Apply(item, r);
            var text = When(item) != oldWhen ? $"moved {item.Title} to {When(item)}" : "updated the schedule item " + item.Title;
            await Feed.Add(db, Me(ctx), "schedule", text, item.ProjectId);
            await db.SaveChangesAsync();
            return Results.Ok(item);
        }).AddEndpointFilter(OfficeOnly);

        items.MapPost("/{id:guid}/toggle", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var item = await db.ScheduleItems.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId);
            if (item is null) return Results.NotFound();
            item.Done = !item.Done;
            await Feed.Add(db, Me(ctx), "schedule", (item.Done ? "finished " : "reopened ") + item.Title, item.ProjectId);
            await db.SaveChangesAsync();
            return Results.Ok(item);
        });

        items.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var item = await db.ScheduleItems.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId);
            if (item is null) return Results.NotFound();
            db.ScheduleItems.Remove(item);
            await Feed.Add(db, Me(ctx), "schedule", "took " + item.Title + " off the schedule", item.ProjectId);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).AddEndpointFilter(OfficeOnly);

        // The agenda: everything on the schedule between two dates, across all
        // jobs, plus which of those days are workdays. Without dates it's this
        // week, Monday to Sunday, in Pacific time. userId narrows it to the
        // items assigned to one person.
        items.MapGet("/", async (DateOnly? from, DateOnly? to, Guid? userId, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var start = from ?? WorkWeek.Monday(Clock.Today());
            var end = to ?? start.AddDays(6);
            if (end < start) return Bad("\"to\" can't be before \"from\".");
            if (end.DayNumber - start.DayNumber >= MaxAgendaDays) return Bad($"Ask for up to {MaxAgendaDays} days at a time.");
            var q = db.ScheduleItems.Where(i => i.AccountId == accountId && i.StartDate <= end && i.EndDate >= start);
            if (userId is Guid uid) q = q.Where(i => i.AssigneeIds.Contains(uid));
            var list = await q.OrderBy(i => i.StartDate).ThenBy(i => i.EndDate).ThenBy(i => i.Title).ToListAsync();
            var workDays = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.WorkDays).FirstAsync();
            var off = await db.NonWorkdays.Where(d => d.AccountId == accountId && d.Date >= start && d.Date <= end)
                .OrderBy(d => d.Date).ToListAsync();
            return Results.Ok(new { from = start, to = end, workDays = WorkWeek.Days(workDays), nonWorkdays = off, items = list });
        });

        // The work calendar: which weekdays the company works, and its days off.
        app.MapGet("/api/work-calendar", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            return Results.Ok(await Calendar(db, accountId));
        }).RequireAuthorization();

        // Replaces the work calendar: the weekdays worked and the full list of days off.
        app.MapPut("/api/work-calendar", async (WorkCalendarRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            if (r.WorkDays is null || r.WorkDays.Count == 0) return Bad("Pick at least one workday.");
            if (r.WorkDays.Any(d => d is < 0 or > 6)) return Bad("Workdays go from 0 (Sunday) to 6 (Saturday).");
            var days = r.NonWorkdays ?? new List<NonWorkdayRequest>();
            if (days.Count > MaxNonWorkdays) return Bad($"Up to {MaxNonWorkdays} days off are allowed.");
            if (days.Any(d => d.Date is null)) return Bad("Every day off needs a date.");
            if (days.Any(d => (d.Name?.Trim().Length ?? 0) > 100)) return Bad("A day off's name is too long.");
            if (days.Select(d => d.Date).Distinct().Count() != days.Count) return Bad("A date is listed twice.");

            var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
            account.WorkDays = WorkWeek.Mask(r.WorkDays);
            db.NonWorkdays.RemoveRange(await db.NonWorkdays.Where(d => d.AccountId == accountId).ToListAsync());
            db.NonWorkdays.AddRange(days.Select(d => new NonWorkday
            {
                AccountId = accountId,
                Date = d.Date!.Value,
                Name = string.IsNullOrWhiteSpace(d.Name) ? "Day off" : d.Name.Trim()
            }));
            Feed.Add(db, Me(ctx), "schedule", "updated the work calendar");
            await db.SaveChangesAsync();
            return Results.Ok(await Calendar(db, accountId));
        }).RequireAuthorization().AddEndpointFilter(OfficeOnly);
    }

    // ---------- Helpers ----------

    static async Task<object> Calendar(AppDbContext db, Guid accountId)
    {
        var workDays = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.WorkDays).FirstAsync();
        var off = await db.NonWorkdays.Where(d => d.AccountId == accountId).OrderBy(d => d.Date).ToListAsync();
        return new { workDays = WorkWeek.Days(workDays), nonWorkdays = off };
    }

    static async Task<IResult?> Check(AppDbContext db, Guid accountId, ScheduleRequest r)
    {
        var title = Clean(r.Title);
        if (title is null) return Bad("Give the schedule item a name.");
        if (title.Length > 200) return Bad("The name is too long.");
        if (r.StartDate is not DateOnly start) return Bad("Pick a start date.");
        var end = r.EndDate ?? start;
        if (end < start) return Bad("The end date can't be before the start date.");
        if (end.DayNumber - start.DayNumber > MaxSpanDays) return Bad("A schedule item can run up to about 3 years.");
        if (Clean(r.Notes) is string notes && notes.Length > 4000) return Bad("Notes are too long.");
        if (r.AssigneeIds is { Count: > 0 } ids)
        {
            var wanted = ids.Distinct().ToList();
            if (wanted.Count > MaxAssignees) return Bad($"Assign up to {MaxAssignees} people.");
            if (await db.Users.CountAsync(u => u.AccountId == accountId && wanted.Contains(u.Id)) != wanted.Count)
                return Bad("Someone you assigned isn't on your team.");
        }
        return null;
    }

    static void Apply(ScheduleItem item, ScheduleRequest r)
    {
        item.Title = Clean(r.Title)!;
        item.StartDate = r.StartDate!.Value;
        item.EndDate = r.EndDate ?? item.StartDate;
        item.AssigneeIds = r.AssigneeIds?.Distinct().ToList() ?? new();
        item.Notes = Clean(r.Notes);
    }

    // "Mon, Dec 1" or "Mon, Dec 1 to Fri, Dec 5".
    static string When(ScheduleItem i) =>
        i.EndDate == i.StartDate ? Feed.Day(i.StartDate) : $"{Feed.Day(i.StartDate)} to {Feed.Day(i.EndDate)}";

    static async ValueTask<object?> OfficeOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var me = ctx.HttpContext.Items["me"] as User;
        if (me is null || me.Role == UserRole.Field)
            return Results.Json(new { error = "Only the owner or office staff can change the schedule." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    static User Me(HttpContext ctx) => (User)ctx.Items["me"]!;
    static Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IResult Bad(string message) => Results.BadRequest(new { error = message });
}

// Which days of the week a company works, stored as one bit per day
// (Sunday = bit 0 ... Saturday = bit 6).
public static class WorkWeek
{
    public const int MondayToFriday = 0b0111110;

    public static List<int> Days(int mask) => Enumerable.Range(0, 7).Where(d => (mask & (1 << d)) != 0).ToList();
    public static int Mask(IEnumerable<int> days) => days.Aggregate(0, (mask, d) => mask | (1 << d));

    // The Monday on or before a date.
    public static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}
