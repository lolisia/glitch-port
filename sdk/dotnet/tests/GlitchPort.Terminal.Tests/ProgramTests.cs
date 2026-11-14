using System;
using System.IO;
using System.Text;
using Xunit;

namespace GlitchPort.Terminal.Tests
{
    public sealed class ProgramTests
    {
        private const string KoreanText = "\ud55c\uae00 \uc624\ub958: \uc5f0\uacb0 \uc2e4\ud328 \ud83c\udfae";

        [Fact]
        public void CreateOutput_WritesUtf8WithoutBom()
        {
            using var stream = new MemoryStream();
            using var writer = Program.CreateOutput(stream);

            writer.Write(KoreanText);
            writer.Flush();

            var bytes = stream.ToArray();
            Assert.False(HasUtf8Bom(bytes));
            Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(KoreanText), bytes);
            Assert.Equal(KoreanText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes));
        }

        [Fact]
        public void CreateInput_ReadsUtf8WithoutBom()
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(KoreanText + "\nsecond\n");
            using var stream = new MemoryStream(bytes);
            using var reader = Program.CreateInput(stream);

            Assert.Equal(KoreanText, reader.ReadLine());
            Assert.Equal("second", reader.ReadLine());
            Assert.Null(reader.ReadLine());
        }

        [Fact]
        public void TryParseArguments_AllOptions_ParsesValues()
        {
            var parsed = Program.TryParseArguments(
                new[] { "--parent-pid", "123", "--format", "compact", "--window", "new", "--app", "My Game" },
                out var options,
                out var window,
                out var error);

            Assert.True(parsed);
            Assert.Equal(string.Empty, error);
            Assert.True(options.ParentProcessId.HasValue);
            Assert.Equal(123, options.ParentProcessId.Value);
            Assert.Equal(TerminalHostFormat.Compact, options.Format);
            Assert.Equal("My Game", options.AppName);
            Assert.Equal(TerminalWindowMode.New, window);
        }

        [Fact]
        public void TryParseArguments_Defaults_ArePrettyInheritAndNoParent()
        {
            var parsed = Program.TryParseArguments(Array.Empty<string>(), out var options, out var window, out _);

            Assert.True(parsed);
            Assert.Equal(string.Empty, options.AppName);
            Assert.Equal(TerminalHostFormat.Pretty, options.Format);
            Assert.Null(options.ParentProcessId);
            Assert.Equal(TerminalWindowMode.Inherit, window);
        }

        [Fact]
        public void TryParseArguments_UnknownArgument_ReturnsError()
        {
            Assert.False(Program.TryParseArguments(new[] { "--nope" }, out _, out _, out var error));
            Assert.Contains("--nope", error);
        }

        [Fact]
        public void TryParseArguments_MissingValue_ReturnsError()
        {
            Assert.False(Program.TryParseArguments(new[] { "--format" }, out _, out _, out var error));
            Assert.Contains("--format", error);
        }

        [Fact]
        public void TryParseArguments_InvalidFormatValue_ReturnsError()
        {
            Assert.False(Program.TryParseArguments(new[] { "--format", "xml" }, out _, out _, out var error));
            Assert.Contains("--format", error);
        }

        [Fact]
        public void TryParseArguments_InvalidParentPid_ReturnsError()
        {
            Assert.False(Program.TryParseArguments(new[] { "--parent-pid", "not-a-pid" }, out _, out _, out var error));
            Assert.Contains("--parent-pid", error);
        }

        [Fact]
        public void ResolveWindowMode_NewOnUnsupportedPlatform_WarnsExactlyOnceAndFallsBackToInherit()
        {
            using var error = new StringWriter();

            var resolved = Program.ResolveWindowMode(TerminalWindowMode.New, error, isWindows: false);

            Assert.Equal(TerminalWindowMode.Inherit, resolved);

            var lines = error.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Single(lines);
            Assert.Equal(Program.UnsupportedWindowWarning, lines[0]);
            Assert.Contains("--window inherit", lines[0]);
        }

        [Fact]
        public void ResolveWindowMode_NewOnWindows_KeepsNewWithoutWarning()
        {
            using var error = new StringWriter();

            var resolved = Program.ResolveWindowMode(TerminalWindowMode.New, error, isWindows: true);

            Assert.Equal(TerminalWindowMode.New, resolved);
            Assert.Equal(string.Empty, error.ToString());
        }

        [Fact]
        public void ResolveWindowMode_Inherit_DoesNotWarn()
        {
            using var error = new StringWriter();

            var resolved = Program.ResolveWindowMode(TerminalWindowMode.Inherit, error, isWindows: false);

            Assert.Equal(TerminalWindowMode.Inherit, resolved);
            Assert.Equal(string.Empty, error.ToString());
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }
    }
}
