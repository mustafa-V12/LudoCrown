using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using LudoCrown.Server.Endpoints;
using LudoCrown.Server.Hubs;
using LudoCrown.Server.Matches;
using LudoCrown.Server.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- configuration ----------
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
builder.Services.Configure<AuthOptions>(config.GetSection("Auth"));
builder.Services.Configure<MatchOptions>(config.GetSection("Match"));

if (string.IsNullOrWhiteSpace(config["Jwt:SigningKey"]))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Jwt:SigningKey must be set (environment variable Jwt__SigningKey). See docs/BACKEND.md.");
    // Development convenience: a random key per run. Tokens stop working when the server restarts.
    var devKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    builder.Services.PostConfigure<JwtOptions>(o => o.SigningKey = devKey);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDiceRoller, CryptoDiceRoller>();
builder.Services.AddHttpClient("idp", c => c.Timeout = TimeSpan.FromSeconds(10));

// ---------- persistence ----------
var connectionString = config.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
{
#if POSTGRES
    LudoCrown.Server.Postgres.PostgresServiceCollectionExtensions.AddLudoPostgres(builder.Services, connectionString, new LudoCrown.Server.Postgres.PostgresOptions
    {
        MigrateOnStartup = config.GetValue("Database:MigrateOnStartup", builder.Environment.IsDevelopment()),
        MatchRetentionDays = config.GetValue("Database:MatchRetentionDays", 730),
    });
#else
    throw new InvalidOperationException("This build was compiled without PostgreSQL support (UsePostgres=false).");
#endif
}
else
{
    if (!builder.Environment.IsDevelopment() && !config.GetValue<bool>("AllowInMemoryStore"))
        throw new InvalidOperationException("ConnectionStrings:Postgres is required outside Development. The in-memory store loses all data on restart.");
    builder.Services.AddSingleton<InMemoryStore>();
    builder.Services.AddSingleton<IAccountStore>(sp => sp.GetRequiredService<InMemoryStore>());
    builder.Services.AddSingleton<IProfileStore>(sp => sp.GetRequiredService<InMemoryStore>());
    builder.Services.AddSingleton<IMatchStore>(sp => sp.GetRequiredService<InMemoryStore>());
    builder.Services.AddSingleton<ISocialStore>(sp => sp.GetRequiredService<InMemoryStore>());
}

// ---------- auth ----------
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IJwksSource, HttpJwksSource>();
builder.Services.AddSingleton<IdTokenValidator>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<AppleSignInService>();
builder.Services.AddSingleton<IOAuthProvider, GoogleOAuthProvider>();
builder.Services.AddSingleton<IOAuthProvider, FacebookOAuthProvider>();
builder.Services.AddSingleton<OAuthService>();
builder.Services.AddSingleton<EmailCodeService>();
if (string.Equals(config["Auth:Email:Sender"], "Smtp", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();

builder.Services.AddAuthentication(LudoAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, LudoAuthenticationHandler>(LudoAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.IsAuthenticated == true ? ctx.User.UserId().ToString() : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

// ---------- realtime & game services ----------
builder.Services.AddSingleton<HubRateLimitFilter>();
builder.Services.AddSignalR(o =>
{
    o.AddFilter<HubRateLimitFilter>();
    o.MaximumReceiveMessageSize = 8 * 1024;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
}).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<IMatchNotifier, HubMatchNotifier>();
builder.Services.AddSingleton<MatchService>();
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddSingleton<PrivateRoomService>();
builder.Services.AddHostedService<GameLoopService>();
builder.Services.AddHealthChecks();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // trust the hosting platform's proxy; restrict in docs/BACKEND.md if self-hosting
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "Ludo Crown server", status = "ok" }));
app.MapHealthChecks("/health");
app.MapGet("/stats/live", (MatchService m, MatchmakingService mm) => Results.Ok(new { liveMatches = m.LiveMatchCount, queued = mm.QueuedCount }));
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapHub<MatchHub>(HubNames.Path);

var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
app.Logger.LogInformation("Auth providers — guest: {Guest}, apple: {Apple}, google: {Google}, facebook: {Facebook}, email: {Email} ({Sender})",
    auth.GuestEnabled, auth.Apple.Enabled, auth.Google.Enabled, auth.Facebook.Enabled, auth.Email.Enabled, auth.Email.Sender);

app.Run();

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
