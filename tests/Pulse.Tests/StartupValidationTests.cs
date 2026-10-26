using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pulse.Application;
using Pulse.Application.Accounts;

namespace Pulse.Tests;

/// <summary>
/// The host registers the options the way Program.cs does (data annotations included). Our own validators must
/// still run: they once did not, because a data-annotations validator for the same type was already registered.
/// </summary>
public sealed class StartupValidationTests
{
    private static readonly string ValidKey = Convert.ToBase64String(new byte[32]);

    [Theory]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEA==", "http://localhost:5090")]
    [InlineData("not base64 at all !", "http://localhost:5090")]
    [InlineData("", "http://localhost:5090")]
    public void An_invalid_encryption_key_stops_the_start(string key, string webBaseUrl)
    {
        var options = Resolve(key, webBaseUrl);

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void A_web_address_that_is_not_a_url_stops_the_start()
    {
        var options = Resolve(ValidKey, "pas une adresse");

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void A_valid_configuration_starts()
    {
        var options = Resolve(ValidKey, "https://pulse.example.org");

        Assert.Equal("https://pulse.example.org", options.Value.WebBaseUrl);
    }

    private static IOptions<AccountOptions> Resolve(string key, string webBaseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pulse:Accounts:EncryptionKey"] = key,
                ["Pulse:Accounts:WebBaseUrl"] = webBaseUrl,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AccountOptions>().BindConfiguration(AccountOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddPulseApplication();
        return services.BuildServiceProvider().GetRequiredService<IOptions<AccountOptions>>();
    }
}
