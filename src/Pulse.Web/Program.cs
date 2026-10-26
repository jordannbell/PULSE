using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Pulse.Web;
using Pulse.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration["Api:BaseUrl"];
if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiBase))
{
    throw new InvalidOperationException("Set Api:BaseUrl to the address of the Pulse API.");
}

// The browser keeps one encrypted cookie. The API token lives inside it and never reaches the page.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "pulse.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Web:SecureCookies")
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = false; // the cookie ends when the API session ends
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHttpClient<PulseApiClient>(client =>
{
    client.BaseAddress = apiBase;
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<ApiSession>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// Only behind a reverse proxy you control (Web:TrustForwardedHeaders): it tells the real scheme and address.
if (app.Configuration.GetValue<bool>("Web:TrustForwardedHeaders"))
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseSecurityHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// Signing out: the API session is closed too, so the token is dead even if someone kept a copy.
// A POST, and the cookie is SameSite=Lax: another site cannot make a visitor's browser sign them out.
app.MapPost("/logout", async (HttpContext http, PulseApiClient api) =>
{
    if (http.User.FindFirst(ApiSession.TokenClaim)?.Value is { } token)
    {
        await api.LogoutAsync(token, http.RequestAborted);
    }

    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
}).DisableAntiforgery();

// The API said the session is over: forget the cookie and ask for a new sign-in.
app.MapGet("/session-expired", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login?expired=1");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
