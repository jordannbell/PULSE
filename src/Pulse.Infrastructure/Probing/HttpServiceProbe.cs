using System.Diagnostics;
using Microsoft.Extensions.Options;
using Pulse.Application;
using Pulse.Domain;

namespace Pulse.Infrastructure.Probing;

/// <summary>Calls the monitored address with an HTTP GET, through the guarded client.</summary>
internal sealed class HttpServiceProbe : IServiceProbe, IDisposable
{
    internal const string BlockedMessage = GuardedHttp.BlockedMessage;

    private const string UserAgent = "PulseBot/0.1 (+uptime monitoring)";

    private readonly HttpClient _client;
    private readonly TimeProvider _clock;

    public HttpServiceProbe(IHostResolver resolver, TimeProvider clock, IOptions<PulseOptions> options)
    {
        _clock = clock;
        _client = new GuardedHttp(resolver, options.Value.AllowPrivateTargets).CreateClient();
    }

    public async Task<CheckResult> ProbeAsync(MonitoredService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var startedAt = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(service.TimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, service.Url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            // Only the headers matter; the body is never read.
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            var latency = ElapsedMs(startedAt);

            return status == service.ExpectedStatusCode
                ? CheckResult.Succeeded(service.Id, _clock.GetUtcNow(), status, latency)
                : CheckResult.Failed(service.Id, _clock.GetUtcNow(), latency, $"Expected status {service.ExpectedStatusCode}, got {status}.", status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckResult.Failed(service.Id, _clock.GetUtcNow(), ElapsedMs(startedAt), $"No answer within {service.TimeoutSeconds} s.");
        }
        catch (HttpRequestException ex)
        {
            // Only a short, fixed description is stored: the raw message can echo internal details.
            return CheckResult.Failed(service.Id, _clock.GetUtcNow(), ElapsedMs(startedAt), GuardedHttp.Describe(ex));
        }
    }

    public void Dispose() => _client.Dispose();

    private static int ElapsedMs(long startedAt) => (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
