using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace BuildFree.Api;

// Estimates and change orders for a job: line items at cost, one markup for the
// whole document, and sales tax on materials. Customers only ever see
// marked-up prices; the cost, markup and profit are for the owner and office.
// Field crew can't see them at all.
//
// Change orders are stored like estimates (Kind = ChangeOrder) with their own
// numbering. They can add days to the schedule and can include credits
// (negative unit costs) for work that was taken out. Both kinds share the
// /api/estimates/{id} endpoints for get, save, status, copy and delete.
public static class Estimates
{
    const int MaxItems = 200;

    public static void MapEstimates(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/projects").RequireAuthorization().AddEndpointFilter(OfficeOnly);
        var estimates = app.MapGroup("/api/estimates").RequireAuthorization().AddEndpointFilter(OfficeOnly);

        // Every estimate (or change order) on a job, newest first, with totals.
        jobs.MapGet("/{id:guid}/estimates", (Guid id, ClaimsPrincipal me, AppDbContext db) =>
            ListDocs(db, id, AccountId(me), EstimateKind.Estimate));
        jobs.MapGet("/{id:guid}/change-orders", (Guid id, ClaimsPrincipal me, AppDbContext db) =>
            ListDocs(db, id, AccountId(me), EstimateKind.ChangeOrder));

        jobs.MapPost("/{id:guid}/estimates", (Guid id, EstimateRequest r, ClaimsPrincipal me, AppDbContext db) =>
            CreateDoc(db, id, AccountId(me), EstimateKind.Estimate, r));
        jobs.MapPost("/{id:guid}/change-orders", (Guid id, EstimateRequest r, ClaimsPrincipal me, AppDbContext db) =>
            CreateDoc(db, id, AccountId(me), EstimateKind.ChangeOrder, r));

        // The job's contract: the latest approved estimate, plus approved change
        // orders, gives the revised contract total.
        jobs.MapGet("/{id:guid}/contract", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
            await Contract(db, id, AccountId(me)) is ContractSummary c ? Results.Ok(c) : Results.NotFound());

        estimates.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var (estimate, items) = await Load(db, id, AccountId(me));
            return estimate is null ? Results.NotFound() : Results.Ok(View(estimate, items));
        });

        // Saves the whole estimate: details plus the full list of line items.
        estimates.MapPut("/{id:guid}", async (Guid id, EstimateRequest r, ClaimsPrincipal me, AppDbContext db) =>
        {
            var (estimate, oldItems) = await Load(db, id, AccountId(me));
            if (estimate is null) return Results.NotFound();
            if (estimate.Status == EstimateStatus.Approved)
                return Results.Conflict(new { error = $"This {Name(estimate.Kind)} is approved, so it can't be changed. Make a copy to start a new version." });
            if (Check(r, estimate.Kind) is IResult bad) return bad;
            Apply(estimate, r);
            estimate.UpdatedAt = DateTime.UtcNow;
            db.EstimateItems.RemoveRange(oldItems);
            var items = MakeItems(estimate, r.Items!);
            db.EstimateItems.AddRange(items);
            await db.SaveChangesAsync();
            return Results.Ok(View(estimate, items));
        });

        estimates.MapPut("/{id:guid}/status", async (Guid id, StatusRequest r, ClaimsPrincipal me, AppDbContext db) =>
        {
            var (estimate, items) = await Load(db, id, AccountId(me));
            if (estimate is null) return Results.NotFound();
            if (!Enum.IsDefined(typeof(EstimateStatus), r.Status)) return Bad("Unknown status.");
            estimate.Status = (EstimateStatus)r.Status;
            estimate.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(View(estimate, items));
        });

        // Makes a new draft with the same details and line items.
        estimates.MapPost("/{id:guid}/copy", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var (source, sourceItems) = await Load(db, id, accountId);
            if (source is null) return Results.NotFound();
            var copy = new Estimate
            {
                AccountId = accountId,
                ProjectId = source.ProjectId,
                Kind = source.Kind,
                Number = await NextNumber(db, accountId, source.Kind),
                DaysAdded = source.DaysAdded,
                Title = source.Title,
                Notes = source.Notes,
                MarkupPercent = source.MarkupPercent,
                TaxPercent = source.TaxPercent
            };
            var items = sourceItems.Select(i => new EstimateItem
            {
                EstimateId = copy.Id,
                Position = i.Position,
                Description = i.Description,
                Category = i.Category,
                Quantity = i.Quantity,
                Unit = i.Unit,
                UnitCost = i.UnitCost
            }).ToList();
            db.Estimates.Add(copy);
            db.EstimateItems.AddRange(items);
            await db.SaveChangesAsync();
            return Results.Created($"/api/estimates/{copy.Id}", View(copy, items));
        });

        estimates.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var (estimate, items) = await Load(db, id, AccountId(me));
            if (estimate is null) return Results.NotFound();
            db.EstimateItems.RemoveRange(items);
            db.Estimates.Remove(estimate);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ---------- Totals ----------

    static decimal Money(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    // The price the customer sees for one unit: cost plus markup, to the cent.
    public static decimal UnitPrice(decimal unitCost, decimal markupPercent) =>
        Money(unitCost * (1 + markupPercent / 100m));

    public record Line(Guid Id, string Description, EstimateCategory Category, decimal Quantity, string? Unit,
        decimal UnitCost, decimal UnitPrice, decimal Amount, decimal Cost);
    public record Sums(List<Line> Lines, decimal Subtotal, decimal Tax, decimal Total, decimal Cost, decimal Profit, decimal MarginPercent);

    static Sums Totals(Estimate e, IEnumerable<EstimateItem> source)
    {
        var lines = source.OrderBy(i => i.Position).Select(i =>
        {
            var price = UnitPrice(i.UnitCost, e.MarkupPercent);
            return new Line(i.Id, i.Description, i.Category, i.Quantity, i.Unit, i.UnitCost,
                price, Money(i.Quantity * price), Money(i.Quantity * i.UnitCost));
        }).ToList();
        var subtotal = lines.Sum(l => l.Amount);
        var taxable = lines.Where(l => l.Category == EstimateCategory.Materials).Sum(l => l.Amount);
        var tax = Money(taxable * e.TaxPercent / 100m);
        var cost = lines.Sum(l => l.Cost);
        var profit = subtotal - cost;
        var margin = subtotal > 0 ? Math.Round(profit / subtotal * 100m, 1, MidpointRounding.AwayFromZero) : 0m;
        return new Sums(lines, subtotal, tax, subtotal + tax, cost, profit, margin);
    }

    static object View(Estimate e, IEnumerable<EstimateItem> source)
    {
        var t = Totals(e, source);
        return new
        {
            e.Id, e.ProjectId, e.Kind, e.Number, e.Title, e.Status, e.Notes, e.MarkupPercent, e.TaxPercent,
            e.DaysAdded, e.CreatedAt, e.UpdatedAt,
            Items = t.Lines,
            t.Subtotal, t.Tax, t.Total, t.Cost, t.Profit, t.MarginPercent
        };
    }

    // ---------- Contract ----------

    public record ContractSummary(Guid? EstimateId, int? EstimateNumber, decimal? Original, decimal ApprovedChanges,
        int ApprovedCount, decimal PendingChanges, int PendingCount, decimal Revised, int DaysAdded);

    // The job's contract: the latest approved estimate plus approved change
    // orders. Returns null if the job isn't in this company. Invoices use this
    // too, to show how much is left to bill.
    public static async Task<ContractSummary?> Contract(AppDbContext db, Guid projectId, Guid accountId)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.AccountId == accountId)) return null;
        var docs = await db.Estimates.Where(e => e.ProjectId == projectId && e.AccountId == accountId).ToListAsync();
        var ids = docs.Select(e => e.Id).ToList();
        var items = await db.EstimateItems.Where(i => ids.Contains(i.EstimateId)).ToListAsync();
        decimal TotalOf(Estimate e) => Totals(e, items.Where(i => i.EstimateId == e.Id)).Total;

        var estimate = docs.Where(e => e.Kind == EstimateKind.Estimate && e.Status == EstimateStatus.Approved)
            .OrderByDescending(e => e.Number).FirstOrDefault();
        var changes = docs.Where(e => e.Kind == EstimateKind.ChangeOrder).ToList();
        var approved = changes.Where(e => e.Status == EstimateStatus.Approved).ToList();
        var pending = changes.Where(e => e.Status is EstimateStatus.Draft or EstimateStatus.Sent).ToList();
        decimal? original = estimate is null ? null : TotalOf(estimate);
        var approvedChanges = approved.Sum(e => TotalOf(e));
        return new ContractSummary(estimate?.Id, estimate?.Number, original, approvedChanges, approved.Count,
            pending.Sum(e => TotalOf(e)), pending.Count, (original ?? 0) + approvedChanges, approved.Sum(e => e.DaysAdded ?? 0));
    }

    // ---------- Helpers ----------

    static string Name(EstimateKind kind) => kind == EstimateKind.ChangeOrder ? "change order" : "estimate";

    static async Task<IResult> ListDocs(AppDbContext db, Guid projectId, Guid accountId, EstimateKind kind)
    {
        var list = await db.Estimates.Where(e => e.ProjectId == projectId && e.AccountId == accountId && e.Kind == kind)
            .OrderByDescending(e => e.Number).ToListAsync();
        var ids = list.Select(e => e.Id).ToList();
        var items = await db.EstimateItems.Where(i => ids.Contains(i.EstimateId)).ToListAsync();
        return Results.Ok(list.Select(e => View(e, items.Where(i => i.EstimateId == e.Id))));
    }

    static async Task<IResult> CreateDoc(AppDbContext db, Guid projectId, Guid accountId, EstimateKind kind, EstimateRequest r)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.AccountId == accountId)) return Results.NotFound();
        if (Check(r, kind) is IResult bad) return bad;
        var estimate = new Estimate
        {
            AccountId = accountId,
            ProjectId = projectId,
            Kind = kind,
            Number = await NextNumber(db, accountId, kind)
        };
        Apply(estimate, r);
        var items = MakeItems(estimate, r.Items!);
        db.Estimates.Add(estimate);
        db.EstimateItems.AddRange(items);
        await db.SaveChangesAsync();
        return Results.Created($"/api/estimates/{estimate.Id}", View(estimate, items));
    }

    static async Task<(Estimate?, List<EstimateItem>)> Load(AppDbContext db, Guid id, Guid accountId)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == id && e.AccountId == accountId);
        if (estimate is null) return (null, new List<EstimateItem>());
        var items = await db.EstimateItems.Where(i => i.EstimateId == id).ToListAsync();
        return (estimate, items);
    }

    // Numbers count up per company, separately for estimates and change orders.
    static async Task<int> NextNumber(AppDbContext db, Guid accountId, EstimateKind kind) =>
        (await db.Estimates.Where(e => e.AccountId == accountId && e.Kind == kind).MaxAsync(e => (int?)e.Number) ?? 0) + 1;

    static void Apply(Estimate e, EstimateRequest r)
    {
        e.Title = Clean(r.Title)!;
        e.Notes = Clean(r.Notes);
        e.MarkupPercent = Math.Round(r.MarkupPercent ?? 0, 2);
        e.TaxPercent = Math.Round(r.TaxPercent ?? 0, 3);
        e.DaysAdded = e.Kind == EstimateKind.ChangeOrder ? r.DaysAdded : null;
    }

    static List<EstimateItem> MakeItems(Estimate e, List<EstimateItemRequest> items) =>
        items.Select((i, n) => new EstimateItem
        {
            EstimateId = e.Id,
            Position = n,
            Description = Clean(i.Description)!,
            Category = (EstimateCategory)i.Category,
            Quantity = Math.Round(i.Quantity, 3),
            Unit = Clean(i.Unit),
            UnitCost = Math.Round(i.UnitCost, 2)
        }).ToList();

    static IResult? Check(EstimateRequest r, EstimateKind kind)
    {
        var credits = kind == EstimateKind.ChangeOrder;
        var title = Clean(r.Title);
        if (title is null) return Bad($"Give the {Name(kind)} a title.");
        if (title.Length > 200) return Bad("Title is too long.");
        if (Clean(r.Notes) is string notes && notes.Length > 10000) return Bad("Notes are too long.");
        if (r.MarkupPercent is < 0 or > 500) return Bad("Markup must be between 0% and 500%.");
        if (r.TaxPercent is < 0 or > 20) return Bad("Tax rate must be between 0% and 20%.");
        if (r.Items is null) return Bad("Add at least one line item.");
        if (r.Items.Count > MaxItems) return Bad($"Up to {MaxItems} line items are allowed.");
        if (credits && r.DaysAdded is < -365 or > 3650) return Bad("Days added must be between -365 and 3,650.");
        for (var n = 0; n < r.Items.Count; n++)
        {
            var i = r.Items[n];
            var line = $"Line {n + 1}: ";
            var description = Clean(i.Description);
            if (description is null) return Bad(line + "add a description.");
            if (description.Length > 500) return Bad(line + "description is too long.");
            if (!Enum.IsDefined(typeof(EstimateCategory), i.Category)) return Bad(line + "unknown category.");
            if (Math.Round(i.Quantity, 3) <= 0 || i.Quantity > 1_000_000) return Bad(line + "quantity must be more than 0.");
            if (Clean(i.Unit) is string unit && unit.Length > 20) return Bad(line + "unit is too long.");
            if (Math.Abs(i.UnitCost) > 100_000_000) return Bad(line + "unit cost is too large.");
            if (i.UnitCost < 0 && !credits) return Bad(line + "unit cost must be $0 or more. Credits go on change orders.");
        }
        return null;
    }

    static async ValueTask<object?> OfficeOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var me = ctx.HttpContext.Items["me"] as User;
        if (me is null || me.Role == UserRole.Field)
            return Results.Json(new { error = "Only the owner or office staff can see estimates and change orders." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    static Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
