using System.Text.Json.Serialization;
using Anthropic;
using MapleLedger.ApiService.Data;
using MapleLedger.ApiService.Endpoints;
using MapleLedger.ApiService.Receipts;
using MapleLedger.ApiService.Users;
using MapleLedger.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// SQL Server for the hosted site; a SQLite file for the desktop app (Database:Provider=Sqlite),
// which uses the browser's SQLite migrations from MapleLedger.Data. SQL Server migrations live in
// this assembly; the context (shared with the browser) does not.
var useSqlite = string.Equals(builder.Configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);
if (useSqlite)
{
    builder.Services.AddDbContext<LedgerDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("mapleledger")));
}
else
{
    builder.Services.AddDbContext<LedgerDbContext>(options => options.UseSqlServer(
        builder.Configuration.GetConnectionString("mapleledger"),
        sql => sql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name)));
    builder.EnrichSqlServerDbContext<LedgerDbContext>();
}

// Only the web host can call the API: it forwards signed-in users' requests with a short-lived
// token signed with a key the two share. Every endpoint requires one unless it opts out.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = InternalApiToken.ValidationParameters(InternalApiToken.SigningKey(builder.Configuration));
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Each request's ledger is scoped to the signed-in user.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
    CurrentUser.From(sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User ?? new())
    ?? throw new UnauthorizedAccessException("No signed-in user on this request."));
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped(sp =>
{
    var db = sp.GetRequiredService<LedgerDbContext>();
    db.OwnerId = sp.GetRequiredService<CurrentUser>().Id;
    return new LedgerService(db);
});

// Optional: reading receipts with Claude for premium users who ask for it. Without a key the
// feature is simply unavailable and receipts are read in the browser only.
if (builder.Configuration["Anthropic:ApiKey"] is { Length: > 0 } anthropicKey)
{
    builder.Services.AddSingleton(new AnthropicClient { ApiKey = anthropicKey });
    builder.Services.AddSingleton<ClaudeReceiptReader>();
}

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // FDX omits absent fields rather than sending nulls.
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// Self-hosted, single database: bring the schema up to date on startup. Set
// Database__MigrateOnStartup=false to manage migrations by hand instead.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    if (!useSqlite)
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        // The desktop app: a new version updates the ledger it finds, with a copy taken first and
        // put back if updating fails. The exit code tells the launcher what happened.
        try
        {
            var upgrade = await SqliteLedgerUpgrade.RunAsync(db);
            if (upgrade.Applied.Count > 0)
            {
                app.Logger.LogInformation(
                    upgrade.BackupPath is null ? "Created the ledger ({Migrations})." : "Updated the ledger ({Migrations}); the copy from before is {Backup}.",
                    string.Join(", ", upgrade.Applied), upgrade.BackupPath);
            }
        }
        catch (LedgerUpgradeException ex)
        {
            // Straight to stderr as well: the logger writes asynchronously and the process ends here.
            await Console.Error.WriteLineAsync(ex.Message);
            app.Logger.LogCritical(ex, "{Message}", ex.Message);
            await app.DisposeAsync();
            Environment.ExitCode = ex.ExitCode;
            return;
        }
    }
}

if (useSqlite)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

    // Several devices can use a shared desktop install at once: readers don't block the writer.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

    // A desktop install that used SQL Server moves its ledger here on the first start.
    if (app.Configuration["Database:ImportFrom"] is { Length: > 0 } importFrom)
    {
        var import = await SqlServerImport.RunAsync(db, importFrom);
        app.Logger.LogInformation(
            import.Copied ? "Copied the ledger from SQL Server: {Rows}." : "This database already has a ledger ({Rows}); nothing was copied from SQL Server.",
            string.Join(", ", import.Rows.Select(r => $"{r.Value} {r.Key}")));
    }
}

app.MapGet("/", () => "LoonieBooks API is running.").AllowAnonymous();
app.MapProfileEndpoints();
app.MapFdxEndpoints().AddEndpointFilter<PremiumLedgerFilter>();
app.MapLedgerEndpoints().AddEndpointFilter<PremiumLedgerFilter>();

app.MapDefaultEndpoints();

app.Run();

// Lets the integration tests reference the API's assembly.
public partial class Program;
