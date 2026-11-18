namespace BuildFree.Api;

// Every contractor company is one Account. Everything else carries an
// AccountId so one company can never see another company's data.
public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ProjectStatus { Planning, Active, OnHold, Complete }

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DailyLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid ProjectId { get; set; }
    public string Author { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Notes { get; set; } = "";
    public int? CrewCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? DueDate { get; set; }
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Request shapes (what the API accepts)
public record SignupRequest(string CompanyName, string Name, string Email, string Password);
public record LoginRequest(string Email, string Password);
public record ClientRequest(string Name, string? Email, string? Phone);
public record ProjectRequest(string Name, string? Address, Guid? ClientId, DateOnly? StartDate);
public record StatusRequest(int Status);
public record LogRequest(string Notes, int? CrewCount, DateOnly? Date);
public record TodoRequest(string Title, DateOnly? DueDate);
