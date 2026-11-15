#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GlitchPort.Unity
{
    /// <summary>
    /// Converts the condition and stack trace Unity attaches to a log message
    /// into the protocol v1 exception list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity 6 (<c>Application.logMessageReceivedThreaded</c>) reports the
    /// innermost exception in the condition and prints one exception segment
    /// per exception: the innermost exception's frames come first and every
    /// <c>Rethrow as &lt;type&gt;: &lt;value&gt;</c> line introduces the next
    /// outer exception. That printed order already is the protocol order
    /// (innermost cause first), so those segments are kept as printed. The
    /// .NET <c>---&gt;</c> notation is the opposite: <c>Exception.ToString</c>
    /// prints the outer exception first and its inner cause below, so a
    /// <c>---&gt;</c> segment is ordered inward and the completed segment list is
    /// sorted from the innermost cause to the outermost observed exception.
    /// </para>
    /// <para>
    /// Inside a segment Unity prints the throw site first and the outermost
    /// caller last, while the protocol wants the outermost caller first, so
    /// every segment's frames are reversed. The frames Unity adds for the log
    /// call itself are removed: <c>Debug.Log*</c> traces start with the wrapper
    /// frame and continue with the callers, while <c>Debug.LogException</c>
    /// traces end the exception frames at <c>UnityEngine.Debug:LogException</c>
    /// and continue with the frames that logged the exception.
    /// </para>
    /// <para>
    /// The parser is a pure function of its arguments: it keeps no shared
    /// state and never calls into the Unity runtime, so it is safe to call from
    /// the <c>Application.logMessageReceivedThreaded</c> callback.
    /// </para>
    /// </remarks>
    public static class UnityStackTraceParser
    {
        private const string DefaultExceptionType = "Error";
        private const string AssetsPathPrefix = "Assets/";
        private const string PackageCachePathMarker = "Library/PackageCache/";
        private const string InnerExceptionSeparator = "--->";
        private const string RethrowSeparator = "Rethrow as ";
        private const string EndOfInnerExceptionMarkerLine = "--- End of inner exception stack trace ---";
        private const string FileMarker = "(at ";
        private const string DebugModule = "UnityEngine.Debug";
        private const string LoggerModule = "UnityEngine.Logger";
        private const string DebugLogHandlerModule = "UnityEngine.DebugLogHandler";
        private const string ConstructorName = "..ctor";
        private const string StaticConstructorName = "..cctor";

        private static readonly string[] EngineModulePrefixes =
        {
            "UnityEngine.",
            "UnityEditor.",
            "Unity.",
            "System.",
            "Mono.",
            "GlitchPort.",
        };

        /// <summary>
        /// Parses the condition and stack trace Unity supplies for a log message.
        /// </summary>
        /// <param name="condition">The message Unity supplied, usually <c>Type: value</c>.</param>
        /// <param name="stackTrace">The stack trace Unity supplied; it may be empty.</param>
        /// <param name="defaultType">
        /// The exception type used when the condition does not start with a
        /// <c>Type:</c> prefix.
        /// </param>
        /// <returns>
        /// The exceptions ordered from the innermost cause (index 0) to the
        /// outermost observed exception (last item). Every exception carries its
        /// frames ordered from the outermost caller (index 0) to the crash site
        /// (last item).
        /// </returns>
        public static IList<ExceptionInfo> Parse(string? condition, string? stackTrace, string defaultType = "Error")
        {
            var header = ParseCondition(condition, defaultType);

            // Unity prints the innermost exception first, so the condition starts
            // at depth 0 and every "Rethrow as" line steps one level outward. A
            // "---> " marker is a .NET inner cause and steps one level inward.
            // "parents" records which segment an inner marker was opened from so
            // that an end-of-inner marker can resume the frames of the enclosing
            // exception.
            var segments = new List<Segment> { new Segment(header.Type, header.Value, 0, true) };
            var parents = new List<int> { -1 };
            var current = 0;

            var lines = SplitLines(stackTrace);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith(EndOfInnerExceptionMarkerLine, StringComparison.Ordinal))
                {
                    if (parents[current] >= 0)
                    {
                        current = parents[current];
                    }

                    continue;
                }

                if (line.StartsWith(RethrowSeparator, StringComparison.Ordinal))
                {
                    AddSegment(segments, parents, ref current, line.Substring(RethrowSeparator.Length), +1);
                    continue;
                }

                if (line.StartsWith(InnerExceptionSeparator, StringComparison.Ordinal))
                {
                    AddSegment(segments, parents, ref current, line.Substring(InnerExceptionSeparator.Length), -1);
                    continue;
                }

                if (TryParseFrame(line, out var frame))
                {
                    segments[current].Frames.Add(frame);
                }
            }

            var ordered = OrderFromInnermost(segments);
            var exceptions = new List<ExceptionInfo>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var segment = ordered[i];
                exceptions.Add(new ExceptionInfo
                {
                    Type = segment.Type,
                    Value = segment.Value,
                    Frames = FinalizeFrames(segment.Frames, segment.IsCondition),
                });
            }

            return exceptions;
        }

        /// <summary>
        /// Decides whether a stack frame belongs to the game's own code rather
        /// than engine, framework or package code.
        /// </summary>
        /// <param name="module">The declaring type of the frame, for example <c>Game.PlayerController</c>.</param>
        /// <param name="fileName">The source file of the frame, when available.</param>
        /// <returns><see langword="true"/> when the frame is application code.</returns>
        public static bool IsInApp(string? module, string? fileName)
        {
            var path = fileName ?? string.Empty;
            if (path.Length > 0 && path.StartsWith(AssetsPathPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            if (path.Length > 0 && path.IndexOf(PackageCachePathMarker, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            var name = module ?? string.Empty;
            for (var i = 0; i < EngineModulePrefixes.Length; i++)
            {
                if (name.StartsWith(EngineModulePrefixes[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return name.Length > 0;
        }

        private static void AddSegment(
            List<Segment> segments,
            List<int> parents,
            ref int current,
            string header,
            int depthDelta)
        {
            var parsed = ParseExceptionHeader(header);
            segments.Add(new Segment(parsed.Type, parsed.Value, segments[current].Depth + depthDelta));
            parents.Add(current);
            current = segments.Count - 1;
        }

        /// <summary>
        /// Returns the segments ordered from the innermost cause to the
        /// outermost exception. Creation order breaks depth ties so that the
        /// result is deterministic for malformed or mixed traces.
        /// </summary>
        private static List<Segment> OrderFromInnermost(List<Segment> segments)
        {
            var ordered = new List<Segment>(segments);
            for (var i = 1; i < ordered.Count; i++)
            {
                var candidate = ordered[i];
                var j = i - 1;
                while (j >= 0 && ordered[j].Depth > candidate.Depth)
                {
                    ordered[j + 1] = ordered[j];
                    j--;
                }

                ordered[j + 1] = candidate;
            }

            return ordered;
        }

        private static (string Type, string? Value) ParseCondition(string? condition, string? defaultType)
        {
            var fallbackType = string.IsNullOrEmpty(defaultType) ? DefaultExceptionType : defaultType!;

            if (string.IsNullOrEmpty(condition))
            {
                return (fallbackType, condition);
            }

            var text = NormalizeNewLines(condition!);
            var newline = text.IndexOf('\n');
            var firstLine = newline < 0 ? text : text.Substring(0, newline);
            var remainingLines = newline < 0 ? null : text.Substring(newline + 1);

            var colon = firstLine.IndexOf(':');
            if (colon < 0)
            {
                return (fallbackType, text);
            }

            var type = firstLine.Substring(0, colon).Trim();
            if (type.Length == 0)
            {
                return (fallbackType, text);
            }

            var value = firstLine.Substring(colon + 1).TrimStart(' ', '\t');
            if (remainingLines != null)
            {
                // Extra condition lines are part of the message, not a new
                // exception: Unity only structures the first line.
                value = value.Length == 0 ? remainingLines : value + "\n" + remainingLines;
            }

            return (type, value.TrimEnd());
        }

        private static (string Type, string? Value) ParseExceptionHeader(string header)
        {
            var colon = header.IndexOf(':');
            if (colon < 0)
            {
                var text = header.Trim();
                return (text.Length > 0 ? text : DefaultExceptionType, null);
            }

            var type = header.Substring(0, colon).Trim();
            var value = StripEndOfInnerExceptionMarker(header.Substring(colon + 1).TrimStart(' ', '\t').TrimEnd());

            return (type.Length > 0 ? type : DefaultExceptionType, value);
        }

        private static string StripEndOfInnerExceptionMarker(string value)
        {
            if (!value.EndsWith(EndOfInnerExceptionMarkerLine, StringComparison.Ordinal))
            {
                return value;
            }

            return value.Substring(0, value.Length - EndOfInnerExceptionMarkerLine.Length).TrimEnd();
        }

        private static bool TryParseFrame(string line, out StackFrameInfo frame)
        {
            frame = null!;

            // A Unity frame is "<declaring type>[.:]<method> (<arguments>)",
            // optionally followed by " (at <file>:<line>)".
            var symbolEnd = line.IndexOf('(');
            if (symbolEnd <= 0)
            {
                return false;
            }

            var symbol = line.Substring(0, symbolEnd).Trim();
            if (symbol.Length == 0)
            {
                return false;
            }

            string? fileName = null;
            int? lineNumber = null;

            var fileMarker = line.LastIndexOf(FileMarker, StringComparison.Ordinal);
            if (fileMarker > 0 && line[line.Length - 1] == ')')
            {
                var fileInfo = line
                    .Substring(fileMarker + FileMarker.Length, line.Length - fileMarker - FileMarker.Length - 1)
                    .Trim();
                ParseFileInfo(fileInfo, out fileName, out lineNumber);
            }

            ParseSymbol(symbol, out var module, out var function);

            frame = new StackFrameInfo
            {
                Function = function,
                Module = module,
                FileName = fileName,
                LineNumber = lineNumber,
                InApp = IsInApp(module, fileName),
            };
            return true;
        }

        private static void ParseFileInfo(string fileInfo, out string? fileName, out int? lineNumber)
        {
            fileName = null;
            lineNumber = null;

            if (fileInfo.Length == 0)
            {
                return;
            }

            var colon = fileInfo.LastIndexOf(':');
            if (colon > 0
                && colon < fileInfo.Length - 1
                && IsAsciiDigits(fileInfo, colon + 1)
                && int.TryParse(
                    fileInfo.Substring(colon + 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedLineNumber))
            {
                fileName = fileInfo.Substring(0, colon);
                lineNumber = parsedLineNumber;
                return;
            }

            fileName = fileInfo;
        }

        private static void ParseSymbol(string symbol, out string? module, out string function)
        {
            if (symbol.EndsWith(ConstructorName, StringComparison.Ordinal))
            {
                module = EmptyToNull(symbol.Substring(0, symbol.Length - ConstructorName.Length));
                function = ".ctor";
                return;
            }

            if (symbol.EndsWith(StaticConstructorName, StringComparison.Ordinal))
            {
                module = EmptyToNull(symbol.Substring(0, symbol.Length - StaticConstructorName.Length));
                function = ".cctor";
                return;
            }

            // Compiled Mono frames separate the declaring type and the method
            // with a colon; IL2CPP and editor frames use a dot.
            var colon = symbol.LastIndexOf(':');
            if (colon > 0 && colon < symbol.Length - 1)
            {
                module = EmptyToNull(symbol.Substring(0, colon).Trim());
                function = symbol.Substring(colon + 1).Trim();
                return;
            }

            var dot = symbol.LastIndexOf('.');
            if (dot > 0 && dot < symbol.Length - 1)
            {
                module = EmptyToNull(symbol.Substring(0, dot).Trim());
                function = symbol.Substring(dot + 1).Trim();
                return;
            }

            module = null;
            function = symbol;
        }

        /// <summary>
        /// Removes the frames that belong to the log call itself and orders the
        /// remaining frames from the outermost caller to the crash site.
        /// </summary>
        /// <remarks>
        /// <c>Debug.Log</c>, <c>Debug.LogWarning</c> and <c>Debug.LogError</c>
        /// print the wrapper first and the callers below it, so the leading
        /// wrapper frames are dropped and the callers are kept.
        /// <c>Debug.LogException</c> prints the exception frames first and then
        /// <c>UnityEngine.Debug:LogException</c> followed by the frames that
        /// logged the exception, so everything from the wrapper onwards is
        /// dropped. Only the segment built from the condition can be a plain
        /// log call: an outer exception that was never thrown starts at the
        /// wrapper as well, and the log call site is not part of it.
        /// </remarks>
        private static IList<StackFrameInfo> FinalizeFrames(List<StackFrameInfo> frames, bool isConditionSegment)
        {
            var wrapperIndex = -1;
            for (var i = 0; i < frames.Count; i++)
            {
                if (IsLogWrapperFrame(frames[i]))
                {
                    wrapperIndex = i;
                    break;
                }
            }

            if (wrapperIndex == 0 && isConditionSegment)
            {
                // The call to the log API is the innermost frame; Logger and
                // DebugLogHandler frames may be chained in front of it.
                var firstCaller = 0;
                while (firstCaller < frames.Count && IsLogWrapperFrame(frames[firstCaller]))
                {
                    firstCaller++;
                }

                frames.RemoveRange(0, firstCaller);
            }
            else if (wrapperIndex >= 0)
            {
                frames.RemoveRange(wrapperIndex, frames.Count - wrapperIndex);
            }

            frames.Reverse();
            return frames;
        }

        private static bool IsLogWrapperFrame(StackFrameInfo frame)
        {
            var module = frame.Module ?? string.Empty;
            if (string.Equals(module, LoggerModule, StringComparison.Ordinal)
                || string.Equals(module, DebugLogHandlerModule, StringComparison.Ordinal))
            {
                return true;
            }

            var function = frame.Function;
            return string.Equals(module, DebugModule, StringComparison.Ordinal)
                && function != null
                && function.StartsWith("Log", StringComparison.Ordinal);
        }

        private static string[] SplitLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            return NormalizeNewLines(text!).Split('\n');
        }

        private static string NormalizeNewLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static bool IsAsciiDigits(string text, int start)
        {
            if (start >= text.Length)
            {
                return false;
            }

            for (var i = start; i < text.Length; i++)
            {
                var character = text[i];
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static string? EmptyToNull(string value)
        {
            return value.Length == 0 ? null : value;
        }

        private sealed class Segment
        {
            public Segment(string type, string? value, int depth, bool isCondition = false)
            {
                Type = type;
                Value = value;
                Depth = depth;
                IsCondition = isCondition;
            }

            public string Type { get; }

            public string? Value { get; }

            /// <summary>
            /// Nesting level relative to the condition; a lower value is a more
            /// deeply nested inner cause.
            /// </summary>
            public int Depth { get; }

            /// <summary>
            /// Gets a value indicating whether this segment was built from the
            /// condition rather than from a stack trace marker. Only that
            /// segment can start with the wrapper of a plain <c>Debug.Log*</c>
            /// call whose call site frames are the reported frames.
            /// </summary>
            public bool IsCondition { get; }

            public List<StackFrameInfo> Frames { get; } = new List<StackFrameInfo>();
        }
    }
}
