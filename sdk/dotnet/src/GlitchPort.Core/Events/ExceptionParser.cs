using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GlitchPort
{
    /// <summary>
    /// Converts .NET exceptions into the protocol v1 exception list.
    /// </summary>
    /// <remarks>
    /// Exceptions are reported inner cause first and outermost last:
    /// <see cref="AggregateException.InnerExceptions"/> and
    /// <see cref="Exception.InnerException"/> chains are flattened into one
    /// linear list. An <see cref="AggregateException"/> with inner exceptions is
    /// replaced by the flattened chains of its inner exceptions; only an
    /// aggregate without inner exceptions is reported itself. Stack frames are
    /// reported the other way around, with the outermost caller first and the
    /// crash site last, matching the protocol schema.
    /// </remarks>
    public static class ExceptionParser
    {
        /// <summary>Maximum number of aggregate inner exceptions considered.</summary>
        private const int MaxAggregateInnerExceptions = 10;

        // "<BoomAsync>d__3" (async methods) and "<BoomAsync>g__Local|0_0"
        // (local functions) are compiler-generated type names; only the method
        // name they wrap is meaningful.
        private static readonly Regex StateMachinePattern = new Regex(
            "<([^<>]+)>[dg]__[0-9A-Za-z_]+",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses <paramref name="exception"/> and its inner exceptions.
        /// </summary>
        /// <param name="exception">The exception observed by the runtime.</param>
        /// <param name="options">The options that drive the <c>in_app</c> rules.</param>
        /// <returns>
        /// The exceptions ordered from the innermost cause (index 0) to the
        /// outermost observed exception (last item), each with its stack frames
        /// ordered from the outermost caller (index 0) to the crash site.
        /// </returns>
        public static IList<ExceptionInfo> Parse(Exception exception, GlitchPortOptions options)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var chain = new List<Exception>();
            Flatten(exception, chain);

            var result = new List<ExceptionInfo>(chain.Count);
            for (var i = 0; i < chain.Count; i++)
            {
                result.Add(CreateExceptionInfo(chain[i], options));
            }

            return result;
        }

        /// <summary>
        /// Removes compiler-generated generic and async state-machine noise from a
        /// type name, so <c>Class+&lt;BoomAsync&gt;d__3</c> becomes
        /// <c>Class.BoomAsync</c>.
        /// </summary>
        /// <param name="fullName">The declaring type name, or <see langword="null"/>.</param>
        /// <returns>The cleaned name, or <see langword="null"/> when the input was empty.</returns>
        internal static string? CleanModuleName(string? fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return fullName;
            }

            var withoutGenerics = RemoveGenericArguments(fullName);
            var withoutStateMachine = StateMachinePattern.Replace(withoutGenerics, "$1");

            return withoutStateMachine.Replace('+', '.');
        }

        private static void Flatten(Exception exception, List<Exception> chain)
        {
            if (exception is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count == 0)
                {
                    chain.Add(aggregate);
                    return;
                }

                var count = Math.Min(aggregate.InnerExceptions.Count, MaxAggregateInnerExceptions);
                for (var i = 0; i < count; i++)
                {
                    Flatten(aggregate.InnerExceptions[i], chain);
                }

                return;
            }

            if (exception.InnerException is { } inner)
            {
                Flatten(inner, chain);
            }

            chain.Add(exception);
        }

        private static ExceptionInfo CreateExceptionInfo(Exception exception, GlitchPortOptions options)
        {
            var type = exception.GetType();

            return new ExceptionInfo
            {
                Type = type.FullName ?? type.Name,
                Value = exception.Message,
                Module = type.Namespace,
                Frames = CreateFrames(exception, options),
            };
        }

        private static IList<StackFrameInfo> CreateFrames(Exception exception, GlitchPortOptions options)
        {
            var trace = new StackTrace(exception, true);
            var frames = new List<StackFrameInfo>(trace.FrameCount);

            // The runtime reports the crash site first; the protocol wants the
            // outermost caller first and the crash site last.
            for (var i = trace.FrameCount - 1; i >= 0; i--)
            {
                var frame = trace.GetFrame(i);
                if (frame is null)
                {
                    continue;
                }

                frames.Add(CreateFrame(frame, options));
            }

            return frames;
        }

        private static StackFrameInfo CreateFrame(StackFrame frame, GlitchPortOptions options)
        {
            var method = frame.GetMethod();
            var module = CleanModuleName(method?.DeclaringType?.FullName);

            var path = frame.GetFileName();
            string? fileName = null;
            string? absPath = null;
            if (!string.IsNullOrEmpty(path))
            {
                fileName = Path.GetFileName(path);
                absPath = Path.IsPathRooted(path) ? path : null;
            }

            var lineNumber = frame.GetFileLineNumber();
            var columnNumber = frame.GetFileColumnNumber();

            return new StackFrameInfo
            {
                Function = method?.Name,
                Module = module,
                FileName = fileName,
                AbsPath = absPath,
                LineNumber = lineNumber > 0 ? lineNumber : null,
                ColumnNumber = columnNumber > 0 ? columnNumber : null,
                InApp = IsInApp(module, options),
            };
        }

        private static bool IsInApp(string? module, GlitchPortOptions options)
        {
            var name = module ?? string.Empty;

            var include = options.InAppInclude;
            if (include is not null && include.Count > 0)
            {
                return StartsWithAny(name, include);
            }

            var exclude = options.InAppExclude;
            return exclude is null || !StartsWithAny(name, exclude);
        }

        private static bool StartsWithAny(string name, IList<string> prefixes)
        {
            for (var i = 0; i < prefixes.Count; i++)
            {
                var prefix = prefixes[i];
                if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes generic arguments and arity markers from a type name, for
        /// example <c>MyType`1[[System.Int32, ...]]</c> becomes <c>MyType</c>.
        /// </summary>
        private static string RemoveGenericArguments(string name)
        {
            var builder = new StringBuilder(name.Length);
            var bracketDepth = 0;

            for (var i = 0; i < name.Length; i++)
            {
                var character = name[i];

                if (character == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (character == ']')
                {
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    continue;
                }

                if (bracketDepth > 0)
                {
                    continue;
                }

                if (character == '`')
                {
                    // Skip the arity digits that follow the backtick.
                    i++;
                    while (i < name.Length && char.IsDigit(name[i]))
                    {
                        i++;
                    }

                    i--;
                    continue;
                }

                builder.Append(character);
            }

            return builder.ToString();
        }
    }
}
