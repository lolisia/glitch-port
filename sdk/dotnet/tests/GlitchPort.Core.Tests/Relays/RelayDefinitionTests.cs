using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class RelayDefinitionTests
    {
        private const string ValidDsn = "http://public-key@relay.example.com:5080/7";

        [Fact]
        public void RelayHttp_ParsesTheDsnAndUsesTheHttpDefaults()
        {
            var definition = Relay.Http(ValidDsn);

            Assert.Equal("http", definition.Type);
            Assert.Equal("http", definition.Name);
            Assert.True(definition.Enabled);
            Assert.Equal(Level.Debug, definition.MinLevel);

            var options = Assert.IsType<HttpRelayOptions>(definition.Options);
            Assert.Null(options.Name);
            Assert.True(options.Enabled);
            Assert.Equal(Level.Debug, options.MinLevel);
            Assert.Equal(1000, options.QueueCapacity);
        }

        [Fact]
        public void RelayHttp_ConfigureCallbackUpdatesTheDefinition()
        {
            var definition = Relay.Http(ValidDsn, options =>
            {
                options.Name = "backup";
                options.Enabled = false;
                options.MinLevel = Level.Error;
                options.QueueCapacity = 10;
            });

            Assert.Equal("backup", definition.Name);
            Assert.False(definition.Enabled);
            Assert.Equal(Level.Error, definition.MinLevel);

            var options = Assert.IsType<HttpRelayOptions>(definition.Options);
            Assert.Equal("backup", options.Name);
            Assert.False(options.Enabled);
            Assert.Equal(Level.Error, options.MinLevel);
            Assert.Equal(10, options.QueueCapacity);
        }

        [Fact]
        public void NameSetter_UpdatesTheUnderlyingOptionsAndFallsBackToTheType()
        {
            var definition = new HttpRelayDefinition(Dsn.Parse(ValidDsn));

            Assert.Equal("http", definition.Name);

            definition.Name = "renamed";
            Assert.Equal("renamed", definition.Name);
            Assert.Equal("renamed", definition.Options.Name);

            definition.Enabled = false;
            definition.MinLevel = Level.Warning;
            Assert.False(definition.Options.Enabled);
            Assert.Equal(Level.Warning, definition.Options.MinLevel);
        }

        [Fact]
        public void RelayHttp_WithInvalidDsn_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => Relay.Http("not a dsn"));
        }

        [Fact]
        public void HttpRelayDefinition_WithoutDsn_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new HttpRelayDefinition(null!));
        }
    }
}
