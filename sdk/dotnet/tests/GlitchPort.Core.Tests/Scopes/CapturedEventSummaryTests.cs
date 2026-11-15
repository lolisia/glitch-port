using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests.Scopes
{
    public sealed class CapturedEventSummaryTests
    {
        [Fact]
        public void ThreeArgumentExceptionSummary_CopiesTypeValueAndFrames()
        {
            var frames = new[]
            {
                new CapturedStackFrame("Game", "Update", "Update.cs", 12),
            };

            var summary = new CapturedExceptionSummary("System.InvalidOperationException", "boom", frames);

            Assert.Equal("System.InvalidOperationException", summary.Type);
            Assert.Equal("boom", summary.Value);
            Assert.Equal("Update", Assert.Single(summary.Frames).Function);
        }

        [Fact]
        public void SevenArgumentConstructor_LeavesTheSummaryFieldsEmpty()
        {
            var captured = new CapturedEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Level.Warning,
                "hello",
                "glitchport.dotnet",
                "0.1.0",
                "{}");

            Assert.Empty(captured.Exceptions);
            Assert.Null(captured.Exception);
            Assert.Empty(captured.Tags);
            Assert.Null(captured.Release);
            Assert.Null(captured.Environment);
        }

        [Fact]
        public void SummaryConstructor_ExposesExceptionsInnerToOuterWithTheOutermostConvenienceProperty()
        {
            var inner = new CapturedExceptionSummary("System.InvalidOperationException", "inner", null);
            var outer = new CapturedExceptionSummary("System.ApplicationException", "outer", null);
            var tags = new Dictionary<string, string> { ["scene"] = "Stage1" };

            var captured = new CapturedEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Level.Error,
                "outer: message",
                "glitchport.dotnet",
                "0.1.0",
                "{}",
                new[] { inner, outer },
                tags,
                "star-raiders@1.2.3",
                "production");

            Assert.Equal(new[] { inner, outer }, captured.Exceptions);
            Assert.Same(outer, captured.Exception);
            Assert.Equal("Stage1", captured.Tags["scene"]);
            Assert.Equal("star-raiders@1.2.3", captured.Release);
            Assert.Equal("production", captured.Environment);
        }

        [Fact]
        public async Task CaptureExceptionAsync_SummariesAreInnerToOuterAndTheConveniencePropertyIsTheOutermost()
        {
            using var client = CapturingClient.Create(out var relay);

            var exception = CreateNestedException();
            await client.CaptureExceptionAsync(exception);

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal(2, captured.Exceptions.Count);
            Assert.Equal("System.InvalidOperationException", captured.Exceptions[0].Type);
            Assert.Equal("inner", captured.Exceptions[0].Value);
            Assert.Equal("System.ApplicationException", captured.Exceptions[1].Type);
            Assert.Equal("outer", captured.Exceptions[1].Value);
            Assert.Same(captured.Exceptions[1], captured.Exception);
            Assert.Equal("System.ApplicationException: outer", captured.Message);
        }

        [Fact]
        public async Task CaptureExceptionAsync_UsesAtMostFiveInAppFramesFromTheThrowSiteBackwards()
        {
            using var client = CapturingClient.Create(out var relay);

            var frames = new List<StackFrameInfo>
            {
                new StackFrameInfo { Module = "System", Function = "system-outer", InApp = false },
                new StackFrameInfo { Module = "Game", Function = "outer-app", InApp = true },
                new StackFrameInfo { Module = "Microsoft", Function = "system-middle", InApp = false },
                new StackFrameInfo { Module = "Game", Function = "app-caller-2", InApp = true },
                new StackFrameInfo { Module = "Game", Function = "app-caller-1", InApp = true },
                new StackFrameInfo { Module = "Game", Function = "app-caller-0", InApp = true },
                new StackFrameInfo { Module = "Game", Function = "app-throw-site", InApp = true },
            };

            var e = new GlitchPortEvent
            {
                Level = Level.Error,
                Exceptions =
                {
                    new ExceptionInfo
                    {
                        Type = "System.InvalidOperationException",
                        Value = "boom",
                        Frames = frames,
                    },
                },
            };

            await client.CaptureEventAsync(e);

            var captured = Assert.Single(relay.Sink.Events);
            var summary = Assert.Single(captured.Exceptions);
            Assert.Equal(5, summary.Frames.Count);
            Assert.Equal(
                new[] { "app-throw-site", "app-caller-0", "app-caller-1", "app-caller-2", "outer-app" },
                summary.Frames.Select(frame => frame.Function));
            Assert.All(summary.Frames, frame => Assert.Equal("Game", frame.Module));
        }

        private static Exception CreateNestedException()
        {
            try
            {
                try
                {
                    throw new InvalidOperationException("inner");
                }
                catch (Exception inner)
                {
                    throw new ApplicationException("outer", inner);
                }
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
