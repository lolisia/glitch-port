using Microsoft.Extensions.Configuration;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class RelayOptionsTests
{
    [Fact]
    public void FromConfiguration_WithoutRelaySection_UsesDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Empty(options.ProcessorEndpoints);
        Assert.Equal(string.Empty, options.InternalSharedSecret);
        Assert.Empty(options.Projects);
        Assert.Equal(TimeSpan.FromSeconds(RelayOptions.DefaultTimeoutSeconds), options.Timeout);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void FromConfiguration_WithInvalidTimeout_UsesDefault(string timeout)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Timeout"] = timeout,
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(RelayOptions.DefaultTimeoutSeconds), options.Timeout);
    }

    [Fact]
    public void FromConfiguration_BindsProjectsEndpointsAndSecret()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Projects:1:0"] = "first-key",
                ["Relay:Projects:1:1"] = "second-key",
                ["Relay:Projects:7:0"] = "other-key",
                ["Relay:ProcessorEndpoints:0"] = "http://processor-a:5090",
                ["Relay:ProcessorEndpoints:1"] = "http://processor-b:5090",
                ["Relay:InternalSharedSecret"] = "shared-secret",
                ["Relay:Timeout"] = "2.5",
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(
            new[] { "http://processor-a:5090", "http://processor-b:5090" },
            options.ProcessorEndpoints);
        Assert.Equal("shared-secret", options.InternalSharedSecret);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.Timeout);
        Assert.Equal(new[] { "first-key", "second-key" }, options.Projects[1]);
        Assert.Equal(new[] { "other-key" }, options.Projects[7]);
    }
}
