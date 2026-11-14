using System;

namespace GlitchPort
{
    /// <summary>Severity of a captured event.</summary>
    public enum Level
    {
        /// <summary>Verbose diagnostic information.</summary>
        Debug = 0,

        /// <summary>Informational message.</summary>
        Info = 1,

        /// <summary>Something unexpected that does not stop the game.</summary>
        Warning = 2,

        /// <summary>A recoverable or handled error.</summary>
        Error = 3,

        /// <summary>A fatal error, typically a crash.</summary>
        Fatal = 4,
    }

    /// <summary>
    /// Converts <see cref="Level"/> values to and from the lower-case names used by
    /// the event JSON (<c>debug</c>, <c>info</c>, <c>warning</c>, <c>error</c>,
    /// <c>fatal</c>).
    /// </summary>
    public static class LevelNames
    {
        /// <summary>Returns the JSON name of <paramref name="level"/>.</summary>
        /// <param name="level">The level to convert.</param>
        /// <returns>The lower-case JSON name, for example <c>warning</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined level.</exception>
        public static string ToJson(Level level)
        {
            switch (level)
            {
                case Level.Debug:
                    return "debug";
                case Level.Info:
                    return "info";
                case Level.Warning:
                    return "warning";
                case Level.Error:
                    return "error";
                case Level.Fatal:
                    return "fatal";
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown level.");
            }
        }

        /// <summary>
        /// Parses a JSON level name, ignoring case.
        /// </summary>
        /// <param name="value">The level name, for example <c>Warning</c>.</param>
        /// <param name="level">The parsed level when the method returns <see langword="true"/>; otherwise the default value.</param>
        /// <returns><see langword="true"/> when <paramref name="value"/> is a known level name; otherwise <see langword="false"/>.</returns>
        public static bool TryParse(string? value, out Level level)
        {
            if (string.Equals(value, "debug", StringComparison.OrdinalIgnoreCase))
            {
                level = Level.Debug;
                return true;
            }

            if (string.Equals(value, "info", StringComparison.OrdinalIgnoreCase))
            {
                level = Level.Info;
                return true;
            }

            if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase))
            {
                level = Level.Warning;
                return true;
            }

            if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
            {
                level = Level.Error;
                return true;
            }

            if (string.Equals(value, "fatal", StringComparison.OrdinalIgnoreCase))
            {
                level = Level.Fatal;
                return true;
            }

            level = default;
            return false;
        }
    }
}
