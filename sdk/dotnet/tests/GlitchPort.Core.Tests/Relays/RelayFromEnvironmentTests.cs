using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class RelayFromEnvironmentTests
    {
        private const string Variable = "GLITCHPORT_DSN";

        [Fact]
        public void FromEnvironment_WithoutTheVariable_ReturnsOptionsWithoutRelays()
        {
            WithEnvironment(null, () =>
            {
                var options = GlitchPortOptions.FromEnvironment();

                Assert.Empty(options.Relays);
                Assert.Null(options.Dsn);
                Assert.Equal(GlitchPortOptions.DefaultSdkName, options.SdkName);
            });
        }

        [Fact]
        public void FromEnvironment_WithABlankVariable_ReturnsOptionsWithoutRelays()
        {
            WithEnvironment("   ; ;  ", () =>
            {
                var options = GlitchPortOptions.FromEnvironment();

                Assert.Empty(options.Relays);
                Assert.Null(options.Dsn);
            });
        }

        [Fact]
        public void FromEnvironment_ParsesTheRelayDefinitionsWithoutSettingDsn()
        {
            WithEnvironment(
                "terminal:?format=compact&name=term;http://public-key@relay.example.com:5080/7?min_level=error&name=web",
                () =>
                {
                    var options = GlitchPortOptions.FromEnvironment();

                    Assert.Null(options.Dsn);
                    Assert.Equal(2, options.Relays.Count);

                    Assert.Equal("terminal", options.Relays[0].Type);
                    Assert.Equal("term", options.Relays[0].Name);
                    Assert.Equal(Level.Debug, options.Relays[0].MinLevel);
                    Assert.Equal(
                        TerminalFormat.Compact,
                        Assert.IsType<TerminalRelayOptions>(options.Relays[0].Options).Format);

                    Assert.Equal("http", options.Relays[1].Type);
                    Assert.Equal("web", options.Relays[1].Name);
                    Assert.Equal(Level.Error, options.Relays[1].MinLevel);
                });
        }

        [Fact]
        public void FromEnvironment_WithAnInvalidRelay_ThrowsFormatException()
        {
            WithEnvironment("terminal:?window=popup", () =>
            {
                var exception = Assert.Throws<FormatException>(() => GlitchPortOptions.FromEnvironment());

                Assert.Contains("#1", exception.Message, StringComparison.Ordinal);
                Assert.Contains("window", exception.Message, StringComparison.Ordinal);
            });
        }

        private static void WithEnvironment(string? value, Action action)
        {
            var original = Environment.GetEnvironmentVariable(Variable);
            Environment.SetEnvironmentVariable(Variable, value);
            try
            {
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(Variable, original);
            }
        }
    }
}
