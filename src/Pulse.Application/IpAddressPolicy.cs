using System.Net;
using System.Net.Sockets;

namespace Pulse.Application;

/// <summary>
/// Decides whether an address belongs on the public internet. A monitor may only call such addresses:
/// this is the defence against server-side request forgery (SSRF) toward the host's own network.
/// </summary>
public static class IpAddressPolicy
{
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address.GetAddressBytes()),
            _ => false,
        };
    }

    private static bool IsPublicV4(ReadOnlySpan<byte> ip)
    {
        // Ranges that are not routable on the public internet (RFC 1122, 1918, 6598, 5737, 2544, 3171, 1112).
        return !(InRange(ip, 0, 0, 0, 0, 8) // "this" network
            || InRange(ip, 10, 0, 0, 0, 8) // private
            || InRange(ip, 100, 64, 0, 0, 10) // carrier-grade NAT
            || InRange(ip, 127, 0, 0, 0, 8) // loopback
            || InRange(ip, 169, 254, 0, 0, 16) // link-local, includes cloud metadata 169.254.169.254
            || InRange(ip, 172, 16, 0, 0, 12) // private
            || InRange(ip, 192, 0, 0, 0, 24) // IETF protocol assignments
            || InRange(ip, 192, 0, 2, 0, 24) // documentation
            || InRange(ip, 192, 168, 0, 0, 16) // private
            || InRange(ip, 198, 18, 0, 0, 15) // benchmarking
            || InRange(ip, 198, 51, 100, 0, 24) // documentation
            || InRange(ip, 203, 0, 113, 0, 24) // documentation
            || InRange(ip, 224, 0, 0, 0, 4) // multicast
            || InRange(ip, 240, 0, 0, 0, 4)); // reserved and broadcast
    }

    private static bool IsPublicV6(ReadOnlySpan<byte> ip)
    {
        // Only global unicast (2000::/3) is allowed; this excludes ::, ::1, fc00::/7, fe80::/10, ff00::/8.
        if ((ip[0] & 0xE0) != 0x20)
        {
            return false;
        }

        // 2001:db8::/32 documentation.
        if (ip[0] == 0x20 && ip[1] == 0x01 && ip[2] == 0x0D && ip[3] == 0xB8)
        {
            return false;
        }

        // 2001::/32 Teredo tunnels.
        if (ip[0] == 0x20 && ip[1] == 0x01 && ip[2] == 0x00 && ip[3] == 0x00)
        {
            return false;
        }

        // 2002::/16 6to4 carries an IPv4 address in bytes 2..5: judge that address instead.
        if (ip[0] == 0x20 && ip[1] == 0x02)
        {
            return IsPublicV4(ip.Slice(2, 4));
        }

        return true;
    }

    private static bool InRange(ReadOnlySpan<byte> ip, byte a, byte b, byte c, byte d, int prefixBits)
    {
        Span<byte> network = [a, b, c, d];
        var fullBytes = prefixBits / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (ip[i] != network[i])
            {
                return false;
            }
        }

        var remainingBits = prefixBits % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (ip[fullBytes] & mask) == (network[fullBytes] & mask);
    }
}
