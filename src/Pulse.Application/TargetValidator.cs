using System.Net;
using Microsoft.Extensions.Options;

namespace Pulse.Application;

/// <summary>
/// Accepts only addresses a monitor may call. Checked when a monitor is created; the probe checks
/// again at connection time, because DNS answers can change in between.
/// </summary>
public sealed class TargetValidator(IHostResolver resolver, IOptions<PulseOptions> options)
{
    private static readonly int[] AllowedPorts = [80, 443, 8080, 8443];

    /// <summary>Returns a message for the user when the address is refused, or null when it is accepted.</summary>
    public async Task<string?> ValidateAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Enter a full http:// or https:// address.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "The address must not contain a user name or password.";
        }

        var allowPrivate = options.Value.AllowPrivateTargets;
        if (!allowPrivate && !AllowedPorts.Contains(uri.Port))
        {
            return "Only ports 80, 443, 8080 and 8443 are allowed.";
        }

        var addresses = await ResolveAsync(uri, cancellationToken);
        if (addresses.Count == 0)
        {
            return "The host name cannot be resolved.";
        }

        if (!allowPrivate && !addresses.All(IpAddressPolicy.IsPubliclyRoutable))
        {
            return "The address must point to a public server.";
        }

        return null;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        // Uri already rewrites forms such as http://2130706433 to 127.0.0.1, so literals are judged as parsed.
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            return [literal];
        }

        try
        {
            return await resolver.ResolveAsync(uri.IdnHost, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return [];
        }
    }
}
