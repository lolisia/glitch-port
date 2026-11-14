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
    }
}
