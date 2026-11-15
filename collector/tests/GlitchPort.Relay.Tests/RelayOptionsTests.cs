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
        Assert.Equal(
            RelayOptions.DefaultConfigRefreshSeconds,
            options.ConfigRefreshSeconds);
        Assert.Equal(
            RelayOptions.DefaultUnknownKeyCacheSeconds,
            options.UnknownKeyCacheSeconds);
        Assert.Equal(
            RelayOptions.DefaultRateLimitPerProjectPerMinute,
            options.RateLimitPerProjectPerMinute);
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

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void FromConfiguration_WithInvalidConfigRefreshSeconds_UsesDefault(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:ConfigRefreshSeconds"] = value,
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(
            RelayOptions.DefaultConfigRefreshSeconds,
            options.ConfigRefreshSeconds);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0.5")]
    public void FromConfiguration_WithInvalidUnknownKeyCacheSeconds_UsesDefault(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:UnknownKeyCacheSeconds"] = value,
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(
            RelayOptions.DefaultUnknownKeyCacheSeconds,
            options.UnknownKeyCacheSeconds);
    }

    [Fact]
    public void FromConfiguration_PreservesDisabledUnknownKeyCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:UnknownKeyCacheSeconds"] = "0",
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(0, options.UnknownKeyCacheSeconds);
    }

    [Fact]
    public void FromConfiguration_PreservesUnlimitedRateLimit()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:RateLimit:PerProjectPerMinute"] = "0",
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(0, options.RateLimitPerProjectPerMinute);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("")]
    public void FromConfiguration_WithInvalidRateLimit_UsesDefault(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:RateLimit:PerProjectPerMinute"] = value,
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(
            RelayOptions.DefaultRateLimitPerProjectPerMinute,
            options.RateLimitPerProjectPerMinute);
    }

    [Fact]
    public void FromConfiguration_BindsRateLimit()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:RateLimit:PerProjectPerMinute"] = "250",
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(250, options.RateLimitPerProjectPerMinute);
    }

    [Fact]
    public void FromConfiguration_BindsRefreshSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:ConfigRefreshSeconds"] = "5",
                ["Relay:UnknownKeyCacheSeconds"] = "120",
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(5, options.ConfigRefreshSeconds);
        Assert.Equal(120, options.UnknownKeyCacheSeconds);
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
                ["Relay:ConfigRefreshSeconds"] = "3",
                ["Relay:UnknownKeyCacheSeconds"] = "7",
            })
            .Build();

        var options = RelayOptions.FromConfiguration(configuration);

        Assert.Equal(
            new[] { "http://processor-a:5090", "http://processor-b:5090" },
            options.ProcessorEndpoints);
        Assert.Equal("shared-secret", options.InternalSharedSecret);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.Timeout);
        Assert.Equal(3, options.ConfigRefreshSeconds);
        Assert.Equal(7, options.UnknownKeyCacheSeconds);
        Assert.Equal(new[] { "first-key", "second-key" }, options.Projects[1]);
        Assert.Equal(new[] { "other-key" }, options.Projects[7]);
    }
}
