using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace GlitchPort.Terminal.Tests
{
    public sealed class ConsoleOutputTests
    {
        private const string Header =
            "═══ glitch-port terminal ═══ hello-game (pid 43876) · 2026-11-14 17:16:47 · protocol 1";

        [Fact]
        public void Create_WithConsoleSink_WritesHeaderKoreanAndMultilineTextUnchanged()
        {
            var sink = new RecordingSink();
            using var writer = ConsoleOutput.Create(new MemoryStream(), sink);

            writer.WriteLine(Header);
            writer.WriteLine("안녕 hello");
            writer.WriteLine("첫 줄" + Environment.NewLine + "둘째 줄");

            var expected = Header + Environment.NewLine
                + "안녕 hello" + Environment.NewLine
                + "첫 줄" + Environment.NewLine + "둘째 줄" + Environment.NewLine;

            Assert.Equal(expected, sink.Text);
            Assert.Equal(3, sink.Writes.Count);
            Assert.Contains("═══", sink.Text, StringComparison.Ordinal);
            Assert.Contains('·', sink.Text);
            Assert.Contains("안녕 hello", sink.Text, StringComparison.Ordinal);
            Assert.Contains("첫 줄", sink.Text, StringComparison.Ordinal);
            Assert.Contains("둘째 줄", sink.Text, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', sink.Text);
            Assert.DoesNotContain('?', sink.Text);
        }

        [Fact]
        public void Create_WithoutConsoleSink_WritesUtf8WithoutBom()
        {
            using var stream = new MemoryStream();
            using var writer = ConsoleOutput.Create(stream, consoleSink: null);

            writer.Write("═");

            var bytes = stream.ToArray();
            Assert.Equal(new byte[] { 0xE2, 0x95, 0x90 }, bytes);
            Assert.False(HasUtf8Bom(bytes));
            Assert.Equal("═", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes));
        }

        [Fact]
        public void Create_WithConsoleSink_LeavesRedirectedStreamUntouched()
        {
            using var stream = new MemoryStream();
            var sink = new RecordingSink();
            using var writer = ConsoleOutput.Create(stream, sink);

            writer.Write("console");

            Assert.Equal(0, stream.Length);
            Assert.Equal(Encoding.Unicode, writer.Encoding);
            Assert.Equal("console", sink.Text);
            Assert.Single(sink.Writes);
        }

        [Fact]
        public void Create_WithConsoleSink_DoesNotRequireRedirectedStream()
        {
            var sink = new RecordingSink();
            using var writer = ConsoleOutput.Create(null!, sink);

            writer.Write("immediate");

            Assert.Equal("immediate", sink.Text);
        }

        [Fact]
        public void ConsoleSinkWriter_ForwardsCharStringCharArrayAndLineWrites()
        {
            var sink = new RecordingSink();
            using var writer = new ConsoleSinkWriter(sink);

            Assert.Equal(Environment.NewLine, writer.NewLine);

            writer.Write('A');
            writer.Write("BC");
            writer.Write((string?)null);

            var buffer = new[] { 'x', 'D', 'E', 'F', 'y' };
            writer.Write(buffer, 1, 3);
            writer.Write(buffer, 1, 0);

            writer.WriteLine("G");
            writer.WriteLine();

            Assert.Equal(
                new[] { "A", "BC", "DEF", "G" + Environment.NewLine, Environment.NewLine },
                sink.Writes);

            writer.NewLine = "|";
            writer.WriteLine("H");

            Assert.Equal("H|", sink.Writes[^1]);
        }

        [Fact]
        public void ConsoleSinkWriter_WriteCharArray_ValidatesLikeTextWriter()
        {
            var sink = new RecordingSink();
            using var writer = new ConsoleSinkWriter(sink);
            var buffer = new char[3];

            Assert.Throws<ArgumentNullException>(() => new ConsoleSinkWriter(null!));
            Assert.Throws<ArgumentNullException>(() => writer.Write((char[]?)null!, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Write(buffer, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Write(buffer, 0, -1));
            Assert.Throws<ArgumentException>(() => writer.Write(buffer, 2, 2));
            Assert.Empty(sink.Writes);
        }

        [Fact]
        public void Win32ConsoleSink_InvalidHandle_WriteThrowsIOException()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var sink = new Win32ConsoleSink(IntPtr.Zero);

            var exception = Assert.Throws<IOException>(() => sink.Write("x"));
            Assert.Contains("Win32 error", exception.Message, StringComparison.Ordinal);
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private sealed class RecordingSink : IConsoleSink
        {
            public List<string> Writes { get; } = new();

            public string Text => string.Concat(Writes);

            public void Write(string text) => Writes.Add(text);
        }
    }
}
