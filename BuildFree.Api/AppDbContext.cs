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

        // Deleting a job deletes its estimates, and deleting an estimate
        // deletes its line items (the database does this).
        b.Entity<Estimate>().HasIndex(e => new { e.AccountId, e.Number }).IsUnique();
        b.Entity<Estimate>().HasIndex(e => new { e.AccountId, e.ProjectId });
        b.Entity<Estimate>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Estimate>().Property(e => e.MarkupPercent).HasPrecision(7, 2);
        b.Entity<Estimate>().Property(e => e.TaxPercent).HasPrecision(6, 3);
        b.Entity<EstimateItem>().HasOne<Estimate>().WithMany().HasForeignKey(i => i.EstimateId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EstimateItem>().Property(i => i.Quantity).HasPrecision(12, 3);
        b.Entity<EstimateItem>().Property(i => i.UnitCost).HasPrecision(12, 2);
    }
}
