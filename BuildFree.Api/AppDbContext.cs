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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<Client>().HasIndex(c => c.AccountId);
        b.Entity<Project>().HasIndex(p => p.AccountId);
        b.Entity<DailyLog>().HasIndex(l => new { l.AccountId, l.ProjectId });
        b.Entity<TodoItem>().HasIndex(t => new { t.AccountId, t.ProjectId });
        b.Entity<LogPhoto>().HasIndex(p => new { p.AccountId, p.ProjectId });
        b.Entity<LogPhoto>().HasIndex(p => p.LogId);
    }
}
