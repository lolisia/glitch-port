using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class EventFormatterTests
    {
        private static readonly Guid EventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");

        private const string SdkName = "glitchport.dotnet";
        private const string SdkVersion = "0.1.0";

        [Fact]
        public void FormatCompact_Info_ReturnsExpectedLine()
        {
            var e = CreateEvent();

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal("2026-11-14 15:02:14.010 INFO  hello  (id 3b7e0c11)", result);
        }

        [Fact]
        public void FormatCompact_Warning_UsesFullUpperCaseName()
        {
            var e = CreateEvent(level: Level.Warning);

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal("2026-11-14 15:02:14.010 WARNING hello  (id 3b7e0c11)", result);
        }

        [Fact]
        public void FormatPretty_Info_ReturnsExpectedLines()
        {
            var e = CreateEvent();

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 INFO  hello\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
        }

        [Fact]
        public void FormatHeader_ReturnsExpectedLine()
        {
            var startedAt = LocalTime(2026, 11, 14, 15, 2, 14);

            var result = EventFormatter.FormatHeader("hello-game", 4242, startedAt);

            Assert.Equal(
                "\u2550\u2550\u2550 glitch-port terminal \u2550\u2550\u2550 hello-game (pid 4242) \u00b7 2026-11-14 15:02:14 \u00b7 protocol 1",
                result);
        }

        [Fact]
        public void FormatCompact_MultiLineMessage_IndentsContinuationLines()
        {
            var e = CreateEvent(message: "first\r\nsecond\nthird");

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal(
                "2026-11-14 15:02:14.010 INFO  first\n"
                    + "  second\n"
                    + "  third  (id 3b7e0c11)",
                result);
            Assert.False(result.Contains('\r'));
        }

        [Fact]
        public void FormatPretty_MultiLineMessage_IndentsContinuationLinesAndKeepsFooterLast()
        {
            var e = CreateEvent(message: "first\r\nsecond\nthird");

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 INFO  first\n"
                    + "  second\n"
                    + "  third\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
            Assert.False(result.Contains('\r'));
        }

        [Fact]
        public void FormatCompact_NonAsciiMessage_KeepsTextUnchanged()
        {
            var e = CreateEvent(message: "\ud55c\uae00 \uc624\ub958: \uc5f0\uacb0 \uc2e4\ud328 \ud83c\udfae");

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal(
                "2026-11-14 15:02:14.010 INFO  \ud55c\uae00 \uc624\ub958: \uc5f0\uacb0 \uc2e4\ud328 \ud83c\udfae  (id 3b7e0c11)",
                result);
        }

        [Fact]
        public void FormatPretty_NonAsciiMessage_KeepsTextUnchanged()
        {
            var message = "\ud55c\uae00 \uc624\ub958: \uc5f0\uacb0 \uc2e4\ud328 \ud83c\udfae";
            var e = CreateEvent(message: message);

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 INFO  " + message + "\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
        }

        [Theory]
        [InlineData(Level.Debug)]
        [InlineData(Level.Info)]
        [InlineData(Level.Warning)]
        [InlineData(Level.Error)]
        [InlineData(Level.Fatal)]
        public void Formatting_WithoutColor_ContainsNoEscapeCharacter(Level level)
        {
            var e = CreateEvent(level: level);
            var startedAt = LocalTime(2026, 11, 14, 15, 2, 14);

            Assert.False(EventFormatter.FormatCompact(e, color: false).Contains('\u001b'));
            Assert.False(EventFormatter.FormatPretty(e, color: false).Contains('\u001b'));
            Assert.False(EventFormatter.FormatHeader("hello-game", 4242, startedAt).Contains('\u001b'));
        }

        [Theory]
        [InlineData(Level.Fatal, "2026-11-14 15:02:14.010 \u001b[41mFATAL\u001b[0m hello  (id 3b7e0c11)")]
        [InlineData(Level.Error, "2026-11-14 15:02:14.010 \u001b[31mERROR\u001b[0m hello  (id 3b7e0c11)")]
        [InlineData(Level.Warning, "2026-11-14 15:02:14.010 \u001b[33mWARNING\u001b[0m hello  (id 3b7e0c11)")]
        [InlineData(Level.Info, "2026-11-14 15:02:14.010 \u001b[36mINFO \u001b[0m hello  (id 3b7e0c11)")]
        [InlineData(Level.Debug, "2026-11-14 15:02:14.010 \u001b[90mDEBUG\u001b[0m hello  (id 3b7e0c11)")]
        public void FormatCompact_WithColor_WrapsOnlyLevelToken(Level level, string expected)
        {
            var e = CreateEvent(level: level);

            var result = EventFormatter.FormatCompact(e, color: true);

            Assert.Equal(expected, result);
            Assert.Equal(2, CountEscapeCharacters(result));
        }

        [Fact]
        public void FormatPretty_WithColor_WrapsOnlyLevelToken()
        {
            var e = CreateEvent(level: Level.Error);

            var result = EventFormatter.FormatPretty(e, color: true);

            Assert.Equal(
                "15:02:14.010 \u001b[31mERROR\u001b[0m hello\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
            Assert.Equal(2, CountEscapeCharacters(result));
        }

        [Fact]
        public void Formatting_NeverEndsWithNewline()
        {
            var e = CreateEvent(message: "first\r\nsecond");
            var startedAt = LocalTime(2026, 11, 14, 15, 2, 14);

            var outputs = new[]
            {
                EventFormatter.FormatCompact(e, color: false),
                EventFormatter.FormatCompact(e, color: true),
                EventFormatter.FormatPretty(e, color: false),
                EventFormatter.FormatPretty(e, color: true),
                EventFormatter.FormatHeader("hello-game", 4242, startedAt),
            };

            foreach (var output in outputs)
            {
                Assert.False(output.EndsWith("\n", StringComparison.Ordinal));
                Assert.False(output.EndsWith("\r", StringComparison.Ordinal));
            }
        }

        private static CapturedEvent CreateEvent(
            string message = "hello",
            Level level = Level.Info)
        {
            return new CapturedEvent(
                EventId,
                LocalTime(2026, 11, 14, 15, 2, 14, 10),
                level,
                message,
                SdkName,
                SdkVersion,
                "{}");
        }

        /// <summary>
        /// Creates a <see cref="DateTimeOffset"/> whose local wall-clock time is the
        /// requested value, so the formatter's local-time output is timezone independent.
        /// </summary>
        private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute, int second, int millisecond = 0)
        {
            return new DateTimeOffset(new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Local));
        }

        private static int CountEscapeCharacters(string value)
        {
            var count = 0;

            foreach (var character in value)
            {
                if (character == '\u001b')
                {
                    count++;
                }
            }

            return count;
        }
    }
}
