using System.Net;
using Pulse.Application;

namespace Pulse.Tests;

public class IpAddressPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string address)
    {
        Assert.True(IpAddressPolicy.IsPubliclyRoutable(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("2002:7f00:1::1")]
    public void Private_reserved_and_special_addresses_are_refused(string address)
    {
        Assert.False(IpAddressPolicy.IsPubliclyRoutable(IPAddress.Parse(address)));
    }

    [Fact]
    public void A_6to4_address_that_wraps_a_public_ipv4_address_is_allowed()
    {
        // 2002:0808:0808::/48 wraps 8.8.8.8.
        Assert.True(IpAddressPolicy.IsPubliclyRoutable(IPAddress.Parse("2002:808:808::1")));
    }
}
