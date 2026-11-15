using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Terminal.Tests
{
    public sealed class TerminalHostTests
    {
        private const string HeaderPrefix = "\u2550\u2550\u2550 glitch-port terminal \u2550\u2550\u2550";
        private const string InvalidLine = "\u26a0 invalid line skipped";
        private const string UnsupportedProtocol = "\u26a0 unsupported protocol";

        private const string Payload =
            "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\",\"level\":\"info\",\"message\":\"hello\",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}";

        private const string EventIdentity =
            "\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\"";

        private const string Sdk = "\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}";

        /// <summary>
        /// An exception-only v1 payload: no message, four frames (one of them not
        /// in-app and two of them at the same file), tags, release and environment.
        /// The wire frames run from the outermost caller to the crash site.
        /// </summary>
        private const string ExceptionPayload =
            "{" + EventIdentity + ",\"level\":\"error\","
            + "\"exception\":[{\"type\":\"System.InvalidOperationException\",\"value\":\"boom\","
            + "\"stacktrace\":{\"frames\":["
            + "{\"function\":\"Main\",\"module\":\"Game\",\"filename\":\"Program.cs\",\"lineno\":9,\"in_app\":true},"
            + "{\"function\":\"Invoke\",\"module\":\"System\",\"filename\":\"Runtime.cs\",\"lineno\":1,\"in_app\":false},"
            + "{\"function\":\"Update\",\"module\":\"Game\",\"filename\":\"Update.cs\",\"lineno\":42,\"in_app\":true},"
            + "{\"function\":\"Throw\",\"module\":\"Game\",\"filename\":\"Update.cs\",\"lineno\":50,\"in_app\":true}"
            + "]}}],"
            + "\"tags\":{\"z\":\"last\",\"a\":\"first\"},"
            + "\"release\":\"star-raiders@1.2.3\",\"environment\":\"production\","
            + Sdk + "}";

        private static readonly DateTimeOffset PayloadTimestamp = DateTimeOffset.Parse(
            "2026-11-14T15:02:14.010Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);


        [Fact]
        public async Task HelloAndEvent_Compact_RendersHeaderAndEventLineWithoutColor()
        {
            var result = await RunAsync(HelloLine() + "\n" + EventLine(Payload) + "\n", TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, lines.Length);
            Assert.StartsWith(HeaderPrefix + " hello-game (pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), lines[0]);
            Assert.Contains("protocol 1", lines[0]);
            Assert.Contains("INFO", lines[1]);
            Assert.Contains("hello", lines[1]);
            Assert.Contains("(id 3b7e0c11)", lines[1]);
            Assert.False(result.Output.Contains('\u001b'));
        }

        [Fact]
        public async Task HelloAndEvent_Pretty_RendersSdkFooterAndEventId()
        {
            var result = await RunAsync(HelloLine() + "\n" + EventLine(Payload) + "\n", TerminalHostFormat.Pretty);

            Assert.Contains("sdk=glitchport.dotnet/0.1.0", result.Output);
            Assert.Contains("id 3b7e0c11", result.Output);
            Assert.Contains("INFO", result.Output);
            Assert.False(result.Output.Contains('\u001b'));
        }

        [Fact]
        public async Task Event_JsonFormat_PrintsPayloadVerbatim()
        {
            const string spacedPayload =
                "{ \"event_id\" : \"3b7e0c11-1111-2222-3333-444455556666\", \"timestamp\" : \"2026-11-14T15:02:14.010Z\", \"level\" : \"warning\", \"message\" : \"raw\", \"sdk\" : { \"name\" : \"glitchport.dotnet\", \"version\" : \"0.1.0\" } }";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(spacedPayload) + "\n", TerminalHostFormat.Json);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal(spacedPayload, lines[1]);
        }

        [Fact]
        public async Task Event_NonAsciiMessage_IsPreserved()
        {
            const string payload =
                "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\",\"level\":\"error\",\"message\":\"\ud55c\uae00 \uc624\ub958: \uc5f0\uacb0 \uc2e4\ud328\",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Compact);

            Assert.Contains("\ud55c\uae00 \uc624\ub958: \uc5f0\uacb0 \uc2e4\ud328", result.Output);
        }

        [Fact]
        public async Task InvalidLine_IsSkippedAndFollowingEventIsRendered()
        {
            var result = await RunAsync(
                HelloLine() + "\n" + "this is not json\n" + EventLine(Payload) + "\n",
                TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.Equal(InvalidLine, lines[1]);
            Assert.Contains("INFO", lines[2]);
            Assert.Contains("hello", lines[2]);
        }

        [Fact]
        public async Task EventWithMissingFields_IsSkipped()
        {
            var result = await RunAsync(
                HelloLine() + "\n" + EventLine("{\"message\":\"orphan\"}") + "\n",
                TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal(InvalidLine, lines[1]);
            Assert.DoesNotContain("orphan", result.Output);
        }

        [Fact]
        public async Task Hello_UnsupportedProtocol_WarnsAndContinues()
        {
            var result = await RunAsync(HelloLine(protocol: 2) + "\n" + EventLine(Payload) + "\n", TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.StartsWith(HeaderPrefix, lines[0]);
            Assert.StartsWith(UnsupportedProtocol, lines[1]);
            Assert.Contains("INFO", lines[2]);
        }

        [Fact]
        public async Task EndOfInput_ReturnsZeroWithHeaderPrintedExactlyOnce()
        {
            var result = await RunAsync(HelloLine() + "\n", TerminalHostFormat.Compact);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, result.Output.Split('\n').Count(line => line.StartsWith(HeaderPrefix, StringComparison.Ordinal)));
        }

        [Fact]
        public async Task EmptyInput_ReturnsZeroWithoutOutput()
        {
            var result = await RunAsync(string.Empty, TerminalHostFormat.Compact);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Output);
        }

        [Fact]
        public async Task BlankLines_AreIgnored()
        {
            var result = await RunAsync(
                "\n \n" + HelloLine() + "\n\n" + EventLine(Payload) + "\n\n",
                TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith(HeaderPrefix, lines[0]);
            Assert.Contains("INFO", lines[1]);
        }

        [Fact]
        public async Task HelloWithoutApp_FallsBackToOptionsAppName()
        {
            var result = await RunAsync(
                "{\"type\":\"hello\",\"protocol\":1,\"parent_pid\":4242}\n",
                TerminalHostFormat.Compact,
                appName: "fallback-game");

            Assert.Contains("fallback-game", result.Output);
        }

        [Fact]
        public async Task NonHelloFirstLine_IsSkippedAndEventsStillRender()
        {
            var result = await RunAsync(
                "{\"type\":\"oops\"}\n" + EventLine(Payload) + "\n",
                TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.StartsWith(HeaderPrefix, lines[0]);
            Assert.Equal(InvalidLine, lines[1]);
            Assert.Contains("INFO", lines[2]);
        }

        [Fact]
        public async Task CancellationBetweenLines_IsHonoured()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var input = new StringReader(HelloLine() + "\n");
            using var output = new StringWriter();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => TerminalHost.RunAsync(input, output, new TerminalHostOptions(), cts.Token));
        }

        [Fact]
        public async Task ExceptionOnlyPayload_Compact_RendersTypeValueAndEventIdOnOneLine()
        {
            var result = await RunAsync(HelloLine() + "\n" + EventLine(ExceptionPayload) + "\n", TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(
                "ERROR System.InvalidOperationException: boom  (id 3b7e0c11)",
                lines[1],
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task ExceptionPayload_Pretty_PrintsCrashSiteFramesTagsMetadataAndFooterLast()
        {
            var result = await RunAsync(HelloLine() + "\n" + EventLine(ExceptionPayload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(9, lines.Length);
            Assert.EndsWith("ERROR System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
            Assert.Equal("  at Game.Throw (Update.cs:50)", lines[2]);
            Assert.Equal("  at Game.Update (Update.cs:42)", lines[3]);
            Assert.Equal("  at Game.Main (Program.cs:9)", lines[4]);
            Assert.Equal("  tags: a=first, z=last", lines[5]);
            Assert.Equal("  release: star-raiders@1.2.3", lines[6]);
            Assert.Equal("  environment: production", lines[7]);
            Assert.Equal("  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11", lines[8]);
        }

        [Fact]
        public async Task ExceptionPayload_JsonFormat_PrintsPayloadVerbatim()
        {
            var result = await RunAsync(HelloLine() + "\n" + EventLine(ExceptionPayload) + "\n", TerminalHostFormat.Json);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal(ExceptionPayload, lines[1]);
        }

        [Fact]
        public async Task ExceptionPayload_ManyInAppFrames_PrintsOnlyTheFiveClosestToTheCrashSite()
        {
            var payload = ManyFramesPayload(7);

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(8, lines.Length);
            Assert.Equal("  at Game.Frame7 (Game.cs:7)", lines[2]);
            Assert.Equal("  at Game.Frame6 (Game.cs:6)", lines[3]);
            Assert.Equal("  at Game.Frame5 (Game.cs:5)", lines[4]);
            Assert.Equal("  at Game.Frame4 (Game.cs:4)", lines[5]);
            Assert.Equal("  at Game.Frame3 (Game.cs:3)", lines[6]);
            Assert.Equal("  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11", lines[7]);
            Assert.DoesNotContain("Frame2", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Frame1", result.Output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ExceptionPayload_WithMessage_UsesTheExceptionTitle()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"error\",\"message\":\"custom message\","
                + "\"exception\":[{\"type\":\"System.InvalidOperationException\",\"value\":\"boom\"}],"
                + Sdk + "}";

            var pretty = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);
            var compact = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Compact);

            Assert.EndsWith(
                "ERROR System.InvalidOperationException: boom",
                pretty.Output.Split('\n')[1],
                StringComparison.Ordinal);
            Assert.EndsWith(
                "ERROR System.InvalidOperationException: boom  (id 3b7e0c11)",
                compact.Output.Split('\n')[1],
                StringComparison.Ordinal);
            Assert.DoesNotContain("custom message", pretty.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("custom message", compact.Output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task FrameWithoutInAppFlag_IsDisplayedAsAnApplicationFrame()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"error\","
                + "\"exception\":[{\"type\":\"System.InvalidOperationException\",\"value\":\"boom\","
                + "\"stacktrace\":{\"frames\":["
                + "{\"function\":\"Hidden\",\"module\":\"Game\",\"filename\":\"Hidden.cs\",\"lineno\":1},"
                + "{\"function\":\"Throw\",\"module\":\"Game\",\"filename\":\"Update.cs\",\"lineno\":50,\"in_app\":true}"
                + "]}}],"
                + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(5, lines.Length);
            Assert.Equal("  at Game.Throw (Update.cs:50)", lines[2]);
            Assert.Equal("  at Game.Hidden (Hidden.cs:1)", lines[3]);
            Assert.Equal("  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11", lines[4]);
        }

        [Fact]
        public async Task FrameWithNullInAppFlag_IsDisplayedAsAnApplicationFrame()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"error\","
                + "\"exception\":[{\"type\":\"System.InvalidOperationException\",\"value\":\"boom\","
                + "\"stacktrace\":{\"frames\":["
                + "{\"function\":\"Throw\",\"module\":\"Game\",\"filename\":\"Update.cs\",\"lineno\":50,\"in_app\":null}"
                + "]}}],"
                + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(4, lines.Length);
            Assert.Equal("  at Game.Throw (Update.cs:50)", lines[2]);
        }

        [Fact]
        public async Task MessageOnlyPayload_KeepsThePreviousCompactAndPrettyOutput()
        {
            var compact = await RunAsync(HelloLine() + "\n" + EventLine(Payload) + "\n", TerminalHostFormat.Compact);
            var pretty = await RunAsync(HelloLine() + "\n" + EventLine(Payload) + "\n", TerminalHostFormat.Pretty);

            var compactLines = compact.Output.Split('\n');
            Assert.Equal(2, compactLines.Length);
            Assert.Equal(LocalCompactTimestamp() + " INFO  hello  (id 3b7e0c11)", compactLines[1]);

            var prettyLines = pretty.Output.Split('\n');
            Assert.Equal(3, prettyLines.Length);
            Assert.Equal(LocalPrettyTimestamp() + " INFO  hello", prettyLines[1]);
            Assert.Equal("  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11", prettyLines[2]);
        }

        [Fact]
        public async Task MessageOnlyPayloadWithTagsAndMetadata_KeepsThePreviousPrettyOutput()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"info\",\"message\":\"hello\","
                + "\"tags\":{\"scene\":\"Stage1\"},"
                + "\"release\":\"star-raiders@1.2.3\",\"environment\":\"production\","
                + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.Equal(LocalPrettyTimestamp() + " INFO  hello", lines[1]);
            Assert.Equal("  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11", lines[2]);
            Assert.DoesNotContain("tags:", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("release:", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("environment:", result.Output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NullOptionalScalars_AreTreatedAsAbsent()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"error\",\"message\":null,"
                + "\"exception\":[{\"type\":\"System.InvalidOperationException\",\"value\":\"boom\"}],"
                + "\"tags\":null,\"release\":null,\"environment\":null,"
                + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.EndsWith("ERROR System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
            Assert.Equal("  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11", lines[2]);
        }

        [Theory]
        [InlineData("message", "42")]
        [InlineData("release", "42")]
        [InlineData("environment", "42")]
        public async Task NonStringOptionalScalar_IsSkipped(string property, string json)
        {
            var payload = property == "message"
                ? "{" + EventIdentity + ",\"level\":\"error\",\"message\":" + json + ","
                    + "\"exception\":[{\"type\":\"System.InvalidOperationException\",\"value\":\"boom\"}],"
                    + Sdk + "}"
                : "{" + EventIdentity + ",\"level\":\"info\",\"message\":\"hello\","
                    + "\"" + property + "\":" + json + ","
                    + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Pretty);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal(InvalidLine, lines[1]);
        }

        [Fact]
        public async Task PayloadWithoutMessageOrException_IsSkipped()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"error\"," + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal(InvalidLine, lines[1]);
        }

        [Fact]
        public async Task ExceptionEntryWithoutType_IsSkipped()
        {
            var payload = "{" + EventIdentity + ",\"level\":\"error\","
                + "\"exception\":[{\"value\":\"boom\"}],"
                + Sdk + "}";

            var result = await RunAsync(HelloLine() + "\n" + EventLine(payload) + "\n", TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal(InvalidLine, lines[1]);
            Assert.DoesNotContain("boom", result.Output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task MalformedExceptionPayload_IsSkippedAndFollowingEventIsRendered()
        {
            var malformed = "{" + EventIdentity + ",\"level\":\"error\",\"exception\":{\"type\":\"broken\"}," + Sdk + "}";

            var result = await RunAsync(
                HelloLine() + "\n" + EventLine(malformed) + "\n" + EventLine(Payload) + "\n",
                TerminalHostFormat.Compact);

            var lines = result.Output.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.Equal(InvalidLine, lines[1]);
            Assert.EndsWith("INFO  hello  (id 3b7e0c11)", lines[2], StringComparison.Ordinal);
        }

        private static async Task<(int ExitCode, string Output)> RunAsync(
            string input,
            TerminalHostFormat format,
            string appName = "fallback-game",
            bool color = false)
        {
            using var reader = new StringReader(input);
            using var writer = new StringWriter();
            var exitCode = await TerminalHost.RunAsync(
                reader,
                writer,
                new TerminalHostOptions
                {
                    AppName = appName,
                    Format = format,
                    Color = color,
                },
                CancellationToken.None);

            return (exitCode, writer.ToString().Replace("\r\n", "\n").TrimEnd('\n'));
        }

        private static string HelloLine(string app = "hello-game", int protocol = 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"hello\",\"protocol\":{0},\"parent_pid\":4242,\"app\":\"{1}\"}}",
                protocol,
                app);
        }

        private static string EventLine(string payload)
        {
            return "{\"type\":\"event\",\"payload\":" + payload + "}";
        }

        /// <summary>
        /// Builds an exception payload with <paramref name="count"/> in-app frames,
        /// ordered from the outermost caller (<c>Frame1</c>) to the crash site.
        /// </summary>
        private static string ManyFramesPayload(int count)
        {
            var frames = string.Join(
                ",",
                Enumerable.Range(1, count).Select(index =>
                    "{\"function\":\"Frame" + index.ToString(CultureInfo.InvariantCulture)
                    + "\",\"module\":\"Game\",\"filename\":\"Game.cs\",\"lineno\":"
                    + index.ToString(CultureInfo.InvariantCulture) + ",\"in_app\":true}"));

            return "{" + EventIdentity + ",\"level\":\"error\","
                + "\"exception\":[{\"type\":\"System.Exception\",\"value\":\"many\","
                + "\"stacktrace\":{\"frames\":[" + frames + "]}}],"
                + Sdk + "}";
        }

        private static string LocalCompactTimestamp()
        {
            return PayloadTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static string LocalPrettyTimestamp()
        {
            return PayloadTimestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }
}
