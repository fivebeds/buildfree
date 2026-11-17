using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BuildFree.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var keyBytes = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!);

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

// Dev only: creates tables on startup. Replace with EF migrations before launch.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
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
            new Claim("accountId", u.AccountId.ToString())
        },
        expires: DateTime.UtcNow.AddDays(7),
        signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);

// ---------- Auth ----------

app.MapPost("/api/auth/signup", async (SignupRequest r, AppDbContext db) =>
{
    var email = r.Email.Trim().ToLowerInvariant();
    if (r.Password.Length < 8)
        return Results.BadRequest(new { error = "Password must be at least 8 characters." });
    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { error = "That email is already registered." });

    var account = new Account { Name = r.CompanyName.Trim() };
    var user = new User
    {
        AccountId = account.Id,
        Email = email,
        Name = r.Name.Trim(),
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password)
    };
    db.Accounts.Add(account);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { token = MakeToken(user) });
});

app.MapPost("/api/auth/login", async (LoginRequest r, AppDbContext db) =>
{
    var email = r.Email.Trim().ToLowerInvariant();
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
    var client = new Client
    {
        AccountId = AccountId(me),
        Name = r.Name.Trim(),
        Email = r.Email,
        Phone = r.Phone
    };
    db.Clients.Add(client);
    await db.SaveChangesAsync();
    return Results.Created($"/api/clients/{client.Id}", client);
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

    // Make sure the client belongs to this same company.
    if (r.ClientId is Guid cid &&
        !await db.Clients.AnyAsync(c => c.Id == cid && c.AccountId == accountId))
        return Results.BadRequest(new { error = "Client not found." });

    var project = new Project
    {
        AccountId = accountId,
        ClientId = r.ClientId,
        Name = r.Name.Trim(),
        Address = r.Address,
        StartDate = r.StartDate
    };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Created($"/api/projects/{project.Id}", project);
});

app.Run();
