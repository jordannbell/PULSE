using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Pulse.Api;
using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Application.Alerts;
using Pulse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<PulseOptions>().BindConfiguration(PulseOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AlertOptions>().BindConfiguration(AlertOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AccountOptions>().BindConfiguration(AccountOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<MailOptions>().BindConfiguration(MailOptions.SectionName).ValidateOnStart();

builder.Services.AddPulseApplication();
builder.Services.AddPulseInfrastructure(builder.Configuration.GetConnectionString("Pulse") ?? "Data Source=pulse.db");

builder.Services
    .AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<PulseOptions>>((limiter, pulse) =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            Client(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = pulse.Value.RateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Sign-up, login and password requests: few per minute and per address, so guessing is slow.
    limiter.AddPolicy(PulseEndpoints.AuthRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            "auth:" + Client(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = pulse.Value.AuthRateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Only behind a proxy you control (Pulse:TrustForwardedHeaders): the proxy's address is not a client's.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

try
{
    // Read the settings first: a bad configuration must stop the app before it touches the database,
    // with a readable message and a clean exit code instead of a crash report.
    var pulse = app.Services.GetRequiredService<IOptions<PulseOptions>>().Value;
    _ = app.Services.GetRequiredService<IOptions<AlertOptions>>().Value;
    _ = app.Services.GetRequiredService<IOptions<AccountOptions>>().Value;
    _ = app.Services.GetRequiredService<IOptions<MailOptions>>().Value;

    if (pulse.TrustForwardedHeaders)
    {
        app.UseForwardedHeaders();
    }
}
catch (OptionsValidationException ex)
{
    await Console.Error.WriteLineAsync($"Invalid configuration: {string.Join(" ", ex.Failures)}");
    return 1;
}

await app.Services.MigratePulseDatabaseAsync();
await app.Services.SeedDemoDataAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapPulseEndpoints();

await app.RunAsync();
return 0;

static string Client(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

// Lets the integration tests start the app.
public partial class Program;
