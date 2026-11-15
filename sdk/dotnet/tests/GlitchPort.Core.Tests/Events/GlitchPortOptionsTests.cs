using System;
using Xunit;

namespace GlitchPort.Tests.Events
{
    public sealed class GlitchPortOptionsTests
    {
        [Fact]
        public void Defaults_MatchTheDocumentedValues()
        {
            var options = new GlitchPortOptions();

            Assert.Null(options.Release);
            Assert.Null(options.Environment);
            Assert.Equal(Environment.MachineName, options.ServerName);
            Assert.Equal("dotnet", options.Platform);
            Assert.Equal(1d, options.SampleRate);
            Assert.Equal(100, options.MaxBreadcrumbs);
            Assert.Null(options.BeforeSend);
            Assert.Empty(options.InAppInclude);
            Assert.Equal(
                new[] { "System.", "Microsoft.", "GlitchPort.", "UnityEngine.", "UnityEditor.", "Unity." },
                options.InAppExclude);
            Assert.False(options.SendDefaultPii);
            Assert.False(options.CaptureUnhandledExceptions);

            // The existing options keep their defaults.
            Assert.Equal(GlitchPortOptions.DefaultSdkName, options.SdkName);
            Assert.Null(options.SdkVersion);
            Assert.Empty(options.Relays);
            Assert.Null(options.Dsn);
        }

        [Theory]
        [InlineData(0d)]
        [InlineData(0.25d)]
        [InlineData(1d)]
        public void SampleRate_AcceptsValuesInsideTheRange(double value)
        {
            var options = new GlitchPortOptions { SampleRate = value };

            Assert.Equal(value, options.SampleRate);
        }

        [Theory]
        [InlineData(-0.001d)]
        [InlineData(1.001d)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void SampleRate_RejectsValuesOutsideTheRange(double value)
        {
            var options = new GlitchPortOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.SampleRate = value);
        }

        [Fact]
        public void FromEnvironment_ReadsReleaseAndEnvironmentWithoutADsn()
        {
            WithEnvironment("GLITCHPORT_DSN", null, () =>
                WithEnvironment("GLITCHPORT_RELEASE", "star-raiders@1.2.3", () =>
                    WithEnvironment("GLITCHPORT_ENVIRONMENT", "staging", () =>
                    {
                        var options = GlitchPortOptions.FromEnvironment();

                        Assert.Empty(options.Relays);
                        Assert.Equal("star-raiders@1.2.3", options.Release);
                        Assert.Equal("staging", options.Environment);
                    })));
        }

        [Fact]
        public void FromEnvironment_IgnoresBlankReleaseAndEnvironment()
        {
            WithEnvironment("GLITCHPORT_DSN", "   ", () =>
                WithEnvironment("GLITCHPORT_RELEASE", "   ", () =>
                    WithEnvironment("GLITCHPORT_ENVIRONMENT", null, () =>
                    {
                        var options = GlitchPortOptions.FromEnvironment();

                        Assert.Empty(options.Relays);
                        Assert.Null(options.Release);
                        Assert.Null(options.Environment);
                    })));
        }

        private static void WithEnvironment(string name, string? value, Action action)
        {
            var original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);

            try
            {
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, original);
            }
        }
    }
}
