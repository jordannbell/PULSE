using System.Net;
using System.Net.Sockets;
using Pulse.Application;

namespace Pulse.Infrastructure.Probing;

/// <summary>
/// Builds HTTP clients that cannot be turned against the host's own network. The host name is resolved
/// here, every answer is checked with <see cref="IpAddressPolicy"/>, and the socket connects to the
/// checked address. A name that answers with a public address first and a private one later
/// (DNS rebinding) is therefore refused. Redirects are never followed.
/// </summary>
internal sealed class GuardedHttp(IHostResolver resolver, bool allowPrivateTargets)
{
    internal const string BlockedMessage = "The address does not point to a public server.";

    internal const string UnresolvedMessage = "The host name cannot be resolved.";

    /// <summary>A client whose lifetime and timeout the caller controls.</summary>
    public HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a redirect is a result to report, never a hop to follow blindly
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A short description of a failed request, safe to store or show: never the raw exception text.</summary>
    public static string Describe(HttpRequestException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex.InnerException switch
        {
            // The handler wraps what the connect callback throws; our own refusals carry a safe message.
            TargetRefusedException refused => refused.Message,
            SocketException => "The server could not be reached.",
            System.Security.Authentication.AuthenticationException => "The TLS handshake failed.",
            _ => ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => UnresolvedMessage,
                HttpRequestError.SecureConnectionError => "The TLS handshake failed.",
                HttpRequestError.ConnectionError => "The server could not be reached.",
                _ => "The request failed.",
            },
        };
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Count == 0)
        {
            throw new TargetRefusedException(UnresolvedMessage);
        }

        if (!allowPrivateTargets && !addresses.All(IpAddressPolicy.IsPubliclyRoutable))
        {
            throw new TargetRefusedException(BlockedMessage);
        }

        // A host often has both an IPv6 and an IPv4 address, and the server running Pulse may reach only one
        // of them. Try each checked address in turn so a broken IPv6 route does not report a healthy site as down.
        SocketException? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastFailure!;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            return await resolver.ResolveAsync(host, cancellationToken);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    /// <summary>Raised by the connect guard; its message is written for the user and safe to store.</summary>
    private sealed class TargetRefusedException(string message) : Exception(message);
}
