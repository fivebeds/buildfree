using System.Text.Json.Serialization;

namespace BuildFree.Api;

// Every contractor company is one Account. Everything else carries an
// AccountId so one company can never see another company's data.
public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// What a team member is allowed to do. Owner: everything, including the team.
// Office: everything except managing the team. Field: sees jobs, posts daily
// logs and photos, checks off to-dos, and edits only their own logs and photos.
public enum UserRole { Owner, Office, Field }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonIgnore] public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Owner;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// An invitation for someone to join a company. The owner shares a join link
// that contains a one-time code; only a hash of the code is stored.
public class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Field;
    [JsonIgnore] public string CodeHash { get; set; } = "";
    public string InvitedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
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
    public Guid? AuthorId { get; set; }
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

// A job-site photo attached to a daily log. The image file is kept in the
// photo store (see PhotoStore.cs); StorageKey says where, and is never sent
// to the browser.
public class LogPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid LogId { get; set; }
    [JsonIgnore] public string StorageKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Caption { get; set; }
    public string UploadedBy { get; set; } = "";
    public Guid? UploadedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum EstimateStatus { Draft, Sent, Approved, Declined }
public enum EstimateCategory { Labor, Materials, Subcontractor, Equipment, Other }

// A priced estimate for a job. Line items are entered at cost; the customer
// price is cost plus MarkupPercent. TaxPercent applies to materials only.
public class Estimate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public EstimateStatus Status { get; set; } = EstimateStatus.Draft;
    public string? Notes { get; set; }
    public decimal MarkupPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class EstimateItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstimateId { get; set; }
    public int Position { get; set; }
    public string Description { get; set; } = "";
    public EstimateCategory Category { get; set; }
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal UnitCost { get; set; }
}

// Request shapes (what the API accepts). Strings can arrive as null when a
// field is left out of the JSON, so the endpoints check them before use.
public record SignupRequest(string? CompanyName, string? Name, string? Email, string? Password);
public record LoginRequest(string? Email, string? Password);
public record ClientRequest(string? Name, string? Email, string? Phone);
public record ProjectRequest(string? Name, string? Address, Guid? ClientId, DateOnly? StartDate, DateOnly? EndDate = null);
public record StatusRequest(int Status);
public record LogRequest(string? Notes, int? CrewCount, DateOnly? Date);
public record TodoRequest(string? Title, DateOnly? DueDate);
public record PhotoRequest(string? Caption);
public record InviteRequest(string? Name, string? Email, int Role);
public record RoleRequest(int Role);
public record AcceptInviteRequest(string? Code, string? Name, string? Password);
public record EstimateItemRequest(string? Description, int Category, decimal Quantity, string? Unit, decimal UnitCost);
public record EstimateRequest(string? Title, string? Notes, decimal? MarkupPercent, decimal? TaxPercent, List<EstimateItemRequest>? Items);
