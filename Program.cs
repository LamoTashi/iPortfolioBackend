using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNetEnv;
using iPortfolioBackend.Models;
using iPortfolioBackend.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Load environment variables early
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var jwtKey = builder.Configuration["JWT_SECRET"];
if (string.IsNullOrEmpty(jwtKey) || jwtKey.Length < 32)
{
    throw new Exception("JWT_SECRET is missing or too short. Must be at least 32 characters.");
}
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

// HTTPS certificate for Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(7025, listenOptions =>
    {
        var certPath = Path.Combine(Directory.GetCurrentDirectory(), "cert", "localhost+2.pem");
        var keyPath = Path.Combine(Directory.GetCurrentDirectory(), "cert", "localhost+2-key.pem");
        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
        listenOptions.UseHttps(cert);
    });
});

// Register DbContext with SQLite
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=portfolio.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

// JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "iPortfolio API", Version = "v1" });

    var jwtScheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Description = "Enter 'Bearer {token}'",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Id = JwtBearerDefaults.AuthenticationScheme,
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme
        }
    };

    c.AddSecurityDefinition(jwtScheme.Reference.Id, jwtScheme);
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        { jwtScheme, Array.Empty<string>() }
    });
});

// Allow frontend CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            origin.StartsWith("http://localhost") || origin.StartsWith("https://localhost"))
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

// LOGIN ENDPOINT
app.MapPost("/login", (LoginRequest login, IConfiguration config) =>
{
    var adminUsername = config["ADMIN_USERNAME"];
    var adminPasswordHash = config["ADMIN_PASSWORD_HASH"];

    if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPasswordHash))
        return Results.Problem("Admin credentials not configured.");

    bool isUserValid = login.Username == adminUsername &&
                       BCrypt.Net.BCrypt.Verify(login.Password, adminPasswordHash);

    if (!isUserValid)
        return Results.Unauthorized();

    var claims = new[] { new Claim(ClaimTypes.Name, login.Username) };
    var token = new JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256)
    );

    var tokenStr = new JwtSecurityTokenHandler().WriteToken(token);
    return Results.Ok(new { token = tokenStr });
});

// DASHBOARD PING
app.MapGet("/dashboard", [Microsoft.AspNetCore.Authorization.Authorize] () =>
    Results.Ok(new { message = "Welcome to your portfolio dashboard!" })
);

// Grouped Project CRUD endpoints with Authorization
var projectsGroup = app.MapGroup("/projects").RequireAuthorization();

projectsGroup.MapGet("/", async (AppDbContext db) =>
    await db.Projects.ToListAsync());

projectsGroup.MapGet("/{id:int}", async (int id, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    return project is null ? Results.NotFound() : Results.Ok(project);
});

projectsGroup.MapPost("/", async (Projects project, AppDbContext db) =>
{
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Created($"/projects/{project.Id}", project);
});

projectsGroup.MapPut("/{id:int}", async (int id, Projects updated, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    project.Title = updated.Title;
    project.Description = updated.Description;
    project.ImageUrl = updated.ImageUrl;
    project.GitHubUrl = updated.GitHubUrl;

    await db.SaveChangesAsync();
    return Results.Ok(project);
});

projectsGroup.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Contact POST API
app.MapPost("/contact", async (ContactMessage contact, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(contact.Name) || string.IsNullOrWhiteSpace(contact.Message))
    {
        return Results.BadRequest("Name and message are required.");
    }

    db.ContactMessages.Add(contact);
    await db.SaveChangesAsync();
    return Results.Ok(new { success = true, message = "Message received." });
});

// View all messages as an admin
app.MapGet("/contact", [Microsoft.AspNetCore.Authorization.Authorize] async (AppDbContext db) =>
{
    return await db.ContactMessages.ToListAsync();
});

// DELETE a contact message by ID
app.MapDelete("/contact/{id:int}", [Microsoft.AspNetCore.Authorization.Authorize] async (int id, AppDbContext db) =>
{
    var message = await db.ContactMessages.FindAsync(id);
    if (message == null)
    {
        return Results.NotFound();
    }

    db.ContactMessages.Remove(message);
    await db.SaveChangesAsync();

    return Results.NoContent();
});
app.Run();