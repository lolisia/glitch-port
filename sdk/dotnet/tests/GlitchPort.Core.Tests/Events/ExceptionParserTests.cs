using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests.Events
{
    public sealed class ExceptionParserTests
    {
        [Fact]
        public void Parse_OrdersExceptionsFromInnermostToOutermost()
        {
            var parsed = ExceptionParser.Parse(CaptureThrownNested(), new GlitchPortOptions());

            Assert.Equal(2, parsed.Count);
            Assert.Equal(typeof(ArgumentException).FullName, parsed[0].Type);
            Assert.Equal("inner", parsed[0].Value);
            Assert.Equal(typeof(ArgumentException).Namespace, parsed[0].Module);
            Assert.Equal(typeof(ApplicationException).FullName, parsed[1].Type);
            Assert.Equal("outer", parsed[1].Value);
            Assert.Equal(typeof(ApplicationException).Namespace, parsed[1].Module);
        }

        [Fact]
        public void Parse_OrdersFramesWithTheOutermostCallerFirstAndTheCrashSiteLast()
        {
            // The catch is in the test method, so the captured trace ends here:
            // the test method is the outermost caller and ThrowOuter the crash site.
            Exception exception;
            try
            {
                ThrowOuter();
                exception = new InvalidOperationException("The helper did not throw.");
            }
            catch (Exception caught)
            {
                exception = caught;
            }

            var parsed = ExceptionParser.Parse(exception, new GlitchPortOptions());
            var outer = parsed[parsed.Count - 1];

            Assert.NotEmpty(outer.Frames);
            Assert.Equal(
                nameof(Parse_OrdersFramesWithTheOutermostCallerFirstAndTheCrashSiteLast),
                outer.Frames[0].Function);
            Assert.Equal(nameof(ThrowOuter), outer.Frames[outer.Frames.Count - 1].Function);

            var testFrameIndex = IndexOfFunction(
                outer.Frames,
                nameof(Parse_OrdersFramesWithTheOutermostCallerFirstAndTheCrashSiteLast));
            var throwSiteIndex = IndexOfFunction(outer.Frames, nameof(ThrowOuter));

            Assert.True(testFrameIndex >= 0, "the test method frame was not found in the stack trace");
            Assert.True(
                testFrameIndex < throwSiteIndex,
                "the outermost caller must come before the throw site in the frame list");
        }

        [Fact]
        public void Parse_IncludesFileLineAndColumnInformationWhenAvailable()
        {
            var parsed = ExceptionParser.Parse(CaptureThrownNested(), new GlitchPortOptions());
            var inner = parsed[0];

            var frame = SingleFunction(inner.Frames, nameof(ThrowInner));

            Assert.Equal("ExceptionParserTests.cs", frame.FileName);
            Assert.True(frame.LineNumber.HasValue && frame.LineNumber.Value > 0);
            Assert.True(frame.ColumnNumber.HasValue && frame.ColumnNumber.Value > 0);

            if (frame.AbsPath is not null)
            {
                Assert.EndsWith("ExceptionParserTests.cs", frame.AbsPath, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Parse_FlattensAggregateExceptionInnerExceptions()
        {
            var aggregate = new AggregateException(
                new InvalidOperationException("one"),
                new ArgumentException("two"));

            var parsed = ExceptionParser.Parse(aggregate, new GlitchPortOptions());

            Assert.Equal(2, parsed.Count);
            Assert.Equal(typeof(InvalidOperationException).FullName, parsed[0].Type);
            Assert.Equal("one", parsed[0].Value);
            Assert.Equal(typeof(ArgumentException).FullName, parsed[1].Type);
            Assert.Equal("two", parsed[1].Value);
        }

        [Fact]
        public void Parse_FlattensInnerChainsInsideAggregateExceptions()
        {
            var nested = new ApplicationException("outer", new InvalidOperationException("inner"));

            var parsed = ExceptionParser.Parse(new AggregateException(nested), new GlitchPortOptions());

            Assert.Equal(2, parsed.Count);
            Assert.Equal("inner", parsed[0].Value);
            Assert.Equal("outer", parsed[1].Value);
        }

        [Fact]
        public void Parse_FlattensNestedAggregateExceptions()
        {
            var aggregate = new AggregateException(
                new AggregateException(new InvalidOperationException("a"), new ArgumentException("b")),
                new FormatException("c"));

            var parsed = ExceptionParser.Parse(aggregate, new GlitchPortOptions());

            Assert.Equal(3, parsed.Count);
            Assert.Equal("a", parsed[0].Value);
            Assert.Equal("b", parsed[1].Value);
            Assert.Equal(typeof(FormatException).FullName, parsed[2].Type);
        }

        [Fact]
        public void Parse_KeepsAtMostTenAggregateInnerExceptions()
        {
            var inners = new List<Exception>();
            for (var i = 0; i < 12; i++)
            {
                inners.Add(new InvalidOperationException("e" + i));
            }

            var parsed = ExceptionParser.Parse(new AggregateException(inners), new GlitchPortOptions());

            Assert.Equal(10, parsed.Count);
            Assert.Equal("e0", parsed[0].Value);
            Assert.Equal("e9", parsed[9].Value);
        }

        [Fact]
        public void Parse_KeepsAnAggregateExceptionWithoutInnerExceptions()
        {
            var parsed = ExceptionParser.Parse(new AggregateException(), new GlitchPortOptions());

            var exception = Assert.Single(parsed);
            Assert.Equal(typeof(AggregateException).FullName, exception.Type);
        }

        [Fact]
        public async Task Parse_CleansAsyncStateMachineModuleNames()
        {
            var exception = await CaptureAsyncException();

            var parsed = ExceptionParser.Parse(exception, new GlitchPortOptions());
            var frames = parsed[0].Frames;

            Assert.Contains(
                frames,
                frame => frame.Module is not null
                    && frame.Module.EndsWith("ExceptionParserTests.ThrowFromAsync", StringComparison.Ordinal));

            Assert.DoesNotContain(
                frames,
                frame => frame.Module is not null && frame.Module.Contains('<', StringComparison.Ordinal));
        }

        [Fact]
        public void Parse_CleansGenericTypeNames()
        {
            var exception = Capture(() => GenericThrower<int>.Throw());

            var parsed = ExceptionParser.Parse(exception, new GlitchPortOptions());
            var module = SingleFunction(parsed[0].Frames, nameof(GenericThrower<int>.Throw)).Module;

            Assert.Equal("GlitchPort.Tests.Events.ExceptionParserTests.GenericThrower", module);
            Assert.DoesNotContain("`", module, StringComparison.Ordinal);
            Assert.DoesNotContain("[[", module, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_MarksApplicationFramesAsInAppByDefault()
        {
            var parsed = ExceptionParser.Parse(Capture(() => TestGame.GameCode.Throw()), new GlitchPortOptions());

            Assert.True(SingleFunction(parsed[0].Frames, "Throw").InApp);
        }

        [Fact]
        public void Parse_MarksFrameworkFramesAsNotInAppByDefault()
        {
            var exception = Capture(() => int.Parse("not a number", CultureInfo.InvariantCulture));

            var parsed = ExceptionParser.Parse(exception, new GlitchPortOptions());
            var systemFrames = FilterByModulePrefix(parsed[0].Frames, "System.");

            Assert.NotEmpty(systemFrames);
            Assert.All(systemFrames, frame => Assert.False(frame.InApp));

            var sdkFrames = FilterByModulePrefix(parsed[0].Frames, "GlitchPort.");
            Assert.NotEmpty(sdkFrames);
            Assert.All(sdkFrames, frame => Assert.False(frame.InApp));
        }

        [Fact]
        public void Parse_InAppIncludePrefersOnlyMatchingModules()
        {
            var options = new GlitchPortOptions();
            options.InAppInclude.Add("TestGame.");

            var parsed = ExceptionParser.Parse(Capture(() => TestGame.GameCode.Throw()), options);

            Assert.True(SingleFunction(parsed[0].Frames, "Throw").InApp);

            var sdkFrames = FilterByModulePrefix(parsed[0].Frames, "GlitchPort.");
            Assert.NotEmpty(sdkFrames);
            Assert.All(sdkFrames, frame => Assert.False(frame.InApp));
        }

        [Fact]
        public void Parse_InAppIncludeThatMatchesNothingMarksEveryFrameAsNotInApp()
        {
            var options = new GlitchPortOptions();
            options.InAppInclude.Add("Contoso.");

            var parsed = ExceptionParser.Parse(Capture(() => TestGame.GameCode.Throw()), options);

            Assert.All(parsed[0].Frames, frame => Assert.False(frame.InApp));
        }

        [Fact]
        public void Parse_InAppExcludeCanExcludeAnApplicationPrefix()
        {
            var options = new GlitchPortOptions();
            options.InAppExclude.Add("TestGame.");

            var parsed = ExceptionParser.Parse(Capture(() => TestGame.GameCode.Throw()), options);
            var gameFrames = FilterByModulePrefix(parsed[0].Frames, "TestGame.");

            Assert.NotEmpty(gameFrames);
            Assert.All(gameFrames, frame => Assert.False(frame.InApp));
        }

        [Fact]
        public void Parse_ClearingInAppExcludeMarksEveryFrameAsInApp()
        {
            var options = new GlitchPortOptions();
            options.InAppExclude.Clear();

            var exception = Capture(() => int.Parse("not a number", CultureInfo.InvariantCulture));
            var parsed = ExceptionParser.Parse(exception, options);

            Assert.All(parsed[0].Frames, frame => Assert.True(frame.InApp));
        }

        [Fact]
        public void Parse_RejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(
                () => ExceptionParser.Parse(null!, new GlitchPortOptions()));
            Assert.Throws<ArgumentNullException>(
                () => ExceptionParser.Parse(new InvalidOperationException(), null!));
        }

        private static Exception CaptureThrownNested()
        {
            try
            {
                ThrowOuter();
                return new InvalidOperationException("The nested helper did not throw.");
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ThrowOuter()
        {
            try
            {
                ThrowInner();
            }
            catch (ArgumentException inner)
            {
                throw new ApplicationException("outer", inner);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ThrowInner()
        {
            throw new ArgumentException("inner");
        }

        private static Exception Capture(Action action)
        {
            try
            {
                action();
                return new InvalidOperationException("The action did not throw.");
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static async Task<Exception> CaptureAsyncException()
        {
            try
            {
                await ThrowFromAsync();
                return new InvalidOperationException("The async helper did not throw.");
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static async Task ThrowFromAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        }

        private static StackFrameInfo SingleFunction(IList<StackFrameInfo> frames, string function)
        {
            foreach (var frame in frames)
            {
                if (frame.Function == function)
                {
                    return frame;
                }
            }

            throw new Xunit.Sdk.XunitException($"No frame with function '{function}' was found.");
        }

        private static int IndexOfFunction(IList<StackFrameInfo> frames, string function)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i].Function == function)
                {
                    return i;
                }
            }

            return -1;
        }

        private static List<StackFrameInfo> FilterByModulePrefix(
            IList<StackFrameInfo> frames,
            string prefix)
        {
            var result = new List<StackFrameInfo>();
            foreach (var frame in frames)
            {
                if (frame.Module is not null
                    && frame.Module.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result.Add(frame);
                }
            }

            return result;
        }

        private static class GenericThrower<T>
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static void Throw()
            {
                throw new InvalidOperationException("generic");
            }
        }
    }
}

namespace TestGame
{
    internal static class GameCode
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Throw()
        {
            throw new InvalidOperationException("game boom");
        }
    }
}
