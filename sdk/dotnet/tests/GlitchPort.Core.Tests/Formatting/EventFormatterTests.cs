using System;
using System.Collections.Generic;
using System.Linq;
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

        [Fact]
        public void FormatCompact_Exception_UsesTypeAndValueOnOneLine()
        {
            var e = CreateEvent(
                message: "the stored message is not shown",
                level: Level.Error,
                exception: CreateException());

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal(
                "2026-11-14 15:02:14.010 ERROR System.InvalidOperationException: boom  (id 3b7e0c11)",
                result);
        }

        [Fact]
        public void FormatCompact_ExceptionWithFramesTagsAndMetadata_StaysOneLine()
        {
            var e = CreateEvent(
                level: Level.Error,
                exception: CreateException(frames: new[] { Frame("Game", "Update", "Update.cs", 12) }),
                tags: new Dictionary<string, string> { ["scene"] = "Stage1" },
                release: "star-raiders@1.2.3",
                environment: "production");

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal(
                "2026-11-14 15:02:14.010 ERROR System.InvalidOperationException: boom  (id 3b7e0c11)",
                result);
            Assert.DoesNotContain("\n", result);
            Assert.DoesNotContain("at Game", result);
            Assert.DoesNotContain("tags:", result);
            Assert.DoesNotContain("release:", result);
            Assert.DoesNotContain("environment:", result);
        }

        [Fact]
        public void FormatCompact_ExceptionWithoutValue_PrintsOnlyTheType()
        {
            var e = CreateEvent(
                level: Level.Error,
                exception: new CapturedExceptionSummary("System.InvalidOperationException", "", null));

            var result = EventFormatter.FormatCompact(e, color: false);

            Assert.Equal(
                "2026-11-14 15:02:14.010 ERROR System.InvalidOperationException  (id 3b7e0c11)",
                result);
        }

        [Fact]
        public void FormatPretty_Exception_PrintsFramesTagsMetadataAndFooterLast()
        {
            var e = CreateEvent(
                level: Level.Error,
                exception: CreateException(frames: new[]
                {
                    Frame("Game", "Update", "Update.cs", 12),
                    Frame("Game", "Start", "Start.cs", 3),
                }),
                tags: new Dictionary<string, string>
                {
                    ["z"] = "last",
                    ["a"] = "first",
                },
                release: "star-raiders@1.2.3",
                environment: "production");

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 ERROR System.InvalidOperationException: boom\n"
                    + "  at Game.Update (Update.cs:12)\n"
                    + "  at Game.Start (Start.cs:3)\n"
                    + "  tags: a=first, z=last\n"
                    + "  release: star-raiders@1.2.3\n"
                    + "  environment: production\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
        }

        [Fact]
        public void FormatPretty_Exception_PrintsAtMostFiveFramesFromTheThrowSite()
        {
            var frames = Enumerable.Range(0, 7)
                .Select(index => Frame("Game", "Frame" + index, "Game.cs", index + 1))
                .ToArray();
            var e = CreateEvent(level: Level.Error, exception: CreateException(frames: frames));

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 ERROR System.InvalidOperationException: boom\n"
                    + "  at Game.Frame0 (Game.cs:1)\n"
                    + "  at Game.Frame1 (Game.cs:2)\n"
                    + "  at Game.Frame2 (Game.cs:3)\n"
                    + "  at Game.Frame3 (Game.cs:4)\n"
                    + "  at Game.Frame4 (Game.cs:5)\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
            Assert.DoesNotContain("Frame5", result, StringComparison.Ordinal);
            Assert.DoesNotContain("Frame6", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FormatPretty_UnrenderableLeadingFrames_DoNotLetAFrameBeyondTheFifthAppear()
        {
            var frames = new[]
            {
                Frame(null, null, null, null),
                Frame(null, null, null, null),
                Frame(null, null, null, null),
                Frame(null, null, null, null),
                Frame(null, null, null, null),
                Frame("Game", "Sixth", "Game.cs", 6),
            };
            var e = CreateEvent(level: Level.Error, exception: CreateException(frames: frames));

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 ERROR System.InvalidOperationException: boom\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
            Assert.DoesNotContain("Sixth", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FormatPretty_FramePartsThatAreAbsent_AreOmitted()
        {
            var e = CreateEvent(
                level: Level.Error,
                exception: CreateException(frames: new[]
                {
                    Frame(null, "Bare", null, null),
                    Frame("Game", "Update", "Update.cs", null),
                    Frame(null, "Plain", "Plain.cs", 7),
                    Frame("Game", null, "Module.cs", 2),
                    Frame(null, null, "Orphan.cs", 1),
                }));

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 ERROR System.InvalidOperationException: boom\n"
                    + "  at Bare\n"
                    + "  at Game.Update (Update.cs)\n"
                    + "  at Plain (Plain.cs:7)\n"
                    + "  at Game (Module.cs:2)\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
        }

        [Fact]
        public void FormatPretty_MultipleExceptions_UseTheOutermostSummary()
        {
            var inner = new CapturedExceptionSummary(
                "System.ArgumentException",
                "inner",
                new[] { Frame("Game", "Inner", "Inner.cs", 1) });
            var outer = new CapturedExceptionSummary(
                "System.InvalidOperationException",
                "outer",
                new[] { Frame("Game", "Outer", "Outer.cs", 2) });
            var e = CreateEvent(level: Level.Error, exceptions: new[] { inner, outer });

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 ERROR System.InvalidOperationException: outer\n"
                    + "  at Game.Outer (Outer.cs:2)\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
        }

        [Fact]
        public void FormatPretty_Tags_AreSortedByKeyWithOrdinalOrder()
        {
            var e = CreateEvent(
                exception: CreateException(),
                tags: new Dictionary<string, string>
                {
                    ["b"] = "2",
                    ["a"] = "3",
                    ["A"] = "1",
                });

            var result = EventFormatter.FormatPretty(e, color: false);

            Assert.Contains("\n  tags: A=1, a=3, b=2\n", result, StringComparison.Ordinal);
        }

        [Fact]
        public void FormatPretty_Exception_WithColor_WrapsOnlyTheLevelToken()
        {
            var e = CreateEvent(level: Level.Error, exception: CreateException());

            var result = EventFormatter.FormatPretty(e, color: true);

            Assert.Equal(
                "15:02:14.010 \u001b[31mERROR\u001b[0m System.InvalidOperationException: boom\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                result);
            Assert.Equal(2, CountEscapeCharacters(result));
        }

        [Fact]
        public void MessageOnlyEvent_WithTagsAndMetadata_KeepsTheLegacyOutput()
        {
            // A client configured with release/environment (and scopes that add
            // tags) attaches them to message captures too. The exception block is
            // the only place that renders them, so the message-only output stays
            // byte-for-byte identical to the pre-v1 format.
            var e = CreateEvent(
                tags: new Dictionary<string, string>
                {
                    ["scene"] = "Stage1",
                    ["build"] = "debug",
                },
                release: "star-raiders@1.2.3",
                environment: "production");

            Assert.Equal(
                "2026-11-14 15:02:14.010 INFO  hello  (id 3b7e0c11)",
                EventFormatter.FormatCompact(e, color: false));

            var pretty = EventFormatter.FormatPretty(e, color: false);

            Assert.Equal(
                "15:02:14.010 INFO  hello\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                pretty);
            Assert.DoesNotContain("tags:", pretty, StringComparison.Ordinal);
            Assert.DoesNotContain("release:", pretty, StringComparison.Ordinal);
            Assert.DoesNotContain("environment:", pretty, StringComparison.Ordinal);
        }

        [Fact]
        public void MessageOnlyEvent_WithoutSummaries_KeepsTheLegacyOutput()
        {
            // The default helper passes empty exception, tag and metadata
            // collections, which is exactly the T02 message-only shape.
            var e = CreateEvent();

            Assert.Equal(
                "2026-11-14 15:02:14.010 INFO  hello  (id 3b7e0c11)",
                EventFormatter.FormatCompact(e, color: false));
            Assert.Equal(
                "15:02:14.010 INFO  hello\n"
                    + "  sdk=glitchport.dotnet/0.1.0 \u00b7 id 3b7e0c11",
                EventFormatter.FormatPretty(e, color: false));
        }

        private static CapturedExceptionSummary CreateException(CapturedStackFrame[]? frames = null)
        {
            return new CapturedExceptionSummary(
                "System.InvalidOperationException",
                "boom",
                frames);
        }

        private static CapturedEvent CreateEvent(
            string message = "hello",
            Level level = Level.Info,
            CapturedExceptionSummary? exception = null,
            IReadOnlyList<CapturedExceptionSummary>? exceptions = null,
            IReadOnlyDictionary<string, string>? tags = null,
            string? release = null,
            string? environment = null)
        {
            var summaries = exceptions
                ?? (exception is null
                    ? Array.Empty<CapturedExceptionSummary>()
                    : new[] { exception });

            return new CapturedEvent(
                EventId,
                LocalTime(2026, 11, 14, 15, 2, 14, 10),
                level,
                message,
                SdkName,
                SdkVersion,
                "{}",
                summaries,
                tags,
                release,
                environment);
        }

        private static CapturedStackFrame Frame(string? module, string? function, string? fileName, int? line)
        {
            return new CapturedStackFrame(module, function, fileName, line);
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
