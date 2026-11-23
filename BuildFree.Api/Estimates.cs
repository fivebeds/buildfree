using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace BuildFree.Api;

// Estimates for a job: line items at cost, one markup for the whole estimate,
// and sales tax on materials. Customers only ever see marked-up prices; the
// cost, markup and profit are for the owner and office. Field crew can't see
// estimates at all.
public static class Estimates
{
    const int MaxItems = 200;

    public static void MapEstimates(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/projects").RequireAuthorization().AddEndpointFilter(OfficeOnly);
        var estimates = app.MapGroup("/api/estimates").RequireAuthorization().AddEndpointFilter(OfficeOnly);

        // Every estimate on a job, newest first, with its totals.
        jobs.MapGet("/{id:guid}/estimates", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var list = await db.Estimates.Where(e => e.ProjectId == id && e.AccountId == accountId)
                .OrderByDescending(e => e.Number).ToListAsync();
            var ids = list.Select(e => e.Id).ToList();
            var items = await db.EstimateItems.Where(i => ids.Contains(i.EstimateId)).ToListAsync();
            return Results.Ok(list.Select(e => View(e, items.Where(i => i.EstimateId == e.Id))));
        });

        jobs.MapPost("/{id:guid}/estimates", async (Guid id, EstimateRequest r, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            if (!await db.Projects.AnyAsync(p => p.Id == id && p.AccountId == accountId)) return Results.NotFound();
            if (Check(r) is IResult bad) return bad;
            var estimate = new Estimate
            {
                AccountId = accountId,
                ProjectId = id,
                Number = await NextNumber(db, accountId)
            };
            Apply(estimate, r);
            var items = MakeItems(estimate, r.Items!);
            db.Estimates.Add(estimate);
            db.EstimateItems.AddRange(items);
            await db.SaveChangesAsync();
            return Results.Created($"/api/estimates/{estimate.Id}", View(estimate, items));
        });

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
                return Results.Conflict(new { error = "This estimate is approved, so it can't be changed. Make a copy to start a new version." });
            if (Check(r) is IResult bad) return bad;
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
                Number = await NextNumber(db, accountId),
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

    static object View(Estimate e, IEnumerable<EstimateItem> source)
    {
        var lines = source.OrderBy(i => i.Position).Select(i =>
        {
            var price = UnitPrice(i.UnitCost, e.MarkupPercent);
            return new
            {
                i.Id, i.Description, i.Category, i.Quantity, i.Unit, i.UnitCost,
                UnitPrice = price,
                Amount = Money(i.Quantity * price),
                Cost = Money(i.Quantity * i.UnitCost)
            };
        }).ToList();
        var subtotal = lines.Sum(l => l.Amount);
        var taxable = lines.Where(l => l.Category == EstimateCategory.Materials).Sum(l => l.Amount);
        var tax = Money(taxable * e.TaxPercent / 100m);
        var cost = lines.Sum(l => l.Cost);
        var profit = subtotal - cost;
        return new
        {
            e.Id, e.ProjectId, e.Number, e.Title, e.Status, e.Notes, e.MarkupPercent, e.TaxPercent,
            e.CreatedAt, e.UpdatedAt,
            Items = lines,
            Subtotal = subtotal,
            Tax = tax,
            Total = subtotal + tax,
            Cost = cost,
            Profit = profit,
            MarginPercent = subtotal > 0 ? Math.Round(profit / subtotal * 100m, 1, MidpointRounding.AwayFromZero) : 0m
        };
    }

    // ---------- Helpers ----------

    static async Task<(Estimate?, List<EstimateItem>)> Load(AppDbContext db, Guid id, Guid accountId)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == id && e.AccountId == accountId);
        if (estimate is null) return (null, new List<EstimateItem>());
        var items = await db.EstimateItems.Where(i => i.EstimateId == id).ToListAsync();
        return (estimate, items);
    }

    // Estimate numbers count up per company: #1, #2, #3...
    static async Task<int> NextNumber(AppDbContext db, Guid accountId) =>
        (await db.Estimates.Where(e => e.AccountId == accountId).MaxAsync(e => (int?)e.Number) ?? 0) + 1;

    static void Apply(Estimate e, EstimateRequest r)
    {
        e.Title = Clean(r.Title)!;
        e.Notes = Clean(r.Notes);
        e.MarkupPercent = Math.Round(r.MarkupPercent ?? 0, 2);
        e.TaxPercent = Math.Round(r.TaxPercent ?? 0, 3);
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

    static IResult? Check(EstimateRequest r)
    {
        var title = Clean(r.Title);
        if (title is null) return Bad("Give the estimate a title.");
        if (title.Length > 200) return Bad("Title is too long.");
        if (Clean(r.Notes) is string notes && notes.Length > 10000) return Bad("Notes are too long.");
        if (r.MarkupPercent is < 0 or > 500) return Bad("Markup must be between 0% and 500%.");
        if (r.TaxPercent is < 0 or > 20) return Bad("Tax rate must be between 0% and 20%.");
        if (r.Items is null) return Bad("Add at least one line item.");
        if (r.Items.Count > MaxItems) return Bad($"An estimate can have up to {MaxItems} line items.");
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
            if (i.UnitCost < 0 || i.UnitCost > 100_000_000) return Bad(line + "unit cost must be $0 or more.");
        }
        return null;
    }

    static async ValueTask<object?> OfficeOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var me = ctx.HttpContext.Items["me"] as User;
        if (me is null || me.Role == UserRole.Field)
            return Results.Json(new { error = "Only the owner or office staff can see estimates." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    static Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
