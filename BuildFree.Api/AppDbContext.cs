using Microsoft.EntityFrameworkCore;

namespace BuildFree.Api;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DailyLog> Logs => Set<DailyLog>();
    public DbSet<TodoItem> Todos => Set<TodoItem>();
    public DbSet<LogPhoto> Photos => Set<LogPhoto>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Estimate> Estimates => Set<Estimate>();
    public DbSet<EstimateItem> EstimateItems => Set<EstimateItem>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<User>().HasIndex(u => u.AccountId);
        b.Entity<Invite>().HasIndex(i => i.CodeHash).IsUnique();
        b.Entity<Invite>().HasIndex(i => i.AccountId);
        b.Entity<Client>().HasIndex(c => c.AccountId);
        b.Entity<Project>().HasIndex(p => p.AccountId);
        b.Entity<DailyLog>().HasIndex(l => new { l.AccountId, l.ProjectId });
        b.Entity<TodoItem>().HasIndex(t => new { t.AccountId, t.ProjectId });
        b.Entity<LogPhoto>().HasIndex(p => new { p.AccountId, p.ProjectId });
        b.Entity<LogPhoto>().HasIndex(p => p.LogId);

        // Estimates and change orders share a table, numbered separately.
        // Deleting a job deletes its estimates, and deleting an estimate
        // deletes its line items (the database does this).
        b.Entity<Estimate>().HasIndex(e => new { e.AccountId, e.Kind, e.Number }).IsUnique();
        b.Entity<Estimate>().HasIndex(e => new { e.AccountId, e.ProjectId });
        b.Entity<Estimate>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Estimate>().Property(e => e.MarkupPercent).HasPrecision(7, 2);
        b.Entity<Estimate>().Property(e => e.TaxPercent).HasPrecision(6, 3);
        b.Entity<EstimateItem>().HasOne<Estimate>().WithMany().HasForeignKey(i => i.EstimateId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EstimateItem>().Property(i => i.Quantity).HasPrecision(12, 3);
        b.Entity<EstimateItem>().Property(i => i.UnitCost).HasPrecision(12, 2);

        // Invoices are numbered per company. Deleting a job deletes its
        // invoices, and deleting an invoice deletes its lines and payments.
        b.Entity<Invoice>().HasIndex(i => new { i.AccountId, i.Number }).IsUnique();
        b.Entity<Invoice>().HasIndex(i => new { i.AccountId, i.ProjectId });
        b.Entity<Invoice>().HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvoiceLine>().HasOne<Invoice>().WithMany().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvoiceLine>().Property(l => l.Amount).HasPrecision(12, 2);
        b.Entity<Payment>().HasOne<Invoice>().WithMany().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Payment>().HasIndex(p => new { p.AccountId, p.InvoiceId });
        b.Entity<Payment>().Property(p => p.Amount).HasPrecision(12, 2);
    }
}
