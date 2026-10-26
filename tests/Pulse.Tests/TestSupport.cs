using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Application.Alerts;
using Pulse.Domain;
using Pulse.Infrastructure;

namespace Pulse.Tests;

/// <summary>Resolves host names from a fixed table, so tests never touch the real DNS.</summary>
internal sealed class FakeResolver(Dictionary<string, IPAddress[]>? table = null) : IHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _table = table ?? [];

    public void Add(string host, params IPAddress[] addresses) => _table[host] = addresses;

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IPAddress>>(_table.TryGetValue(host, out var found) ? found : []);
}

/// <summary>A probe whose answer the test chooses.</summary>
internal sealed class FakeProbe(TimeProvider clock) : IServiceProbe
{
    public bool Succeeds { get; set; } = true;

    public int Calls { get; private set; }

    public Task<CheckResult> ProbeAsync(MonitoredService service, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Succeeds
            ? CheckResult.Succeeded(service.Id, clock.GetUtcNow(), 200, 42)
            : CheckResult.Failed(service.Id, clock.GetUtcNow(), 42, "Expected status 200, got 500.", 500));
    }
}

/// <summary>A mail server that keeps what it is asked to send.</summary>
internal sealed class FakeMailSender : IMailSender
{
    private readonly List<OutgoingMail> _sent = [];
    private readonly object _lock = new();

    public bool IsAvailable { get; set; } = true;

    /// <summary>Make every send fail, as an unreachable server would.</summary>
    public bool Failing { get; set; }

    public IReadOnlyList<OutgoingMail> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        if (Failing)
        {
            throw new MailDeliveryException("The mail server could not be reached.");
        }

        lock (_lock)
        {
            _sent.Add(mail);
        }

        return Task.CompletedTask;
    }

    /// <summary>The token in the last link sent to this address (the part after "token=").</summary>
    public string? LastTokenFor(string to) =>
        Sent.LastOrDefault(m => m.To == to) is { } mail
            ? mail.Body.Split("token=")[1].Split(['\n', '\r', ' '])[0]
            : null;
}

internal static class TestKeys
{
    /// <summary>32 bytes in base64: the encryption key used by tests.</summary>
    public static readonly string Encryption = Convert.ToBase64String("test-encryption-key-0123456789ab"u8.ToArray());
}

/// <summary>
/// The real application and storage on a private in-memory SQLite database, with a fake clock, probe and
/// mail server.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;
    private int _counter;

    private TestApp(SqliteConnection keepAlive, ServiceProvider provider, FakeTimeProvider clock, FakeProbe probe, FakeMailSender mail, FakeResolver resolver)
    {
        _keepAlive = keepAlive;
        _provider = provider;
        Clock = clock;
        Probe = probe;
        Mail = mail;
        Resolver = resolver;
    }

    public FakeTimeProvider Clock { get; }

    public FakeProbe Probe { get; }

    public FakeMailSender Mail { get; }

    /// <summary>The host names the app can resolve. "shop.test" is public and always known.</summary>
    public FakeResolver Resolver { get; }

    public IServiceProvider Services => _provider;

    public static async Task<TestApp> CreateAsync(Action<PulseOptions>? configure = null, Action<AccountOptions>? configureAccounts = null)
    {
        // A shared-cache in-memory database lives as long as one connection stays open.
        var connectionString = $"Data Source=pulse-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var probe = new FakeProbe(clock);
        var mail = new FakeMailSender();
        var resolver = new FakeResolver();
        resolver.Add("shop.test", IPAddress.Parse("8.8.8.8"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<PulseOptions>(o =>
        {
            o.SchedulerEnabled = false;
            configure?.Invoke(o);
        });
        services.Configure<AccountOptions>(o =>
        {
            o.EncryptionKey = TestKeys.Encryption;
            configureAccounts?.Invoke(o);
        });
        services.Configure<AlertOptions>(o => o.RetryDelaySeconds = 0);
        services.AddPulseApplication();
        services.AddPulseInfrastructure(connectionString);
        services.AddSingleton<IServiceProbe>(probe);
        services.AddSingleton<IHostResolver>(resolver);
        services.AddSingleton<IMailSender>(mail);
        var provider = services.BuildServiceProvider();

        await provider.MigratePulseDatabaseAsync();
        return new TestApp(keepAlive, provider, clock, probe, mail, resolver);
    }

    public AsyncServiceScope NewScope() => _provider.CreateAsyncScope();

    /// <summary>Adds an organization (and nothing else) and returns it.</summary>
    public async Task<Organization> AddOrganizationAsync(string? slug = null, Plan plan = Plan.Pro)
    {
        await using var scope = NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var organization = Organization.Create("Org " + slug, slug ?? $"org-{Interlocked.Increment(ref _counter)}", Clock.GetUtcNow());
        organization.ChangePlan(plan);
        await accounts.AddOrganizationAsync(organization, CancellationToken.None);
        await accounts.SaveChangesAsync(CancellationToken.None);
        return organization;
    }

    /// <summary>Adds a service to an organization.</summary>
    public async Task<MonitoredService> AddServiceAsync(
        Guid organizationId,
        string name = "Site",
        int intervalSeconds = 30,
        bool isPublic = true)
    {
        await using var scope = NewScope();
        var store = scope.ServiceProvider.GetRequiredService<IPulseStore>();
        var service = MonitoredService.Create(organizationId, name, "https://example.com/", intervalSeconds, 10, 200, isPublic, Clock.GetUtcNow());
        await store.AddServiceAsync(service, CancellationToken.None);
        await store.SaveChangesAsync(CancellationToken.None);
        return service;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}

internal static class OptionsFactory
{
    public static IOptions<PulseOptions> Create(Action<PulseOptions>? configure = null)
    {
        var options = new PulseOptions();
        configure?.Invoke(options);
        return Options.Create(options);
    }

    public static IOptions<T> Wrap<T>(T value)
        where T : class => Options.Create(value);

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;
}
