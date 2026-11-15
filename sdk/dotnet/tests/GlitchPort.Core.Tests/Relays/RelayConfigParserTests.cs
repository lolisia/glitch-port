using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class RelayConfigParserTests
    {
        private const string HttpRelay = "http://public-key@relay.example.com:5080/7?min_level=error&name=main";

        [Fact]
        public void Parse_HttpRelay_ParsesTheDsnAndParameters()
        {
            var relays = RelayConfigParser.Parse(HttpRelay);

            var definition = Assert.Single(relays);
            Assert.Equal("http", definition.Type);
            Assert.Equal("main", definition.Name);
            Assert.Equal(Level.Error, definition.MinLevel);
            Assert.True(definition.Enabled);
            Assert.IsType<HttpRelayOptions>(definition.Options);
        }

        [Fact]
        public void Parse_HttpRelay_WithoutParametersUsesTheDefaults()
        {
            var definition = Assert.Single(
                RelayConfigParser.Parse("http://public-key@relay.example.com:5080/7"));

            Assert.Equal("http", definition.Type);
            Assert.Equal("http", definition.Name);
            Assert.Equal(Level.Debug, definition.MinLevel);
            Assert.True(definition.Enabled);

            var options = Assert.IsType<HttpRelayOptions>(definition.Options);
            Assert.Null(options.OfflineQueue.Directory);
            Assert.Equal(500, options.OfflineQueue.MaxEvents);
            Assert.Equal(72, options.OfflineQueue.MaxAgeHours);
            Assert.Equal(30, options.OfflineQueue.RetryIntervalSeconds);
        }

        [Fact]
        public void Parse_HttpRelay_ParsesTheOfflineQueueParameters()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "http://public-key@relay.example.com:5080/7?offline_dir=C%3A%5Cgames%5Clogs%2Fqueue"
                + "&offline_max_events=100&offline_max_age_hours=0&offline_retry_interval_seconds=5"));

            var options = Assert.IsType<HttpRelayOptions>(definition.Options);
            Assert.Equal(@"C:\games\logs/queue", options.OfflineQueue.Directory);
            Assert.Equal(100, options.OfflineQueue.MaxEvents);
            Assert.Equal(0, options.OfflineQueue.MaxAgeHours);
            Assert.Equal(5, options.OfflineQueue.RetryIntervalSeconds);
        }

        [Fact]
        public void Parse_HttpRelay_ParsesTheDocumentedOfflineQueueExample()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "http://key@host:5080/1?offline_dir=C%3A%5Cgames%5Clogs%2Fqueue&offline_max_events=500"));

            var options = Assert.IsType<HttpRelayOptions>(definition.Options);
            Assert.Equal(@"C:\games\logs/queue", options.OfflineQueue.Directory);
            Assert.Equal(500, options.OfflineQueue.MaxEvents);
            Assert.Equal(72, options.OfflineQueue.MaxAgeHours);
            Assert.Equal(30, options.OfflineQueue.RetryIntervalSeconds);
        }

        [Fact]
        public void Parse_HttpRelay_ExpandsTheOfflineDirectoryLikeTheFileRelay()
        {
            var name = "GLITCHPORT_TEST_OFFLINE_DIR_" + Guid.NewGuid().ToString("N");
            const string Value = "custom/queue";
            Environment.SetEnvironmentVariable(name, Value);
            try
            {
                var definition = Assert.Single(RelayConfigParser.Parse(
                    "http://public-key@relay.example.com:5080/7?offline_dir=~%2F%25" + name + "%25"
                    + "%2F%24" + name
                    + "%2F%24%7B" + name + "%7D"));

                var options = Assert.IsType<HttpRelayOptions>(definition.Options);
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                Assert.Equal(
                    Path.Combine(profile, Value + "/" + Value + "/" + Value),
                    options.OfflineQueue.Directory);
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [Fact]
        public void Parse_HttpRelay_LeavesTheOfflineDirectoryNullWhenItIsAbsent()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "http://public-key@relay.example.com:5080/7?offline_max_events=10"));

            var options = Assert.IsType<HttpRelayOptions>(definition.Options);
            Assert.Null(options.OfflineQueue.Directory);
            Assert.Equal(10, options.OfflineQueue.MaxEvents);
        }

        [Fact]
        public void Parse_HttpsRelay_IsAccepted()
        {
            var definition = Assert.Single(
                RelayConfigParser.Parse("https://public-key@relay.example.com/9?name=secure"));

            Assert.Equal("http", definition.Type);
            Assert.Equal("secure", definition.Name);
            Assert.Equal(Level.Debug, definition.MinLevel);
        }

        [Fact]
        public void Parse_TerminalRelay_WithoutParametersUsesTheDefaults()
        {
            var definition = Assert.Single(RelayConfigParser.Parse("terminal"));

            Assert.Equal("terminal", definition.Type);
            Assert.Equal("terminal", definition.Name);
            Assert.Equal(Level.Debug, definition.MinLevel);
            Assert.True(definition.Enabled);

            var options = Assert.IsType<TerminalRelayOptions>(definition.Options);
            Assert.Equal(TerminalWindow.Inherit, options.Window);
            Assert.Equal(TerminalFormat.Pretty, options.Format);
            Assert.Null(options.ExecutablePath);
        }

        [Fact]
        public void Parse_TerminalRelay_ParsesAllParameters()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "terminal:?window=new&format=compact&min_level=warning&name=term&path=C%3A%5Ctools%5Cglitchport-terminal.exe"));

            Assert.Equal("terminal", definition.Type);
            Assert.Equal("term", definition.Name);
            Assert.Equal(Level.Warning, definition.MinLevel);
            Assert.True(definition.Enabled);

            var options = Assert.IsType<TerminalRelayOptions>(definition.Options);
            Assert.Equal(TerminalWindow.New, options.Window);
            Assert.Equal(TerminalFormat.Compact, options.Format);
            Assert.Equal(@"C:\tools\glitchport-terminal.exe", options.ExecutablePath);
        }

        [Fact]
        public void Parse_TerminalRelay_ParsesTheJsonFormat()
        {
            var definition = Assert.Single(
                RelayConfigParser.Parse("terminal:?window=inherit&format=json"));

            var options = Assert.IsType<TerminalRelayOptions>(definition.Options);
            Assert.Equal(TerminalWindow.Inherit, options.Window);
            Assert.Equal(TerminalFormat.Json, options.Format);
        }

        [Fact]
        public void Parse_FileRelay_WithoutParametersUsesTheDefaults()
        {
            var definition = Assert.Single(RelayConfigParser.Parse("file"));

            Assert.Equal("file", definition.Type);
            Assert.Equal("file", definition.Name);
            Assert.Equal(Level.Debug, definition.MinLevel);
            Assert.True(definition.Enabled);

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal("logs/glitchport", options.Directory);
            Assert.Equal(FileFormat.Ndjson, options.Format);
            Assert.Equal(10, options.MaxFileSizeMb);
            Assert.Equal(7, options.MaxAgeDays);
            Assert.Equal(200, options.MaxTotalSizeMb);
            Assert.Equal(100, options.MaxFiles);
            Assert.Equal(1000, options.FlushIntervalMs);
        }

        [Fact]
        public void Parse_FileRelay_ParsesAllParameters()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "file:?dir=logs%2Fcustom&format=text&max_file_size_mb=2&max_age_days=3"
                + "&max_total_size_mb=50&max_files=5&flush_interval_ms=250&min_level=info&name=files"));

            Assert.Equal("file", definition.Type);
            Assert.Equal("files", definition.Name);
            Assert.Equal(Level.Info, definition.MinLevel);
            Assert.True(definition.Enabled);

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal("logs/custom", options.Directory);
            Assert.Equal(FileFormat.Text, options.Format);
            Assert.Equal(2, options.MaxFileSizeMb);
            Assert.Equal(3, options.MaxAgeDays);
            Assert.Equal(50, options.MaxTotalSizeMb);
            Assert.Equal(5, options.MaxFiles);
            Assert.Equal(250, options.FlushIntervalMs);
        }

        [Fact]
        public void Parse_FileRelay_AcceptsZeroRetentionLimits()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "file:?max_age_days=0&max_total_size_mb=0&max_files=0"));

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal(0, options.MaxAgeDays);
            Assert.Equal(0, options.MaxTotalSizeMb);
            Assert.Equal(0, options.MaxFiles);
        }

        [Fact]
        public void Parse_FileRelay_AcceptsTheMinimumFileSizeAndFlushInterval()
        {
            var definition = Assert.Single(RelayConfigParser.Parse(
                "file:?max_file_size_mb=1&flush_interval_ms=1"));

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal(1, options.MaxFileSizeMb);
            Assert.Equal(1, options.FlushIntervalMs);
        }

        [Fact]
        public void Parse_SplitsOnSemicolons_IgnoresBlankItemsAndKeepsTheInputOrder()
        {
            var relays = RelayConfigParser.Parse(
                " terminal ; ;file:?format=text;;http://public-key@relay.example.com:5080/7?name=web; ");

            Assert.Equal(3, relays.Count);
            Assert.Equal(new[] { "terminal", "file", "web" }, relays.Select(relay => relay.Name));
            Assert.Equal(new[] { "terminal", "file", "http" }, relays.Select(relay => relay.Type));
            Assert.All(relays, relay => Assert.True(relay.Enabled));
        }

        [Fact]
        public void Parse_BlankValue_ReturnsAnEmptyList()
        {
            Assert.Empty(RelayConfigParser.Parse("   ;; ;  "));
        }

        [Fact]
        public void Parse_Null_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => RelayConfigParser.Parse(null!));
        }

        [Fact]
        public void Parse_MinLevel_IsCaseInsensitive()
        {
            var definition = Assert.Single(RelayConfigParser.Parse("terminal:?min_level=WARNING"));

            Assert.Equal(Level.Warning, definition.MinLevel);
        }

        [Fact]
        public void Parse_QueryValues_AreUrlDecoded()
        {
            var definition = Assert.Single(
                RelayConfigParser.Parse("file:?dir=logs%2Fsome%20dir&name=my%20file"));

            Assert.Equal("my file", definition.Name);
            Assert.Equal("logs/some dir", Assert.IsType<FileRelayOptions>(definition.Options).Directory);
        }

        [Fact]
        public void Parse_FileRelay_ExpandsPercentDollarAndBraceEnvironmentVariables()
        {
            var name = "GLITCHPORT_TEST_DIR_" + Guid.NewGuid().ToString("N");
            const string Value = "custom/logs";
            Environment.SetEnvironmentVariable(name, Value);
            try
            {
                var definition = Assert.Single(RelayConfigParser.Parse(
                    "file:?dir=%25" + name + "%25/$" + name + "/${" + name + "}"));

                var options = Assert.IsType<FileRelayOptions>(definition.Options);
                Assert.Equal(Value + "/" + Value + "/" + Value, options.Directory);
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [Fact]
        public void Parse_FileRelay_ExpandsALeadingTilde()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var withSlash = Assert.Single(RelayConfigParser.Parse("file:?dir=~/glitchport-logs"));
            Assert.Equal(
                Path.Combine(profile, "glitchport-logs"),
                Assert.IsType<FileRelayOptions>(withSlash.Options).Directory);

            var alone = Assert.Single(RelayConfigParser.Parse("file:?dir=~"));
            Assert.Equal(profile, Assert.IsType<FileRelayOptions>(alone.Options).Directory);
        }

        [Fact]
        public void Parse_FileRelay_LeavesUnknownEnvironmentVariablesLiteral()
        {
            var name = "GLITCHPORT_TEST_MISSING_" + Guid.NewGuid().ToString("N");

            var definition = Assert.Single(RelayConfigParser.Parse(
                "file:?dir=%25" + name + "%25/$" + name + "/${" + name + "}"));

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal("%" + name + "%/$" + name + "/${" + name + "}", options.Directory);
        }

        [Fact]
        public void Parse_InvalidItem_ReportsItsPositionAmongNonEmptyItems()
        {
            var exception = Assert.Throws<FormatException>(
                () => RelayConfigParser.Parse("; terminal ; ; file:?max_files=nope ;"));

            Assert.Contains("#2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("max_files", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("bogus", "#1", "unknown relay type")]
        [InlineData("ftp://relay.example.com:5080/1", "#1", "unknown relay type")]
        [InlineData("http://relay.example.com/1", "#1", "malformed DSN")]
        [InlineData("http://public-key@relay.example.com:5080/not-a-number", "#1", "malformed DSN")]
        [InlineData("terminal:?bogus=1", "#1", "unknown parameter 'bogus'")]
        [InlineData("terminal:?window=popup", "#1", "unknown window value")]
        [InlineData("terminal:?format=ndjson", "#1", "unknown format value")]
        [InlineData("terminal:?min_level=verbose", "#1", "unknown min_level value")]
        [InlineData("file:?format=pretty", "#1", "unknown format value")]
        [InlineData("file:?max_files=abc", "#1", "not a valid integer")]
        [InlineData("file:?max_file_size_mb=0", "#1", "must be >= 1")]
        [InlineData("file:?flush_interval_ms=0", "#1", "must be >= 1")]
        [InlineData("file:?max_age_days=-1", "#1", "must be >= 0")]
        [InlineData("file:?max_total_size_mb=-1", "#1", "must be >= 0")]
        [InlineData("file:?max_files=-1", "#1", "must be >= 0")]
        [InlineData("http://public-key@relay.example.com:5080/1?window=new", "#1", "unknown parameter 'window'")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_dir=", "#1", "must not be blank")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_dir=%20", "#1", "must not be blank")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_max_events=abc", "#1", "not a valid integer")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_max_events=", "#1", "not a valid integer")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_max_events=0", "#1", "must be >= 1")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_max_age_hours=-1", "#1", "must be >= 0")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_retry_interval_seconds=0", "#1", "must be >= 1")]
        [InlineData("http://public-key@relay.example.com:5080/1?offline_bogus=1", "#1", "unknown parameter 'offline_bogus'")]
        public void Parse_InvalidItem_ThrowsFormatExceptionWithIndexAndReason(
            string config,
            string expectedIndex,
            string expectedReason)
        {
            var exception = Assert.Throws<FormatException>(() => RelayConfigParser.Parse(config));

            Assert.Contains(expectedIndex, exception.Message, StringComparison.Ordinal);
            Assert.Contains(expectedReason, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_InvalidOfflineParameter_ReportsItsPositionAmongNonEmptyItems()
        {
            var exception = Assert.Throws<FormatException>(() => RelayConfigParser.Parse(
                "terminal; http://public-key@relay.example.com:5080/1?offline_max_events=0"));

            Assert.Contains("#2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("offline_max_events", exception.Message, StringComparison.Ordinal);
        }
    }
}
