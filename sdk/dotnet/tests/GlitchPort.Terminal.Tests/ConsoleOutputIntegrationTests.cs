using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Terminal.Tests
{
    /// <summary>
    /// End-to-end tests that run <see cref="TerminalHost"/> against the
    /// console-sink output path, so the rendered text is captured as the
    /// characters a Windows console would receive instead of as encoded bytes.
    /// </summary>
    public sealed class ConsoleOutputIntegrationTests
    {
        private const string HeaderPrefix = "\u2550\u2550\u2550 glitch-port terminal \u2550\u2550\u2550";

        private const string HelloLine =
            "{\"type\":\"hello\",\"protocol\":1,\"parent_pid\":4242,\"app\":\"hello-game\"}";

        [Fact]
        public async Task HelloAndKoreanEvent_WithConsoleSink_RendersWithoutReplacementCharacters()
        {
            const string payload =
                "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\",\"level\":\"info\",\"message\":\"안녕 hello\",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}";

            var sink = new RecordingSink();

            using var reader = new StringReader(HelloLine + "\n" + EventLine(payload) + "\n");
            using var output = ConsoleOutput.Create(Stream.Null, sink);

            var exitCode = await TerminalHost.RunAsync(
                reader,
                output,
                new TerminalHostOptions
                {
                    AppName = "hello-game",
                    Format = TerminalHostFormat.Compact,
                    Color = false,
                },
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains(HeaderPrefix, sink.Text, StringComparison.Ordinal);
            Assert.Contains('·', sink.Text);
            Assert.Contains("INFO", sink.Text, StringComparison.Ordinal);
            Assert.Contains("안녕 hello", sink.Text, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', sink.Text);
            Assert.DoesNotContain('?', sink.Text);
        }

        [Fact]
        public async Task MultiLineKoreanEvent_WithConsoleSink_IndentsContinuationLineAndKeepsTextIntact()
        {
            // The newline is escaped inside the JSON payload, exactly as the relay
            // writes it, so the host parses a real multi-line message.
            const string payload =
                "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\",\"level\":\"info\",\"message\":\"첫 줄\\n둘째 줄\",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}";

            var sink = new RecordingSink();

            using var reader = new StringReader(HelloLine + "\n" + EventLine(payload) + "\n");
            using var output = ConsoleOutput.Create(Stream.Null, sink);

            var exitCode = await TerminalHost.RunAsync(
                reader,
                output,
                new TerminalHostOptions
                {
                    AppName = "hello-game",
                    Format = TerminalHostFormat.Compact,
                    Color = false,
                },
                CancellationToken.None);

            // Line writes use Environment.NewLine while the formatter's message
            // continuation uses a bare LF; normalise the record separators so the
            // assertion is platform independent.
            var rendered = sink.Text.Replace("\r\n", "\n");

            Assert.Equal(0, exitCode);
            Assert.Contains("첫 줄\n  둘째 줄", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', rendered);
            Assert.DoesNotContain('?', rendered);
        }

        private static string EventLine(string payload)
        {
            return "{\"type\":\"event\",\"payload\":" + payload + "}";
        }

        /// <summary>
        /// Captures every write so the raw rendered characters can be asserted
        /// without an encoding round trip.
        /// </summary>
        private sealed class RecordingSink : IConsoleSink
        {
            public List<string> Writes { get; } = new();

            public string Text => string.Concat(Writes);

            public void Write(string text) => Writes.Add(text);
        }
    }
}
