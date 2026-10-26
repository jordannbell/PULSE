using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulse.Application;
using Pulse.Contracts;

namespace Pulse.Tests;

/// <summary>The whole application started in memory, driven through HTTP, with a fake mail server and resolver.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "correct horse battery";

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString = $"Data Source=api-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly Dictionary<string, string> _settings;
    private int _counter;

    public ApiFactory()
        : this([])
    {
    }

    protected ApiFactory(Dictionary<string, string> settings)
    {
        _settings = settings;
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    internal FakeMailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var defaults = new Dictionary<string, string>
        {
            ["ConnectionStrings:Pulse"] = _connectionString,
            ["Pulse:SchedulerEnabled"] = "false",
            ["Pulse:RateLimitPerMinute"] = "100000",
            ["Pulse:AuthRateLimitPerMinute"] = "100000",
            ["Pulse:Accounts:EncryptionKey"] = TestKeys.Encryption,
            ["Pulse:Accounts:WebBaseUrl"] = "http://localhost:5090",
        };
        foreach (var (key, value) in defaults.Concat(_settings))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            var resolver = new FakeResolver();
            resolver.Add("shop.test", IPAddress.Parse("8.8.8.8"));
            resolver.Add("hooks.test", IPAddress.Parse("8.8.4.4"));
            resolver.Add("intranet.test", IPAddress.Parse("10.0.0.5"));
            services.RemoveAll<IHostResolver>();
            services.AddSingleton<IHostResolver>(resolver);
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Mail);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }

    /// <summary>A client carrying a session token.</summary>
    public HttpClient ClientFor(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static string NewEmail() => $"user{Guid.NewGuid():N}@example.com";

    /// <summary>Signs a new customer up through the API. The address and the page name are unique unless given.</summary>
    public async Task<Customer> SignUpAsync(string? email = null, string? slug = null, string? name = null)
    {
        var number = Interlocked.Increment(ref _counter);
        email ??= NewEmail();
        slug ??= $"cust-{Guid.NewGuid():N}"[..20];
        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/signup", new SignUpRequest(email, Password, name ?? $"Customer {number}", slug));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return new Customer(email, slug, auth.Token, ClientFor(auth.Token), auth.Account);
    }

    public sealed record Customer(string Email, string Slug, string Token, HttpClient Client, AccountDto Account);
}

/// <summary>The same application with a very low limit on sign-in attempts.</summary>
public sealed class StrictAuthApiFactory : ApiFactory
{
    public StrictAuthApiFactory()
        : base(new Dictionary<string, string> { ["Pulse:AuthRateLimitPerMinute"] = "5" })
    {
    }
}

/// <summary>The same application with demonstration data, as in development.</summary>
public sealed class DemoApiFactory : ApiFactory
{
    public DemoApiFactory()
        : base(new Dictionary<string, string> { ["Pulse:SeedDemoData"] = "true" })
    {
    }
}

/// <summary>The same application where addresses are confirmed automatically and plans cannot be changed.</summary>
public sealed class LockedPlansApiFactory : ApiFactory
{
    public LockedPlansApiFactory()
        : base(new Dictionary<string, string>
        {
            ["Pulse:Accounts:AutoVerifyEmail"] = "true",
            ["Pulse:Accounts:AllowSelfServicePlanChange"] = "false",
        })
    {
    }
}
