using System;
using System.Globalization;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Renders <see cref="CapturedEvent"/> instances as terminal-friendly text.
    /// The output never ends with a newline.
    /// </summary>
    public static class EventFormatter
    {
        private const string CompactTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
        private const string PrettyTimeFormat = "HH:mm:ss.fff";
        private const string HeaderTimeFormat = "yyyy-MM-dd HH:mm:ss";

        private const string Indent = "  ";
        private const string ResetColor = "\u001b[0m";

        /// <summary>
        /// Formats an event as a single compact line:
        /// <c>{local time} {LEVEL} {message}  (id {short id})</c>.
        /// Continuation lines of a multi-line message are indented by two spaces
        /// and the event id is appended to the last line.
        /// </summary>
        /// <param name="e">The event to format.</param>
        /// <param name="color">When <see langword="true"/>, wraps only the level token in ANSI SGR codes.</param>
        /// <returns>The formatted event, without a trailing newline.</returns>
        public static string FormatCompact(CapturedEvent e, bool color)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            var builder = new StringBuilder(e.Message.Length + 96);
            builder.Append(e.Timestamp.ToLocalTime().ToString(CompactTimeFormat, CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(FormatLevel(e.Level, color));
            builder.Append(' ');
            AppendMessage(builder, e.Message);
            builder.Append("  (id ");
            builder.Append(ShortEventId(e.EventId));
            builder.Append(')');

            return builder.ToString();
        }

        /// <summary>
        /// Formats an event over multiple lines: the first line starts with the
        /// local time and level, the last line is
        /// <c>  sdk={name}/{version} · id {short id}</c>. Continuation lines of a
        /// multi-line message are indented by two spaces.
        /// </summary>
        /// <param name="e">The event to format.</param>
        /// <param name="color">When <see langword="true"/>, wraps only the level token in ANSI SGR codes.</param>
        /// <returns>The formatted event, without a trailing newline.</returns>
        public static string FormatPretty(CapturedEvent e, bool color)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            var builder = new StringBuilder(e.Message.Length + 160);
            builder.Append(e.Timestamp.ToLocalTime().ToString(PrettyTimeFormat, CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(FormatLevel(e.Level, color));
            builder.Append(' ');
            AppendMessage(builder, e.Message);
            builder.Append('\n');
            builder.Append(Indent);
            builder.Append("sdk=");
            builder.Append(e.SdkName);
            builder.Append('/');
            builder.Append(e.SdkVersion);
            builder.Append(" \u00b7 id ");
            builder.Append(ShortEventId(e.EventId));

            return builder.ToString();
        }

        /// <summary>
        /// Formats the terminal session header:
        /// <c>═══ glitch-port terminal ═══ {appName} (pid {pid}) · {local time} · protocol 1</c>.
        /// </summary>
        /// <param name="appName">The application name shown in the header.</param>
        /// <param name="pid">The pid of the process that owns the terminal.</param>
        /// <param name="startedAt">The session start timestamp; rendered in local time.</param>
        /// <returns>The header line, without a trailing newline.</returns>
        public static string FormatHeader(string appName, int pid, DateTimeOffset startedAt)
        {
            if (appName is null)
            {
                throw new ArgumentNullException(nameof(appName));
            }

            var builder = new StringBuilder(appName.Length + 120);
            builder.Append("\u2550\u2550\u2550 glitch-port terminal \u2550\u2550\u2550 ");
            builder.Append(appName);
            builder.Append(" (pid ");
            builder.Append(pid.ToString(CultureInfo.InvariantCulture));
            builder.Append(") \u00b7 ");
            builder.Append(startedAt.ToLocalTime().ToString(HeaderTimeFormat, CultureInfo.InvariantCulture));
            builder.Append(" \u00b7 protocol 1");

            return builder.ToString();
        }

        /// <summary>
        /// Renders the level name upper-cased and padded to five characters, wrapped
        /// in the level's ANSI SGR codes when <paramref name="color"/> is set.
        /// </summary>
        private static string FormatLevel(Level level, bool color)
        {
            var token = LevelNames.ToJson(level).ToUpperInvariant().PadRight(5);

            return color
                ? ColorCode(level) + token + ResetColor
                : token;
        }

        /// <summary>Appends a message, indenting every line after the first by two spaces.</summary>
        private static void AppendMessage(StringBuilder builder, string message)
        {
            // CRLF is normalised so a Windows-style multi-line message does not leave
            // a stray carriage return in the terminal output; lines are then split on
            // a bare LF.
            var normalized = message.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            builder.Append(lines[0]);

            for (var i = 1; i < lines.Length; i++)
            {
                builder.Append('\n');
                builder.Append(Indent);
                builder.Append(lines[i]);
            }
        }

        /// <summary>Returns the first eight lower-case hex characters of the event id.</summary>
        private static string ShortEventId(Guid eventId)
        {
            return eventId.ToString("N").Substring(0, 8);
        }

        private static string ColorCode(Level level)
        {
            switch (level)
            {
                case Level.Fatal:
                    return "\u001b[41m";
                case Level.Error:
                    return "\u001b[31m";
                case Level.Warning:
                    return "\u001b[33m";
                case Level.Info:
                    return "\u001b[36m";
                case Level.Debug:
                    return "\u001b[90m";
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown level.");
            }
        }
    }
}
