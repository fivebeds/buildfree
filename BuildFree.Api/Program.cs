using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using BuildFree.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// The signing key proves a login token came from this server. Refuse to start
// online with the sample key from appsettings.json, since anyone can read it.
var jwtKey = builder.Configuration["Jwt:Key"] ?? "";
if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");
if (!builder.Environment.IsDevelopment() && jwtKey.StartsWith("CHANGE-ME"))
    throw new InvalidOperationException("Set a real Jwt:Key (for example the Jwt__Key environment variable) before running outside development.");
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes)
        };
    });
builder.Services.AddAuthorization();

// Lets a frontend on another port call the API while developing.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Database setup on startup. Once a migration exists (see README), the
// database is upgraded in place and keeps its data. Until then, tables are
// created directly, which is fine for development only.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.GetMigrations().Any()) db.Database.Migrate();
    else db.Database.EnsureCreated();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

string MakeToken(User u)
{
    var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        claims: new[]
        {
            new Claim("userId", u.Id.ToString()),
            new Claim("accountId", u.AccountId.ToString()),
            new Claim("name", u.Name)
        },
        expires: DateTime.UtcNow.AddDays(7),
        signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);

// ---------- Input helpers ----------

// Trims text and turns empty text into null.
static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

static IResult Bad(string message) => Results.BadRequest(new { error = message });

static bool TooLong(string? s, int max) => s is not null && s.Length > max;

static bool LooksLikeEmail(string? s)
{
    if (s is null || s.Length > 254) return false;
    return MailAddress.TryCreate(s, out var parsed) && parsed.Address == s;
}

// Shared checks for creating and editing clients and projects.
static IResult? CheckClient(ClientRequest r)
{
    var name = Clean(r.Name);
    if (name is null) return Bad("Client name is required.");
    if (TooLong(name, 200)) return Bad("Client name is too long.");
    var email = Clean(r.Email);
    if (email is not null && !LooksLikeEmail(email)) return Bad("That email address doesn't look right.");
    if (TooLong(Clean(r.Phone), 40)) return Bad("Phone number is too long.");
    return null;
}

static IResult? CheckProject(ProjectRequest r)
{
    var name = Clean(r.Name);
    if (name is null) return Bad("Job name is required.");
    if (TooLong(name, 200)) return Bad("Job name is too long.");
    if (TooLong(Clean(r.Address), 300)) return Bad("Address is too long.");
    if (r.StartDate is DateOnly s && r.EndDate is DateOnly e && e < s)
        return Bad("End date can't be before the start date.");
    return null;
}

// ---------- Auth ----------

app.MapPost("/api/auth/signup", async (SignupRequest r, AppDbContext db) =>
{
    var company = Clean(r.CompanyName);
    var name = Clean(r.Name);
    var email = Clean(r.Email)?.ToLowerInvariant();
    if (company is null || name is null) return Bad("Company name and your name are required.");
    if (TooLong(company, 200) || TooLong(name, 200)) return Bad("Name is too long.");
    if (!LooksLikeEmail(email)) return Bad("Enter a valid email address.");
    if (r.Password is null || r.Password.Length < 8)
        return Bad("Password must be at least 8 characters.");
    if (r.Password.Length > 200) return Bad("Password is too long.");
    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { error = "That email is already registered." });

    var account = new Account { Name = company };
    var user = new User
    {
        AccountId = account.Id,
        Email = email!,
        Name = name,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password)
    };
    db.Accounts.Add(account);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { token = MakeToken(user) });
});

app.MapPost("/api/auth/login", async (LoginRequest r, AppDbContext db) =>
{
    var email = Clean(r.Email)?.ToLowerInvariant();
    if (email is null || string.IsNullOrEmpty(r.Password)) return Results.Unauthorized();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user is null || !BCrypt.Net.BCrypt.Verify(r.Password, user.PasswordHash))
        return Results.Unauthorized();
    return Results.Ok(new { token = MakeToken(user) });
});

// ---------- Clients ----------

var clients = app.MapGroup("/api/clients").RequireAuthorization();

clients.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    return await db.Clients.Where(c => c.AccountId == accountId)
        .OrderBy(c => c.Name).ToListAsync();
});

clients.MapPost("/", async (ClientRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    if (CheckClient(r) is IResult bad) return bad;
    var client = new Client
    {
        AccountId = AccountId(me),
        Name = Clean(r.Name)!,
        Email = Clean(r.Email),
        Phone = Clean(r.Phone)
    };
    db.Clients.Add(client);
    await db.SaveChangesAsync();
    return Results.Created($"/api/clients/{client.Id}", client);
});

clients.MapPut("/{id:guid}", async (Guid id, ClientRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.AccountId == accountId);
    if (client is null) return Results.NotFound();
    if (CheckClient(r) is IResult bad) return bad;
    client.Name = Clean(r.Name)!;
    client.Email = Clean(r.Email);
    client.Phone = Clean(r.Phone);
    await db.SaveChangesAsync();
    return Results.Ok(client);
});

// Deleting a client keeps their jobs; the jobs just lose the client link.
clients.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.AccountId == accountId);
    if (client is null) return Results.NotFound();
    var jobs = await db.Projects.Where(p => p.ClientId == id && p.AccountId == accountId).ToListAsync();
    foreach (var job in jobs) job.ClientId = null;
    db.Clients.Remove(client);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---------- Projects ----------

var projects = app.MapGroup("/api/projects").RequireAuthorization();

projects.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    return await db.Projects.Where(p => p.AccountId == accountId)
        .OrderByDescending(p => p.CreatedAt).ToListAsync();
});

projects.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    return project is null ? Results.NotFound() : Results.Ok(project);
});

projects.MapPost("/", async (ProjectRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    if (CheckProject(r) is IResult bad) return bad;

    // Make sure the client belongs to this same company.
    if (r.ClientId is Guid cid &&
        !await db.Clients.AnyAsync(c => c.Id == cid && c.AccountId == accountId))
        return Bad("Client not found.");

    var project = new Project
    {
        AccountId = accountId,
        ClientId = r.ClientId,
        Name = Clean(r.Name)!,
        Address = Clean(r.Address),
        StartDate = r.StartDate,
        EndDate = r.EndDate
    };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Created($"/api/projects/{project.Id}", project);
});

projects.MapPut("/{id:guid}", async (Guid id, ProjectRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (project is null) return Results.NotFound();
    if (CheckProject(r) is IResult bad) return bad;
    if (r.ClientId is Guid cid &&
        !await db.Clients.AnyAsync(c => c.Id == cid && c.AccountId == accountId))
        return Bad("Client not found.");

    project.Name = Clean(r.Name)!;
    project.Address = Clean(r.Address);
    project.ClientId = r.ClientId;
    project.StartDate = r.StartDate;
    project.EndDate = r.EndDate;
    await db.SaveChangesAsync();
    return Results.Ok(project);
});

// Deleting a job also deletes its daily logs and to-dos.
projects.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (project is null) return Results.NotFound();
    db.Logs.RemoveRange(await db.Logs.Where(l => l.ProjectId == id && l.AccountId == accountId).ToListAsync());
    db.Todos.RemoveRange(await db.Todos.Where(t => t.ProjectId == id && t.AccountId == accountId).ToListAsync());
    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---------- Job status, daily logs, to-dos ----------

projects.MapPut("/{id:guid}/status", async (Guid id, StatusRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (project is null) return Results.NotFound();
    if (!Enum.IsDefined(typeof(ProjectStatus), r.Status)) return Bad("Unknown status.");
    project.Status = (ProjectStatus)r.Status;
    await db.SaveChangesAsync();
    return Results.Ok(project);
});

projects.MapGet("/{id:guid}/logs", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    return await db.Logs.Where(l => l.ProjectId == id && l.AccountId == accountId)
        .OrderByDescending(l => l.Date).ThenByDescending(l => l.CreatedAt).ToListAsync();
});

static IResult? CheckLog(LogRequest r)
{
    var notes = Clean(r.Notes);
    if (notes is null) return Bad("Write a note for the log.");
    if (TooLong(notes, 10000)) return Bad("Log notes are too long.");
    if (r.CrewCount is < 0 or > 1000) return Bad("Crew count must be between 0 and 1,000.");
    return null;
}

projects.MapPost("/{id:guid}/logs", async (Guid id, LogRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    if (!await db.Projects.AnyAsync(p => p.Id == id && p.AccountId == accountId))
        return Results.NotFound();
    if (CheckLog(r) is IResult bad) return bad;
    var log = new DailyLog
    {
        AccountId = accountId,
        ProjectId = id,
        Author = me.FindFirstValue("name") ?? "Unknown",
        Date = r.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
        Notes = Clean(r.Notes)!,
        CrewCount = r.CrewCount
    };
    db.Logs.Add(log);
    await db.SaveChangesAsync();
    return Results.Created($"/api/logs/{log.Id}", log);
});

var logs = app.MapGroup("/api/logs").RequireAuthorization();

logs.MapPut("/{id:guid}", async (Guid id, LogRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var log = await db.Logs.FirstOrDefaultAsync(l => l.Id == id && l.AccountId == accountId);
    if (log is null) return Results.NotFound();
    if (CheckLog(r) is IResult bad) return bad;
    log.Notes = Clean(r.Notes)!;
    log.CrewCount = r.CrewCount;
    if (r.Date is DateOnly d) log.Date = d;
    await db.SaveChangesAsync();
    return Results.Ok(log);
});

logs.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var log = await db.Logs.FirstOrDefaultAsync(l => l.Id == id && l.AccountId == accountId);
    if (log is null) return Results.NotFound();
    db.Logs.Remove(log);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

static IResult? CheckTodo(TodoRequest r)
{
    var title = Clean(r.Title);
    if (title is null) return Bad("To-do text is required.");
    if (TooLong(title, 500)) return Bad("To-do text is too long.");
    return null;
}

projects.MapPost("/{id:guid}/todos", async (Guid id, TodoRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    if (!await db.Projects.AnyAsync(p => p.Id == id && p.AccountId == accountId))
        return Results.NotFound();
    if (CheckTodo(r) is IResult bad) return bad;
    var todo = new TodoItem { AccountId = accountId, ProjectId = id, Title = Clean(r.Title)!, DueDate = r.DueDate };
    db.Todos.Add(todo);
    await db.SaveChangesAsync();
    return Results.Created($"/api/todos/{todo.Id}", todo);
});

var todos = app.MapGroup("/api/todos").RequireAuthorization();

todos.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    return await db.Todos.Where(t => t.AccountId == accountId)
        .OrderBy(t => t.Done).ThenBy(t => t.DueDate).ToListAsync();
});

todos.MapPut("/{id:guid}", async (Guid id, TodoRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
    if (todo is null) return Results.NotFound();
    if (CheckTodo(r) is IResult bad) return bad;
    todo.Title = Clean(r.Title)!;
    todo.DueDate = r.DueDate;
    await db.SaveChangesAsync();
    return Results.Ok(todo);
});

todos.MapPost("/{id:guid}/toggle", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
    if (todo is null) return Results.NotFound();
    todo.Done = !todo.Done;
    await db.SaveChangesAsync();
    return Results.Ok(todo);
});

todos.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId);
    if (todo is null) return Results.NotFound();
    db.Todos.Remove(todo);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();
