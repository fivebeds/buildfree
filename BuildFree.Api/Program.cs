using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using BuildFree.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// The database. A DATABASE_URL setting (postgres://...), which many hosts
// provide, wins over ConnectionStrings:Default.
var connectionString = HostSettings.FromDatabaseUrl(builder.Configuration["DATABASE_URL"])
    ?? builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

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
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
        };
    });
builder.Services.AddAuthorization();

// Job-site photos (see PhotoStore.cs). By default they're files in a folder:
// Photos:Path, or "photo-uploads" next to the API. Online, use S3-compatible
// storage such as Cloudflare R2 instead (Photos:Storage = S3), because many
// hosts wipe their disks on every deploy.
if (string.Equals(builder.Configuration["Photos:Storage"], "S3", StringComparison.OrdinalIgnoreCase))
{
    string S3Setting(string name) => builder.Configuration["Photos:S3:" + name] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set Photos:S3:{name} (or Photos__S3__{name}) to store photos in S3.");
    builder.Services.AddSingleton<IPhotoStore>(new S3PhotoStore(S3Setting("ServiceUrl"), S3Setting("Bucket"),
        S3Setting("AccessKey"), S3Setting("SecretKey"), builder.Configuration["Photos:S3:Region"] ?? "auto"));
}
else
{
    var photosPath = builder.Configuration["Photos:Path"];
    if (string.IsNullOrWhiteSpace(photosPath))
        photosPath = Path.Combine(builder.Environment.ContentRootPath, "photo-uploads");
    builder.Services.AddSingleton<IPhotoStore>(new DiskPhotoStore(photosPath));
}

// Online, a hosting provider's proxy sits in front of the API and passes the
// visitor's address and https along in X-Forwarded-* headers. Trust them only
// when the API can't be reached any other way, by setting
// Proxy:TrustForwardedHeaders to true. Without it, every visitor would share
// one login limit (the proxy's address).
var trustProxy = builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false);
if (trustProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        o.ForwardLimit = 1;
    });

// Which websites may call the API from a browser. Set Cors:Origins (or the
// Cors__Origins environment variable) to the web app's address, for example
// "https://app.buildfree.org"; separate several with commas. In development
// with nothing set, any site may call it, so web/index.html works from disk.
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var corsAnyOrigin = corsOrigins.Length == 0 && builder.Environment.IsDevelopment();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (corsAnyOrigin) p.AllowAnyOrigin(); else p.WithOrigins(corsOrigins);
    p.AllowAnyHeader().AllowAnyMethod();
}));

// Limits how often one network can try to log in, sign up, use invite or reset
// links, or change a password: RateLimits:AuthPerMinute tries a minute (60 by
// default). Wrong passwords are also limited per email; see LoginGuard.
var authPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 60);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (rejected, cancel) =>
    {
        rejected.HttpContext.Response.Headers.RetryAfter = "60";
        await rejected.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many tries. Wait a minute and try again." }, cancel);
    };
});

// Requests the API can't read (broken JSON, a word where a number goes) are
// turned into a clear 400 by the error handler below instead of an empty one.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

var app = builder.Build();

if (trustProxy) app.UseForwardedHeaders();

// Basic browser protections on every response: don't guess file types, don't
// show the API inside another site's frame, and don't leak addresses.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var headers = ctx.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next();
});

// Unexpected errors come back as a short JSON message the web app can show;
// the full details go to the log. The CORS header is added back here, because
// the error handler clears the response and the browser would otherwise hide
// the message.
app.UseExceptionHandler(errors => errors.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = error is BadHttpRequestException bad
        ? (bad.StatusCode, bad.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? "That upload is too large. Try fewer or smaller photos."
            : "The request couldn't be read. Check the fields and try again.")
        : (StatusCodes.Status500InternalServerError, "Something went wrong on our side. Try again, and if it keeps happening, let us know.");
    ctx.Response.StatusCode = status;
    var origin = ctx.Request.Headers.Origin.ToString();
    if (corsAnyOrigin) ctx.Response.Headers.AccessControlAllowOrigin = "*";
    else if (corsOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) ctx.Response.Headers.AccessControlAllowOrigin = origin;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

// Database setup on startup. Once a migration exists (see README), the
// database is upgraded in place and keeps its data. Until then, tables are
// created directly, which is fine for development only.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.GetMigrations().Any()) db.Database.Migrate();
    else db.Database.EnsureCreated();
}

// Online, browsers are told to always use https for this site.
if (!app.Environment.IsDevelopment()) app.UseHsts();

// The web app is served from the same address as the API, so it works online
// without any extra setup. Files are checked for changes on every visit.
if (HostSettings.WebFolder(app.Environment.ContentRootPath, app.Configuration["Web:Path"]) is string webFolder)
{
    var webFiles = new PhysicalFileProvider(webFolder);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = webFiles,
        OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache"
    });
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();

// Every signed-in request loads the current user from the database, so a
// removed team member is locked out right away and role changes apply
// immediately, even though their login token is still valid. A token from
// before the person's last password change is refused too.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        var user = Guid.TryParse(ctx.User.FindFirstValue("userId"), out var uid)
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid)
            : null;
        if (user is null || ctx.User.FindFirstValue("pwd") != Passwords.Stamp(user.PasswordHash))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        ctx.Items["me"] = user;
    }
    await next();
});

app.UseAuthorization();

// Photo limits. The web app shrinks photos before uploading, so real uploads
// are far below these.
const int MaxPhotosPerUpload = 10;
const int MaxPhotosPerLog = 30;
const long MaxPhotoBytes = 10L * 1024 * 1024;       // 10 MB per photo
const long MaxUploadBytes = 50L * 1024 * 1024;      // 50 MB per upload

string MakeToken(User u)
{
    var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        claims: new[]
        {
            new Claim("userId", u.Id.ToString()),
            new Claim("accountId", u.AccountId.ToString()),
            new Claim("name", u.Name),
            new Claim("pwd", Passwords.Stamp(u.PasswordHash))
        },
        expires: DateTime.UtcNow.AddDays(7),
        signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

Guid AccountId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("accountId")!);

// Checked when an email isn't registered, so a login takes the same time
// either way and doesn't reveal which emails have accounts.
var dummyHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

// The user a password reset code is for, if the code is genuine, unexpired and unused.
async Task<User?> ResetUser(AppDbContext db, string? code)
{
    if (ResetCodes.UserId(code) is not Guid uid) return null;
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid);
    return user is not null && ResetCodes.IsFor(keyBytes, code!, user) ? user : null;
}
const string ResetLinkBroken = "This reset link is invalid, expired or already used. Ask your company's owner for a new one.";

// ---------- Team roles ----------

// The signed-in user, loaded by the middleware above.
static User Me(HttpContext ctx) => (User)ctx.Items["me"]!;

static IResult Forbidden(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

// Owners and office staff can manage clients and jobs; field crew can't.
static async ValueTask<object?> OfficeOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var me = ctx.HttpContext.Items["me"] as User;
    if (me is null || me.Role == UserRole.Field)
        return Forbidden("Only the owner or office staff can do that.");
    return await next(ctx);
}

// Only owners can invite people, change roles or remove team members.
static async ValueTask<object?> OwnerOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var me = ctx.HttpContext.Items["me"] as User;
    if (me is null || me.Role != UserRole.Owner)
        return Forbidden("Only the owner can manage the team.");
    return await next(ctx);
}

// Field crew can change only what they created themselves.
static bool CanChange(User me, Guid? createdBy) => me.Role != UserRole.Field || createdBy == me.Id;

static string InviteHash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

static object MemberView(User u) => new { u.Id, u.Name, u.Email, u.Role, u.CreatedAt };

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

// Works out the image type from the first bytes of the file instead of
// trusting the file name, so only real photos are accepted.
static string? DetectImageType(ReadOnlySpan<byte> b)
{
    if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
    if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
    if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
    if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
    return null;
}

// Keeps only the file name part (no folders) and a sensible length.
static string SafeFileName(string? name)
{
    var clean = Clean(Path.GetFileName(name ?? ""));
    if (clean is null) return "photo";
    return clean.Length > 200 ? clean[..200] : clean;
}

// Removes photo files after their database rows are gone. A file that fails
// to delete is only wasted space, so it never fails the request.
static async Task DeletePhotoFiles(IEnumerable<LogPhoto> photos, IPhotoStore store, ILogger logger)
{
    foreach (var photo in photos)
    {
        try { await store.DeleteAsync(photo.StorageKey); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not delete photo file {Key}", photo.StorageKey); }
    }
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

// ---------- Health ----------

// For the hosting provider's health check: 200 when the API is up and can
// reach the database, 503 when it can't.
app.MapGet("/api/health", async (AppDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "The database can't be reached." }, statusCode: StatusCodes.Status503ServiceUnavailable));

// ---------- Auth ----------

app.MapPost("/api/auth/signup", async (SignupRequest r, AppDbContext db) =>
{
    var company = Clean(r.CompanyName);
    var name = Clean(r.Name);
    var email = Clean(r.Email)?.ToLowerInvariant();
    if (company is null || name is null) return Bad("Company name and your name are required.");
    if (TooLong(company, 200) || TooLong(name, 200)) return Bad("Name is too long.");
    if (!LooksLikeEmail(email)) return Bad("Enter a valid email address.");
    if (Passwords.Problem(r.Password) is string problem) return Bad(problem);
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
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (LoginRequest r, AppDbContext db) =>
{
    var email = Clean(r.Email)?.ToLowerInvariant();
    if (email is null || string.IsNullOrEmpty(r.Password)) return Results.Unauthorized();
    if (LoginGuard.MinutesLocked(email) is int minutes)
        return Results.Json(new { error = $"Too many wrong passwords for this email. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}, or ask your company's owner for a password reset link." },
            statusCode: StatusCodes.Status429TooManyRequests);
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    var correct = BCrypt.Net.BCrypt.Verify(r.Password, user?.PasswordHash ?? dummyHash);
    if (user is null || !correct)
    {
        LoginGuard.Failed(email);
        return Results.Unauthorized();
    }
    LoginGuard.Clear(email);
    return Results.Ok(new { token = MakeToken(user) });
}).RequireRateLimiting("auth");

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
}).AddEndpointFilter(OfficeOnly);

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
}).AddEndpointFilter(OfficeOnly);

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
}).AddEndpointFilter(OfficeOnly);

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
}).AddEndpointFilter(OfficeOnly);

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
}).AddEndpointFilter(OfficeOnly);

// Deleting a job also deletes its daily logs, their photos, its to-dos, and
// its draft estimates and invoices. A job with an approved estimate or change
// order, or an invoice that went out, holds real money records, so it can't
// be deleted; mark it Complete instead.
projects.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, IPhotoStore store, ILogger<Program> logger) =>
{
    var accountId = AccountId(me);
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (project is null) return Results.NotFound();
    if (await db.Estimates.AnyAsync(e => e.ProjectId == id && e.Status == EstimateStatus.Approved) ||
        await db.Invoices.AnyAsync(i => i.ProjectId == id && i.Status != InvoiceStatus.Draft))
        return Results.Conflict(new { error = "This job has an approved estimate or change order, or invoices that went out, so it can't be deleted. Mark it Complete instead, so its records are kept." });
    var photos = await db.Photos.Where(p => p.ProjectId == id && p.AccountId == accountId).ToListAsync();
    db.Photos.RemoveRange(photos);
    db.Logs.RemoveRange(await db.Logs.Where(l => l.ProjectId == id && l.AccountId == accountId).ToListAsync());
    db.Todos.RemoveRange(await db.Todos.Where(t => t.ProjectId == id && t.AccountId == accountId).ToListAsync());
    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    await DeletePhotoFiles(photos, store, logger);
    return Results.NoContent();
}).AddEndpointFilter(OfficeOnly);

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
}).AddEndpointFilter(OfficeOnly);

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

projects.MapPost("/{id:guid}/logs", async (Guid id, LogRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
{
    var accountId = AccountId(me);
    if (!await db.Projects.AnyAsync(p => p.Id == id && p.AccountId == accountId))
        return Results.NotFound();
    if (CheckLog(r) is IResult bad) return bad;
    var log = new DailyLog
    {
        AccountId = accountId,
        ProjectId = id,
        Author = Me(ctx).Name,
        AuthorId = Me(ctx).Id,
        Date = r.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
        Notes = Clean(r.Notes)!,
        CrewCount = r.CrewCount
    };
    db.Logs.Add(log);
    await db.SaveChangesAsync();
    return Results.Created($"/api/logs/{log.Id}", log);
});

var logs = app.MapGroup("/api/logs").RequireAuthorization();

logs.MapPut("/{id:guid}", async (Guid id, LogRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var log = await db.Logs.FirstOrDefaultAsync(l => l.Id == id && l.AccountId == accountId);
    if (log is null) return Results.NotFound();
    if (!CanChange(Me(ctx), log.AuthorId)) return Forbidden("Field crew can only edit their own daily logs.");
    if (CheckLog(r) is IResult bad) return bad;
    log.Notes = Clean(r.Notes)!;
    log.CrewCount = r.CrewCount;
    if (r.Date is DateOnly d) log.Date = d;
    await db.SaveChangesAsync();
    return Results.Ok(log);
});

// Deleting a log also deletes its photos.
logs.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db, IPhotoStore store, ILogger<Program> logger) =>
{
    var accountId = AccountId(me);
    var log = await db.Logs.FirstOrDefaultAsync(l => l.Id == id && l.AccountId == accountId);
    if (log is null) return Results.NotFound();
    if (!CanChange(Me(ctx), log.AuthorId)) return Forbidden("Field crew can only delete their own daily logs.");
    var photos = await db.Photos.Where(p => p.LogId == id && p.AccountId == accountId).ToListAsync();
    db.Photos.RemoveRange(photos);
    db.Logs.Remove(log);
    await db.SaveChangesAsync();
    await DeletePhotoFiles(photos, store, logger);
    return Results.NoContent();
});

// ---------- Photos on daily logs ----------

// Every photo on a job, newest first (the web app groups them by log).
projects.MapGet("/{id:guid}/photos", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    return await db.Photos.Where(p => p.ProjectId == id && p.AccountId == accountId)
        .OrderByDescending(p => p.CreatedAt).ToListAsync();
});

logs.MapGet("/{id:guid}/photos", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    return await db.Photos.Where(p => p.LogId == id && p.AccountId == accountId)
        .OrderBy(p => p.CreatedAt).ToListAsync();
});

// Upload one or more photos to a log as multipart/form-data: each file in a
// field named "photos", plus an optional "caption" for all of them.
logs.MapPost("/{id:guid}/photos", async (Guid id, HttpRequest req, ClaimsPrincipal me, HttpContext ctx, AppDbContext db, IPhotoStore store, ILogger<Program> logger) =>
{
    var accountId = AccountId(me);
    var log = await db.Logs.FirstOrDefaultAsync(l => l.Id == id && l.AccountId == accountId);
    if (log is null) return Results.NotFound();
    if (!req.HasFormContentType) return Bad("Send photos as a form upload in a field named \"photos\".");

    var form = await req.ReadFormAsync();
    var files = form.Files.GetFiles("photos");
    if (files.Count == 0) return Bad("Choose at least one photo.");
    if (files.Count > MaxPhotosPerUpload) return Bad($"Upload up to {MaxPhotosPerUpload} photos at a time.");
    var already = await db.Photos.CountAsync(p => p.LogId == id && p.AccountId == accountId);
    if (already + files.Count > MaxPhotosPerLog) return Bad($"A daily log can hold up to {MaxPhotosPerLog} photos.");
    var caption = Clean(form["caption"]);
    if (TooLong(caption, 500)) return Bad("Caption is too long.");

    // Check every file before saving any, so an upload either fully works or changes nothing.
    var accepted = new List<(IFormFile File, string Type)>();
    long total = 0;
    foreach (var file in files)
    {
        var name = SafeFileName(file.FileName);
        if (file.Length == 0) return Bad($"\"{name}\" is empty.");
        if (file.Length > MaxPhotoBytes) return Bad($"\"{name}\" is larger than 10 MB.");
        total += file.Length;
        var head = new byte[12];
        int read;
        await using (var peek = file.OpenReadStream())
            read = await peek.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
        var type = DetectImageType(head.AsSpan(0, read));
        if (type is null) return Bad($"\"{name}\" isn't a JPEG, PNG, GIF or WebP photo.");
        accepted.Add((file, type));
    }
    if (total > MaxUploadBytes) return Bad("That upload is too large. Try fewer photos at a time.");

    var author = Me(ctx).Name;
    var authorId = Me(ctx).Id;
    var saved = new List<LogPhoto>();
    try
    {
        foreach (var (file, type) in accepted)
        {
            var photo = new LogPhoto
            {
                AccountId = accountId,
                ProjectId = log.ProjectId,
                LogId = log.Id,
                FileName = SafeFileName(file.FileName),
                ContentType = type,
                SizeBytes = file.Length,
                Caption = caption,
                UploadedBy = author,
                UploadedById = authorId
            };
            photo.StorageKey = $"{accountId}/{photo.Id}";
            await using (var content = file.OpenReadStream())
                await store.SaveAsync(photo.StorageKey, content);
            saved.Add(photo);
        }
        db.Photos.AddRange(saved);
        await db.SaveChangesAsync();
    }
    catch
    {
        // Don't leave orphan files behind if anything failed part way.
        await DeletePhotoFiles(saved, store, logger);
        throw;
    }
    return Results.Created($"/api/logs/{log.Id}/photos", saved);
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes + 1024 * 1024));

var photoRoutes = app.MapGroup("/api/photos").RequireAuthorization();

// The image itself. Only the company that owns the photo can load it.
photoRoutes.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, IPhotoStore store, HttpResponse res) =>
{
    var accountId = AccountId(me);
    var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (photo is null) return Results.NotFound();
    var stream = await store.OpenAsync(photo.StorageKey);
    if (stream is null) return Results.NotFound();
    res.Headers.CacheControl = "private, max-age=86400";
    res.Headers.XContentTypeOptions = "nosniff";
    return Results.Stream(stream, photo.ContentType);
});

photoRoutes.MapPut("/{id:guid}", async (Guid id, PhotoRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (photo is null) return Results.NotFound();
    if (!CanChange(Me(ctx), photo.UploadedById)) return Forbidden("Field crew can only change their own photos.");
    var caption = Clean(r.Caption);
    if (TooLong(caption, 500)) return Bad("Caption is too long.");
    photo.Caption = caption;
    await db.SaveChangesAsync();
    return Results.Ok(photo);
});

photoRoutes.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db, IPhotoStore store, ILogger<Program> logger) =>
{
    var accountId = AccountId(me);
    var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == id && p.AccountId == accountId);
    if (photo is null) return Results.NotFound();
    if (!CanChange(Me(ctx), photo.UploadedById)) return Forbidden("Field crew can only delete their own photos.");
    db.Photos.Remove(photo);
    await db.SaveChangesAsync();
    await DeletePhotoFiles(new[] { photo }, store, logger);
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

// ---------- Team members ----------

app.MapGet("/api/me", async (HttpContext ctx, AppDbContext db) =>
{
    var me = Me(ctx);
    var company = await db.Accounts.Where(a => a.Id == me.AccountId).Select(a => a.Name).FirstOrDefaultAsync();
    return Results.Ok(new { me.Id, me.Name, me.Email, me.Role, Company = company });
}).RequireAuthorization();

var team = app.MapGroup("/api/team").RequireAuthorization();

// Everyone on the team can see who else is on it. Pending invites are listed too.
team.MapGet("/", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var members = await db.Users.Where(u => u.AccountId == accountId).OrderBy(u => u.CreatedAt).ToListAsync();
    var invites = await db.Invites.Where(i => i.AccountId == accountId && i.ExpiresAt > DateTime.UtcNow)
        .OrderBy(i => i.CreatedAt).ToListAsync();
    return Results.Ok(new { members = members.Select(MemberView), invites });
});

// Creates an invite and returns its one-time code. The code is only shown
// here, so the owner copies the join link right away.
team.MapPost("/invites", async (InviteRequest r, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var name = Clean(r.Name);
    var email = Clean(r.Email)?.ToLowerInvariant();
    if (name is null) return Bad("Enter the person's name.");
    if (TooLong(name, 200)) return Bad("Name is too long.");
    if (!LooksLikeEmail(email)) return Bad("Enter a valid email address.");
    if (!Enum.IsDefined(typeof(UserRole), r.Role)) return Bad("Unknown role.");
    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { error = "Someone with that email already has a BuildFree account." });

    // Inviting the same email again replaces the old invite and its link.
    db.Invites.RemoveRange(await db.Invites.Where(i => i.AccountId == accountId && i.Email == email).ToListAsync());

    var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    var invite = new Invite
    {
        AccountId = accountId,
        Email = email!,
        Name = name,
        Role = (UserRole)r.Role,
        CodeHash = InviteHash(code),
        InvitedBy = Me(ctx).Name
    };
    db.Invites.Add(invite);
    await db.SaveChangesAsync();
    return Results.Created($"/api/team/invites/{invite.Id}", new { invite, code });
}).AddEndpointFilter(OwnerOnly);

team.MapDelete("/invites/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId);
    if (invite is null) return Results.NotFound();
    db.Invites.Remove(invite);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).AddEndpointFilter(OwnerOnly);

team.MapPut("/{id:guid}/role", async (Guid id, RoleRequest r, ClaimsPrincipal me, AppDbContext db) =>
{
    var accountId = AccountId(me);
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.AccountId == accountId);
    if (user is null) return Results.NotFound();
    if (!Enum.IsDefined(typeof(UserRole), r.Role)) return Bad("Unknown role.");
    var role = (UserRole)r.Role;
    if (user.Role == UserRole.Owner && role != UserRole.Owner &&
        await db.Users.CountAsync(u => u.AccountId == accountId && u.Role == UserRole.Owner) == 1)
        return Bad("The company needs at least one owner.");
    user.Role = role;
    await db.SaveChangesAsync();
    return Results.Ok(MemberView(user));
}).AddEndpointFilter(OwnerOnly);

// Makes a link the person can use to set a new password, for when they forget
// it. BuildFree doesn't send email yet, so the owner texts or emails the link.
// It works once and expires after 24 hours.
team.MapPost("/{id:guid}/reset-link", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
{
    var accountId = AccountId(me);
    if (id == Me(ctx).Id) return Bad("To change your own password, use Change password on the Team page.");
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.AccountId == accountId);
    if (user is null) return Results.NotFound();
    var expiresAt = DateTime.UtcNow.Add(ResetCodes.Lifetime);
    return Results.Ok(new { code = ResetCodes.Make(keyBytes, user, expiresAt), user.Name, user.Email, expiresAt });
}).AddEndpointFilter(OwnerOnly);

// Removing someone locks them out right away. Their daily logs and photos stay.
team.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, HttpContext ctx, AppDbContext db) =>
{
    var accountId = AccountId(me);
    if (id == Me(ctx).Id) return Bad("You can't remove yourself.");
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.AccountId == accountId);
    if (user is null) return Results.NotFound();
    db.Users.Remove(user);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).AddEndpointFilter(OwnerOnly);

// What the join screen shows before the person sets a password. No login needed.
app.MapGet("/api/invites/{code}", async (string code, AppDbContext db) =>
{
    var hash = InviteHash(code);
    var invite = await db.Invites.FirstOrDefaultAsync(i => i.CodeHash == hash && i.ExpiresAt > DateTime.UtcNow);
    if (invite is null) return Results.NotFound(new { error = "This invite link is invalid or has expired. Ask for a new one." });
    var company = await db.Accounts.Where(a => a.Id == invite.AccountId).Select(a => a.Name).FirstOrDefaultAsync();
    return Results.Ok(new { company, invite.Name, invite.Email, invite.Role });
}).RequireRateLimiting("auth");

// Accepting an invite creates the person's login in the inviting company.
app.MapPost("/api/auth/accept-invite", async (AcceptInviteRequest r, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(r.Code)) return Bad("The invite code is missing.");
    var hash = InviteHash(r.Code.Trim());
    var invite = await db.Invites.FirstOrDefaultAsync(i => i.CodeHash == hash && i.ExpiresAt > DateTime.UtcNow);
    if (invite is null) return Results.NotFound(new { error = "This invite link is invalid or has expired. Ask for a new one." });
    var name = Clean(r.Name) ?? invite.Name;
    if (TooLong(name, 200)) return Bad("Name is too long.");
    if (Passwords.Problem(r.Password) is string problem) return Bad(problem);
    if (await db.Users.AnyAsync(u => u.Email == invite.Email))
        return Results.Conflict(new { error = "That email is already registered." });

    var user = new User
    {
        AccountId = invite.AccountId,
        Email = invite.Email,
        Name = name,
        Role = invite.Role,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password)
    };
    db.Users.Add(user);
    db.Invites.Remove(invite);
    await db.SaveChangesAsync();
    return Results.Ok(new { token = MakeToken(user) });
}).RequireRateLimiting("auth");

// ---------- Passwords ----------

// Changing your password signs out every other device. The response has a new
// token for this one.
app.MapPut("/api/me/password", async (PasswordChangeRequest r, HttpContext ctx, AppDbContext db) =>
{
    var myId = Me(ctx).Id;
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == myId);
    if (user is null) return Results.Unauthorized();
    if (string.IsNullOrEmpty(r.CurrentPassword) || !BCrypt.Net.BCrypt.Verify(r.CurrentPassword, user.PasswordHash))
        return Bad("Your current password isn't right.");
    if (Passwords.Problem(r.NewPassword) is string problem) return Bad(problem);
    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.NewPassword);
    await db.SaveChangesAsync();
    LoginGuard.Clear(user.Email);
    return Results.Ok(new { token = MakeToken(user) });
}).RequireAuthorization().RequireRateLimiting("auth");

// What the reset screen shows before the person picks a new password. No login needed.
app.MapGet("/api/password-resets/{code}", async (string code, AppDbContext db) =>
{
    var user = await ResetUser(db, code);
    if (user is null) return Results.NotFound(new { error = ResetLinkBroken });
    var company = await db.Accounts.Where(a => a.Id == user.AccountId).Select(a => a.Name).FirstOrDefaultAsync();
    return Results.Ok(new { company, user.Name, user.Email });
}).RequireRateLimiting("auth");

// Sets a new password from a reset link and logs the person in. Every device
// that was logged in as them is signed out, and the link stops working.
app.MapPost("/api/auth/reset-password", async (PasswordResetRequest r, AppDbContext db) =>
{
    var user = await ResetUser(db, r.Code);
    if (user is null) return Results.NotFound(new { error = ResetLinkBroken });
    if (Passwords.Problem(r.Password) is string problem) return Bad(problem);
    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password);
    await db.SaveChangesAsync();
    LoginGuard.Clear(user.Email);
    return Results.Ok(new { token = MakeToken(user) });
}).RequireRateLimiting("auth");

// ---------- Estimates, change orders and invoices (see Estimates.cs, Invoices.cs) ----------

app.MapEstimates();
app.MapInvoices();

app.Run();
