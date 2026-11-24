using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace BuildFree.Api;

// Invoices and payments for a job. An invoice is a list of lines (a deposit, a
// percent of the contract, the remaining balance...) and its total is the sum
// of the lines. Payments are recorded against an invoice and can't be more
// than what's still owed on it.
//
// An invoice's state comes from its status and payments: draft, sent,
// partial (some paid), paid, overdue (sent, not paid, past the due date) or
// void. Once an invoice has payments it can't be edited, voided or deleted;
// delete the payment first if it was entered by mistake.
//
// Owner and office only, like estimates.
public static class Invoices
{
    const int MaxLines = 100;

    public static void MapInvoices(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/projects").RequireAuthorization().AddEndpointFilter(OfficeOnly);
        var invoices = app.MapGroup("/api/invoices").RequireAuthorization().AddEndpointFilter(OfficeOnly);
        var payments = app.MapGroup("/api/payments").RequireAuthorization().AddEndpointFilter(OfficeOnly);

        // Every invoice on a job, newest first, with lines, payments and balance.
        jobs.MapGet("/{id:guid}/invoices", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var list = await db.Invoices.Where(i => i.ProjectId == id && i.AccountId == accountId)
                .OrderByDescending(i => i.Number).ToListAsync();
            var ids = list.Select(i => i.Id).ToList();
            var lines = await db.InvoiceLines.Where(l => ids.Contains(l.InvoiceId)).ToListAsync();
            var paid = await db.Payments.Where(p => ids.Contains(p.InvoiceId)).ToListAsync();
            return Results.Ok(list.Select(i => View(i, lines.Where(l => l.InvoiceId == i.Id), paid.Where(p => p.InvoiceId == i.Id))));
        });

        jobs.MapPost("/{id:guid}/invoices", async (Guid id, InvoiceRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            if (!await db.Projects.AnyAsync(p => p.Id == id && p.AccountId == accountId)) return Results.NotFound();
            if (Check(r) is IResult bad) return bad;
            var invoice = new Invoice { AccountId = accountId, ProjectId = id };
            Apply(invoice, r);
            var lines = MakeLines(invoice, r.Lines!);
            db.Invoices.Add(invoice);
            db.InvoiceLines.AddRange(lines);
            var line = await Note(db, ctx, invoice, "");
            await Saving.SaveNumbered(db,
                async () => (await db.Invoices.Where(i => i.AccountId == accountId).MaxAsync(i => (int?)i.Number) ?? 0) + 1,
                n =>
                {
                    invoice.Number = n;
                    line.Text = "created " + Label(invoice);
                });
            return Results.Created($"/api/invoices/{invoice.Id}", View(invoice, lines, Array.Empty<Payment>()));
        });

        // What's been billed and paid on a job, against its revised contract.
        jobs.MapGet("/{id:guid}/billing", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var contract = await Estimates.Contract(db, id, accountId);
            if (contract is null) return Results.NotFound();
            var list = await db.Invoices.Where(i => i.ProjectId == id && i.AccountId == accountId).ToListAsync();
            var ids = list.Select(i => i.Id).ToList();
            var lines = await db.InvoiceLines.Where(l => ids.Contains(l.InvoiceId)).ToListAsync();
            var paid = await db.Payments.Where(p => ids.Contains(p.InvoiceId)).ToListAsync();
            var billed = list.Where(i => i.Status == InvoiceStatus.Sent).ToList();
            var invoiced = billed.Sum(i => lines.Where(l => l.InvoiceId == i.Id).Sum(l => l.Amount));
            var received = paid.Sum(p => p.Amount);
            var today = Today();
            var overdue = billed.Where(i => i.DueDate is DateOnly due && due < today &&
                lines.Where(l => l.InvoiceId == i.Id).Sum(l => l.Amount) > paid.Where(p => p.InvoiceId == i.Id).Sum(p => p.Amount)).ToList();
            return Results.Ok(new
            {
                Contract = contract.Revised,
                HasContract = contract.Original is not null,
                Invoiced = invoiced,
                Paid = received,
                BalanceDue = invoiced - received,
                LeftToBill = contract.Revised - invoiced,
                DraftCount = list.Count(i => i.Status == InvoiceStatus.Draft),
                OverdueCount = overdue.Count
            });
        });

        invoices.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var (invoice, lines, paid) = await Load(db, id, AccountId(me));
            return invoice is null ? Results.NotFound() : Results.Ok(View(invoice, lines, paid));
        });

        // Saves the whole invoice: details plus the full list of lines.
        invoices.MapPut("/{id:guid}", async (Guid id, InvoiceRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var (invoice, oldLines, paid) = await Load(db, id, AccountId(me));
            if (invoice is null) return Results.NotFound();
            if (paid.Count > 0) return Results.Conflict(new { error = "This invoice has payments, so it can't be changed. Delete the payments first if they were a mistake." });
            if (invoice.Status == InvoiceStatus.Void) return Results.Conflict(new { error = "This invoice is void, so it can't be changed." });
            if (Check(r) is IResult bad) return bad;
            Apply(invoice, r);
            invoice.UpdatedAt = DateTime.UtcNow;
            db.InvoiceLines.RemoveRange(oldLines);
            var lines = MakeLines(invoice, r.Lines!);
            db.InvoiceLines.AddRange(lines);
            await Note(db, ctx, invoice, "updated " + Label(invoice));
            await db.SaveChangesAsync();
            return Results.Ok(View(invoice, lines, paid));
        });

        invoices.MapPut("/{id:guid}/status", async (Guid id, StatusRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var (invoice, lines, paid) = await Load(db, id, AccountId(me));
            if (invoice is null) return Results.NotFound();
            if (!Enum.IsDefined(typeof(InvoiceStatus), r.Status)) return Bad("Unknown status.");
            var status = (InvoiceStatus)r.Status;
            if (status != InvoiceStatus.Sent && paid.Count > 0)
                return Results.Conflict(new { error = "This invoice has payments, so it has to stay sent. Delete the payments first if they were a mistake." });
            if (invoice.Status != status)
                await Note(db, ctx, invoice, $"marked invoice #{invoice.Number} {status.ToString().ToLowerInvariant()}");
            invoice.Status = status;
            invoice.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(View(invoice, lines, paid));
        });

        invoices.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var (invoice, lines, paid) = await Load(db, id, AccountId(me));
            if (invoice is null) return Results.NotFound();
            if (paid.Count > 0) return Results.Conflict(new { error = "This invoice has payments, so it can't be deleted. Void it or delete the payments first." });
            if (invoice.Status == InvoiceStatus.Sent) return Results.Conflict(new { error = "This invoice was sent, so it can't be deleted. Void it instead, so there's a record of it." });
            db.InvoiceLines.RemoveRange(lines);
            db.Invoices.Remove(invoice);
            await Note(db, ctx, invoice, "deleted " + Label(invoice));
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Records money received. Recording a payment on a draft marks it sent.
        invoices.MapPost("/{id:guid}/payments", async (Guid id, PaymentRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var (invoice, lines, paid) = await Load(db, id, AccountId(me));
            if (invoice is null) return Results.NotFound();
            if (invoice.Status == InvoiceStatus.Void) return Results.Conflict(new { error = "This invoice is void, so it can't take payments." });
            var amount = Math.Round(r.Amount, 2);
            if (amount <= 0) return Bad("Enter the amount received.");
            if (!Enum.IsDefined(typeof(PaymentMethod), r.Method)) return Bad("Unknown payment method.");
            var reference = Clean(r.Reference);
            if (reference is not null && reference.Length > 100) return Bad("Reference is too long.");
            var balance = lines.Sum(l => l.Amount) - paid.Sum(p => p.Amount);
            if (amount > balance) return Bad($"That's more than the ${balance:0.00} still owed on this invoice.");

            var payment = new Payment
            {
                AccountId = invoice.AccountId,
                InvoiceId = invoice.Id,
                Amount = amount,
                Date = r.Date ?? Today(),
                Method = (PaymentMethod)r.Method,
                Reference = reference,
                RecordedBy = (ctx.Items["me"] as User)?.Name ?? ""
            };
            db.Payments.Add(payment);
            await Note(db, ctx, invoice, $"recorded a {Feed.Money(amount)} payment on invoice #{invoice.Number}", "payment");
            if (invoice.Status == InvoiceStatus.Draft) invoice.Status = InvoiceStatus.Sent;
            invoice.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            paid.Add(payment);
            return Results.Created($"/api/payments/{payment.Id}", View(invoice, lines, paid));
        });

        payments.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
        {
            var accountId = AccountId(me);
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
            if (payment is null) return Results.NotFound();
            db.Payments.Remove(payment);
            var invoice = await db.Invoices.FirstAsync(i => i.Id == payment.InvoiceId);
            await Note(db, ctx, invoice, $"deleted a {Feed.Money(payment.Amount)} payment on invoice #{invoice.Number}", "payment");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    // ---------- Views ----------

    // Dates are compared in Pacific time, where BuildFree's contractors work.
    static readonly TimeZoneInfo Pacific = FindPacific();
    static TimeZoneInfo FindPacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
    static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pacific));

    static string State(Invoice i, decimal total, decimal paid)
    {
        if (i.Status == InvoiceStatus.Void) return "void";
        if (i.Status == InvoiceStatus.Draft) return "draft";
        if (paid >= total) return "paid";
        if (i.DueDate is DateOnly due && due < Today()) return "overdue";
        return paid > 0 ? "partial" : "sent";
    }

    static object View(Invoice i, IEnumerable<InvoiceLine> lines, IEnumerable<Payment> payments)
    {
        var lineList = lines.OrderBy(l => l.Position).Select(l => new { l.Id, l.Description, l.Amount }).ToList();
        var paymentList = payments.OrderBy(p => p.Date).ThenBy(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Amount, p.Date, p.Method, p.Reference, p.RecordedBy }).ToList();
        var total = lineList.Sum(l => l.Amount);
        var paid = paymentList.Sum(p => p.Amount);
        return new
        {
            i.Id, i.ProjectId, i.Number, i.Title, i.Status, i.IssueDate, i.DueDate, i.Notes, i.CreatedAt, i.UpdatedAt,
            Lines = lineList,
            Payments = paymentList,
            Total = total,
            Paid = paid,
            Balance = total - paid,
            State = State(i, total, paid)
        };
    }

    // ---------- Helpers ----------

    static string Label(Invoice i) => $"invoice #{i.Number} · {i.Title}";

    // Adds a line to the recent activity feed. Field crew don't see these.
    static Task<Activity> Note(AppDbContext db, HttpContext ctx, Invoice i, string text, string kind = "invoice") =>
        Feed.Add(db, (User)ctx.Items["me"]!, kind, text, i.ProjectId, officeOnly: true);

    static async Task<(Invoice?, List<InvoiceLine>, List<Payment>)> Load(AppDbContext db, Guid id, Guid accountId)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId);
        if (invoice is null) return (null, new List<InvoiceLine>(), new List<Payment>());
        var lines = await db.InvoiceLines.Where(l => l.InvoiceId == id).ToListAsync();
        var paid = await db.Payments.Where(p => p.InvoiceId == id).ToListAsync();
        return (invoice, lines, paid);
    }

    static void Apply(Invoice i, InvoiceRequest r)
    {
        i.Title = Clean(r.Title)!;
        i.IssueDate = r.IssueDate ?? Today();
        i.DueDate = r.DueDate;
        i.Notes = Clean(r.Notes);
    }

    static List<InvoiceLine> MakeLines(Invoice i, List<InvoiceLineRequest> lines) =>
        lines.Select((l, n) => new InvoiceLine
        {
            InvoiceId = i.Id,
            Position = n,
            Description = Clean(l.Description)!,
            Amount = Math.Round(l.Amount, 2)
        }).ToList();

    static IResult? Check(InvoiceRequest r)
    {
        var title = Clean(r.Title);
        if (title is null) return Bad("Give the invoice a title.");
        if (title.Length > 200) return Bad("Title is too long.");
        if (Clean(r.Notes) is string notes && notes.Length > 10000) return Bad("Notes are too long.");
        if (r.IssueDate is DateOnly issue && r.DueDate is DateOnly due && due < issue)
            return Bad("The due date can't be before the invoice date.");
        if (r.Lines is null || r.Lines.Count == 0) return Bad("Add at least one line.");
        if (r.Lines.Count > MaxLines) return Bad($"An invoice can have up to {MaxLines} lines.");
        for (var n = 0; n < r.Lines.Count; n++)
        {
            var l = r.Lines[n];
            var description = Clean(l.Description);
            if (description is null) return Bad($"Line {n + 1}: add a description.");
            if (description.Length > 500) return Bad($"Line {n + 1}: description is too long.");
            if (Math.Abs(l.Amount) > 100_000_000) return Bad($"Line {n + 1}: amount is too large.");
        }
        if (r.Lines.Sum(l => Math.Round(l.Amount, 2)) <= 0) return Bad("The invoice total has to be more than $0.");
        return null;
    }

    static async ValueTask<object?> OfficeOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var me = ctx.HttpContext.Items["me"] as User;
        if (me is null || me.Role == UserRole.Field)
            return Results.Json(new { error = "Only the owner or office staff can see invoices and payments." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    static Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
