using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace BuildFree.Api;

// The recent activity feed: "Mariana updated invoice #12 · Deposit". Endpoints
// add a line with Feed.Add before they save, so the line is saved with the
// change itself, or not at all.
public static class Feed
{
    static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

    public static Activity Add(AppDbContext db, User me, string kind, string text, Project? job = null, bool officeOnly = false)
    {
        var line = new Activity
        {
            AccountId = me.AccountId,
            ProjectId = job?.Id,
            JobName = job is null ? null : JobNumbers.Label(job),
            UserId = me.Id,
            UserName = me.Name,
            Kind = kind,
            Text = text,
            OfficeOnly = officeOnly
        };
        db.Activities.Add(line);
        return line;
    }

    // For endpoints that only have the job's id.
    public static async Task<Activity> Add(AppDbContext db, User me, string kind, string text, Guid projectId, bool officeOnly = false) =>
        Add(db, me, kind, text, await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.AccountId == me.AccountId), officeOnly);

    public static string Day(DateOnly d) => d.ToString("ddd, MMM d", US);
    public static string Money(decimal m) => m.ToString("C", US);

    // GET /api/activity, newest first. Optional: projectId or userId to narrow
    // it, limit (up to 100, 30 by default), and before (the createdAt of the
    // last line shown) to load older lines.
    public static void MapActivity(this WebApplication app)
    {
        app.MapGet("/api/activity", async (Guid? projectId, Guid? userId, string? before, int? limit, HttpContext ctx, AppDbContext db) =>
        {
            var me = (User)ctx.Items["me"]!;
            var accountId = me.AccountId;
            var q = db.Activities.Where(a => a.AccountId == accountId);
            if (me.Role == UserRole.Field) q = q.Where(a => !a.OfficeOnly);
            if (projectId is Guid pid) q = q.Where(a => a.ProjectId == pid);
            if (userId is Guid uid) q = q.Where(a => a.UserId == uid);
            if (!string.IsNullOrEmpty(before))
            {
                if (!DateTime.TryParse(before, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var cutoff))
                    return Results.BadRequest(new { error = "\"before\" should be a date and time." });
                cutoff = DateTime.SpecifyKind(cutoff, DateTimeKind.Utc);
                q = q.Where(a => a.CreatedAt < cutoff);
            }
            var take = Math.Clamp(limit ?? 30, 1, 100);
            return Results.Ok(await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take).ToListAsync());
        }).RequireAuthorization();
    }
}

// Job numbers count up per company from 1001. Jobs from before job numbers
// existed are numbered on startup, oldest first.
public static class JobNumbers
{
    public const int First = 1001;
    public const int Max = 9_999_999;

    public static async Task<int> Next(AppDbContext db, Guid accountId) =>
        (await db.Projects.Where(p => p.AccountId == accountId).MaxAsync(p => p.Number) ?? First - 1) + 1;

    public static string Label(Project p) => p.Number is int n ? $"#{n} {p.Name}" : p.Name;

    public static async Task Backfill(AppDbContext db)
    {
        var missing = await db.Projects.Where(p => p.Number == null).OrderBy(p => p.CreatedAt).ToListAsync();
        foreach (var company in missing.GroupBy(p => p.AccountId))
        {
            var next = await Next(db, company.Key);
            foreach (var job in company) job.Number = next++;
        }
        if (missing.Count > 0) await db.SaveChangesAsync();
    }
}
