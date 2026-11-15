using GlitchPort.Processor.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Binding rules of the <c>Processor:Retention</c> configuration section.
/// </summary>
public sealed class RetentionOptionsTests
{
    [Fact]
    public void Defaults_are_30_days_hourly_with_a_10000_row_batch()
    {
        var options = Resolve([]);

        Assert.Equal(30, options.Days);
        Assert.Equal(60, options.IntervalMinutes);
        Assert.Equal(10_000, options.BatchSize);
    }

    [Fact]
    public void Values_are_bound_from_the_processor_retention_section()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Processor:Retention:Days"] = "7",
            ["Processor:Retention:IntervalMinutes"] = "15",
            ["Processor:Retention:BatchSize"] = "250",
        });

        Assert.Equal(7, options.Days);
        Assert.Equal(15, options.IntervalMinutes);
        Assert.Equal(250, options.BatchSize);
    }

    [Fact]
    public void Days_zero_disables_retention_and_is_preserved()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Processor:Retention:Days"] = "0",
        });

        Assert.Equal(0, options.Days);
    }

    [Fact]
    public void Negative_days_fall_back_to_the_default()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Processor:Retention:Days"] = "-1",
        });

        Assert.Equal(RetentionOptions.DefaultDays, options.Days);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-15")]
    public void Non_positive_intervals_fall_back_to_the_default(string intervalMinutes)
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Processor:Retention:IntervalMinutes"] = intervalMinutes,
        });

        Assert.Equal(RetentionOptions.DefaultIntervalMinutes, options.IntervalMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-100")]
    public void Non_positive_batch_sizes_fall_back_to_the_default(string batchSize)
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Processor:Retention:BatchSize"] = batchSize,
        });

        Assert.Equal(RetentionOptions.DefaultBatchSize, options.BatchSize);
    }

    private static RetentionOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.Configure<RetentionOptions>(configuration.GetSection(
            $"{ProcessorOptions.SectionName}:{RetentionOptions.SectionName}"));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RetentionOptions>>().Value;
    }
}
