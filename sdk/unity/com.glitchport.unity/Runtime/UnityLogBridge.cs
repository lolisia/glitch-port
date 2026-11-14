#nullable enable
using System;
using UnityEngine;

namespace GlitchPort.Unity
{
    /// <summary>
    /// Translates Unity log messages into glitch-port events. The bridge is a
    /// pure function of its arguments so it can be called from any thread and
    /// from the single-threaded editor tests without shared state.
    /// </summary>
    public static class UnityLogBridge
    {
        private const string DiagnosticPrefix = "[GlitchPort]";

        /// <summary>
        /// Maps a Unity log message to an event message and level.
        /// </summary>
        /// <param name="condition">The message text Unity supplied.</param>
        /// <param name="stackTrace">The stack trace Unity supplied; it may be empty.</param>
        /// <param name="type">The Unity log type.</param>
        /// <param name="settings">The settings that decide which log types are captured.</param>
        /// <param name="message">The event message when the method returns <see langword="true"/>.</param>
        /// <param name="level">The event level when the method returns <see langword="true"/>.</param>
        /// <returns>
        /// <see langword="true"/> when the message should be captured; otherwise
        /// <see langword="false"/>.
        /// </returns>
        public static bool TryMap(
            string condition,
            string stackTrace,
            LogType type,
            GlitchPortSettings settings,
            out string message,
            out Level level)
        {
            message = condition ?? string.Empty;
            level = Level.Debug;

            if (settings is null || !settings.Enabled)
            {
                return false;
            }

            if (message.StartsWith(DiagnosticPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            bool capture;
            switch (type)
            {
                case LogType.Exception:
                    level = Level.Error;
                    capture = settings.CaptureExceptions;
                    break;
                case LogType.Error:
                case LogType.Assert:
                    level = Level.Error;
                    capture = settings.CaptureErrors;
                    break;
                case LogType.Warning:
                    level = Level.Warning;
                    capture = settings.CaptureWarnings;
                    break;
                case LogType.Log:
                    level = Level.Debug;
                    capture = settings.CaptureLogs;
                    break;
                default:
                    level = Level.Debug;
                    return false;
            }

            if (!capture)
            {
                return false;
            }

            // Unity attaches the useful stack to Error and Exception messages.
            // Assert and the lower levels do not get it appended, matching the
            // documented event shape.
            if ((type == LogType.Error || type == LogType.Exception)
                && !string.IsNullOrEmpty(stackTrace))
            {
                message = message + "\n" + stackTrace;
            }

            return true;
        }
    }
}
