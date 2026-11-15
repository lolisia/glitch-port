#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GlitchPort.Unity
{
    /// <summary>
    /// The result of mapping one Unity log message: the event that should be
    /// captured (if any) and the breadcrumb that should be recorded (if any).
    /// </summary>
    /// <remarks>
    /// A warning or log message can produce both entries when its capture flag
    /// and <see cref="GlitchPortSettings.CaptureBreadcrumbsFromLogs"/> are
    /// enabled. All other log types produce at most one event.
    /// </remarks>
    public readonly struct UnityLogMapping
    {
        /// <summary>Initializes a mapping with the given event and breadcrumb.</summary>
        /// <param name="mappedEvent">The event to capture, or <see langword="null"/>.</param>
        /// <param name="breadcrumb">The breadcrumb to record, or <see langword="null"/>.</param>
        public UnityLogMapping(GlitchPortEvent? mappedEvent, Breadcrumb? breadcrumb)
        {
            Event = mappedEvent;
            Breadcrumb = breadcrumb;
        }

        /// <summary>Gets the event that should be captured, or <see langword="null"/>.</summary>
        public GlitchPortEvent? Event { get; }

        /// <summary>Gets the breadcrumb that should be recorded, or <see langword="null"/>.</summary>
        public Breadcrumb? Breadcrumb { get; }

        /// <summary>Gets a value indicating whether an event should be captured.</summary>
        public bool HasEvent => Event is not null;

        /// <summary>Gets a value indicating whether a breadcrumb should be recorded.</summary>
        public bool HasBreadcrumb => Breadcrumb is not null;

        /// <summary>Gets a value indicating whether the message is ignored completely.</summary>
        public bool IsEmpty => Event is null && Breadcrumb is null;
    }

    /// <summary>
    /// Translates Unity log messages into glitch-port events and breadcrumbs.
    /// The bridge is a pure function of its arguments so it can be called from
    /// any thread and from the single-threaded editor tests without shared
    /// state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LogType.Exception"/> messages become structured events: the
    /// condition and stack trace are parsed by <see cref="UnityStackTraceParser"/>
    /// so the event carries the exception chain instead of a flattened string.
    /// </para>
    /// <para>
    /// <see cref="LogType.Error"/> and <see cref="LogType.Assert"/> messages with
    /// a stack trace become structured events too. Unity does not report an
    /// exception type for those logs, so a synthetic <c>LogError</c> or
    /// <c>Assert</c> exception with the condition's first line as its value
    /// carries the parser's frames.
    /// </para>
    /// <para>
    /// Warnings and logs are captured only when their settings flag is enabled.
    /// Independently of that flag, they are recorded as a <c>log</c> breadcrumb
    /// when <see cref="GlitchPortSettings.CaptureBreadcrumbsFromLogs"/> is
    /// enabled, so a later event still shows what the game logged.
    /// </para>
    /// </remarks>
    public static class UnityLogBridge
    {
        private const string DiagnosticPrefix = "[GlitchPort]";
        private const string LoggerName = "unity";
        private const string ExceptionFallbackType = "Exception";
        private const string ErrorExceptionType = "LogError";
        private const string AssertExceptionType = "Assert";

        /// <summary>
        /// Maps a Unity log message to the event and breadcrumb the SDK should
        /// record.
        /// </summary>
        /// <param name="condition">The message text Unity supplied.</param>
        /// <param name="stackTrace">The stack trace Unity supplied; it may be empty.</param>
        /// <param name="type">The Unity log type.</param>
        /// <param name="settings">The settings that decide which log types are captured.</param>
        /// <returns>
        /// The mapping. <see cref="UnityLogMapping.IsEmpty"/> is
        /// <see langword="true"/> when the message must be ignored.
        /// </returns>
        public static UnityLogMapping Map(
            string condition,
            string stackTrace,
            LogType type,
            GlitchPortSettings settings)
        {
            var message = condition ?? string.Empty;
            var stack = stackTrace ?? string.Empty;

            if (settings is null || !settings.Enabled)
            {
                return default;
            }

            if (message.StartsWith(DiagnosticPrefix, StringComparison.Ordinal))
            {
                return default;
            }

            switch (type)
            {
                case LogType.Exception:
                    return settings.CaptureExceptions
                        ? new UnityLogMapping(CreateExceptionEvent(message, stack), null)
                        : default;

                case LogType.Error:
                case LogType.Assert:
                    if (!settings.CaptureErrors)
                    {
                        return default;
                    }

                    var exceptionType = type == LogType.Assert
                        ? AssertExceptionType
                        : ErrorExceptionType;

                    return new UnityLogMapping(
                        CreateErrorEvent(message, stack, exceptionType),
                        null);

                case LogType.Warning:
                case LogType.Log:
                    return MapLoggedMessage(message, type, settings);

                default:
                    // A future Unity log type must not be captured by accident.
                    return default;
            }
        }

        private static UnityLogMapping MapLoggedMessage(
            string message,
            LogType type,
            GlitchPortSettings settings)
        {
            var warning = type == LogType.Warning;
            var level = warning ? Level.Warning : Level.Debug;
            var capture = warning ? settings.CaptureWarnings : settings.CaptureLogs;

            var mappedEvent = capture ? CreateMessageEvent(message, level) : null;
            var breadcrumb = settings.CaptureBreadcrumbsFromLogs
                ? UnityBreadcrumbFactory.Log(message, level)
                : null;

            return new UnityLogMapping(mappedEvent, breadcrumb);
        }

        private static GlitchPortEvent CreateExceptionEvent(string condition, string stackTrace)
        {
            var exceptions = UnityStackTraceParser.Parse(
                condition,
                stackTrace,
                ExceptionFallbackType);

            AttachLogCallSiteFramesWhenTheChainHasNone(exceptions, condition, stackTrace);

            return new GlitchPortEvent
            {
                Level = Level.Error,
                Logger = LoggerName,
                Exceptions = exceptions,
            };
        }

        /// <summary>
        /// An exception that was created but never thrown has no stack of its
        /// own: the only frames Unity can report are those of the
        /// <c>Debug.LogException</c> call. The parser intentionally removes such
        /// call-site frames from outer exception segments, so when the whole
        /// chain has no frames they are recovered here and attached to the
        /// outermost (logged) exception, whose stack is the log call site.
        /// </summary>
        private static void AttachLogCallSiteFramesWhenTheChainHasNone(
            IList<ExceptionInfo> exceptions,
            string condition,
            string stackTrace)
        {
            if (exceptions.Count == 0 || HasAnyFrames(exceptions))
            {
                return;
            }

            var outermost = exceptions[exceptions.Count - 1];
            if (outermost.Frames is { Count: > 0 })
            {
                return;
            }

            var frames = ParseLogCallSiteFrames(condition, stackTrace);
            if (frames.Count > 0)
            {
                outermost.Frames = frames;
            }
        }

        private static bool HasAnyFrames(IList<ExceptionInfo> exceptions)
        {
            for (var i = 0; i < exceptions.Count; i++)
            {
                var exception = exceptions[i];
                if (exception is not null && exception.Frames is { Count: > 0 })
                {
                    return true;
                }
            }

            return false;
        }

        private static IList<StackFrameInfo> ParseLogCallSiteFrames(string condition, string stackTrace)
        {
            var normalized = stackTrace.Replace("\r\n", "\n").Replace('\r', '\n');
            var wrapperIndex = normalized.IndexOf(
                "UnityEngine.Debug:LogException",
                StringComparison.Ordinal);
            if (wrapperIndex < 0)
            {
                return Array.Empty<StackFrameInfo>();
            }

            // Feed the parser only the wrapper and the frames below it; as a
            // condition segment it keeps the callers instead of dropping them.
            var lineStart = normalized.LastIndexOf('\n', Math.Max(0, wrapperIndex - 1)) + 1;
            var callSite = normalized.Substring(lineStart);
            var parsed = UnityStackTraceParser.Parse(condition, callSite, ExceptionFallbackType);

            return parsed.Count > 0 ? parsed[0].Frames : Array.Empty<StackFrameInfo>();
        }

        private static GlitchPortEvent CreateErrorEvent(
            string condition,
            string stackTrace,
            string exceptionType)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                return CreateMessageEvent(condition, Level.Error);
            }

            // Parse for the frames only: Unity reports a log, not an exception,
            // so the type and value are synthesized from the log type and the
            // condition's first line.
            var parsed = UnityStackTraceParser.Parse(condition, stackTrace, exceptionType);
            var frames = parsed.Count > 0
                ? parsed[0].Frames
                : (IList<StackFrameInfo>)Array.Empty<StackFrameInfo>();

            return new GlitchPortEvent
            {
                Level = Level.Error,
                Logger = LoggerName,
                Exceptions = new List<ExceptionInfo>
                {
                    new ExceptionInfo
                    {
                        Type = exceptionType,
                        Value = FirstLine(condition),
                        Frames = frames,
                    },
                },
            };
        }

        private static GlitchPortEvent CreateMessageEvent(string message, Level level)
        {
            return new GlitchPortEvent
            {
                Level = level,
                Logger = LoggerName,
                Message = message,
            };
        }

        private static string FirstLine(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (character == '\r' || character == '\n')
                {
                    return text.Substring(0, i).TrimEnd();
                }
            }

            return text.TrimEnd();
        }
    }
}
