using System.Net;
using Pulse.Application;

namespace Pulse.Tests;

public class TargetValidatorTests
{
    private static readonly Dictionary<string, IPAddress[]> Hosts = new()
    {
        ["public.test"] = [IPAddress.Parse("8.8.8.8")],
        ["internal.test"] = [IPAddress.Parse("10.0.0.5")],
        ["mixed.test"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")],
        ["metadata.test"] = [IPAddress.Parse("169.254.169.254")],
    };

    private static TargetValidator Create(bool allowPrivate = false) =>
        new(new FakeResolver(Hosts), OptionsFactory.Create(o => o.AllowPrivateTargets = allowPrivate));

    [Theory]
    [InlineData("https://public.test/")]
    [InlineData("http://public.test/health")]
    [InlineData("https://public.test:8443/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    public async Task A_public_address_is_accepted(string url)
    {
        Assert.Null(await Create().ValidateAsync(url, CancellationToken.None));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://public.test/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//public.test/")]
    public async Task A_malformed_or_non_http_address_is_refused(string url)
    {
        Assert.NotNull(await Create().ValidateAsync(url, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.0.10/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://2130706433/")] // 127.0.0.1 written as one decimal number
    [InlineData("http://0x7f.1/")] // 127.0.0.1 written in hexadecimal
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://internal.test/")]
    [InlineData("http://metadata.test/")]
    public async Task An_address_that_points_inside_the_network_is_refused(string url)
    {
        Assert.NotNull(await Create().ValidateAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task A_host_with_one_private_answer_among_public_ones_is_refused()
    {
        Assert.NotNull(await Create().ValidateAsync("http://mixed.test/", CancellationToken.None));
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_is_refused()
    {
        Assert.NotNull(await Create().ValidateAsync("http://unknown.test/", CancellationToken.None));
    }

    [Fact]
    public async Task Credentials_in_the_address_are_refused()
    {
        Assert.NotNull(await Create().ValidateAsync("https://user:secret@public.test/", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://public.test:22/")]
    [InlineData("http://public.test:6379/")]
    public async Task A_port_outside_the_allowed_list_is_refused(string url)
    {
        Assert.NotNull(await Create().ValidateAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task Private_targets_can_be_allowed_for_local_demos()
    {
        Assert.Null(await Create(allowPrivate: true).ValidateAsync("http://127.0.0.1:3000/", CancellationToken.None));
    }
}
