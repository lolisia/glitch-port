using System;
using System.Collections.Generic;
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

        /// <summary>How many exception stack frames the pretty format shows at most.</summary>
        private const int MaxPrettyFrames = 5;

        /// <summary>
        /// Formats an event as a single compact line:
        /// <c>{local time} {LEVEL} {message}  (id {short id})</c>.
        /// An event with an exception uses the outermost exception's
        /// <c>type: value</c> in place of the message. Continuation lines of a
        /// multi-line message are indented by two spaces and the event id is
        /// appended to the last line.
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
            AppendTitle(builder, e);
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
        /// <remarks>
        /// An event with an exception shows the outermost exception's
        /// <c>type: value</c> in place of the message, followed by up to five of
        /// the selected stack frames (<c>  at Module.Function (file:line)</c>),
        /// one sorted tags summary line (<c>  tags: key=value, ...</c>) and the
        /// release and environment when the event carries them.
        /// </remarks>
        /// <param name="e">The event to format.</param>
        /// <param name="color">When <see langword="true"/>, wraps only the level token in ANSI SGR codes.</param>
        /// <returns>The formatted event, without a trailing newline.</returns>
        public static string FormatPretty(CapturedEvent e, bool color)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            var builder = new StringBuilder(e.Message.Length + 256);
            builder.Append(e.Timestamp.ToLocalTime().ToString(PrettyTimeFormat, CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(FormatLevel(e.Level, color));
            builder.Append(' ');
            AppendTitle(builder, e);

            // Frames, tags, release and environment belong to the exception
            // block. A message-only event has no exception (see
            // CapturedEvent.Exception) and must keep its previous output even
            // when the client attaches tags or metadata to it.
            var exception = e.Exception;
            if (exception is not null)
            {
                AppendFrames(builder, exception.Frames);
                AppendTags(builder, e.Tags);
                AppendMetadata(builder, "release", e.Release);
                AppendMetadata(builder, "environment", e.Environment);
            }

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

        /// <summary>
        /// Appends the event title: <c>type: value</c> of the outermost exception,
        /// or the message for a message-only event.
        /// </summary>
        private static void AppendTitle(StringBuilder builder, CapturedEvent e)
        {
            var exception = e.Exception;
            if (exception is not null)
            {
                var type = exception.Type ?? string.Empty;
                var value = exception.Value ?? string.Empty;

                // A summary without type and value carries no title; fall back to
                // the event message instead of printing an empty line.
                if (type.Length > 0 || value.Length > 0)
                {
                    AppendMessage(builder, FormatExceptionTitle(type, value));
                    return;
                }
            }

            AppendMessage(builder, e.Message);
        }

        /// <summary>Joins an exception type and value as <c>type: value</c>, omitting empty parts.</summary>
        private static string FormatExceptionTitle(string type, string value)
        {
            if (type.Length == 0)
            {
                return value;
            }

            return value.Length == 0 ? type : type + ": " + value;
        }

        /// <summary>
        /// Appends at most <see cref="MaxPrettyFrames"/> frames, in the order of
        /// the summary (throw site first), as
        /// <c>  at Module.Function (file:line)</c>.
        /// </summary>
        private static void AppendFrames(StringBuilder builder, IList<CapturedStackFrame>? frames)
        {
            if (frames is null)
            {
                return;
            }

            // The first MaxPrettyFrames summaries are inspected, counting even
            // the frames that cannot be rendered. Producers already cap the
            // summary, but this keeps the five-frame limit unconditional for
            // any CapturedEvent built by hand.
            for (var i = 0; i < frames.Count && i < MaxPrettyFrames; i++)
            {
                var frame = frames[i];
                if (frame is null)
                {
                    continue;
                }

                var name = FormatFrameName(frame);
                if (name.Length == 0)
                {
                    // A frame without a module and function has nothing to show.
                    continue;
                }

                builder.Append('\n');
                builder.Append(Indent);
                builder.Append("at ");
                builder.Append(name);

                var location = FormatFrameLocation(frame);
                if (location is not null)
                {
                    builder.Append(' ');
                    builder.Append(location);
                }
            }
        }

        /// <summary>Renders <c>Module.Function</c>, omitting the dot when a part is missing.</summary>
        private static string FormatFrameName(CapturedStackFrame frame)
        {
            var module = frame.Module ?? string.Empty;
            var function = frame.Function ?? string.Empty;

            if (module.Length == 0)
            {
                return function;
            }

            return function.Length == 0 ? module : module + "." + function;
        }

        /// <summary>Renders <c>(file:line)</c>, or <see langword="null"/> without a file name.</summary>
        private static string? FormatFrameLocation(CapturedStackFrame frame)
        {
            var fileName = frame.FileName;
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            if (!frame.LineNumber.HasValue)
            {
                return "(" + fileName + ")";
            }

            return "("
                + fileName
                + ":"
                + frame.LineNumber.Value.ToString(CultureInfo.InvariantCulture)
                + ")";
        }

        /// <summary>Appends one <c>  tags: key=value, ...</c> line when the event has tags.</summary>
        private static void AppendTags(StringBuilder builder, IReadOnlyDictionary<string, string>? tags)
        {
            if (tags is null || tags.Count == 0)
            {
                return;
            }

            var keys = new List<string>(tags.Count);
            foreach (var tag in tags)
            {
                keys.Add(tag.Key);
            }

            keys.Sort(StringComparer.Ordinal);

            builder.Append('\n');
            builder.Append(Indent);
            builder.Append("tags: ");

            for (var i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                var key = keys[i];
                builder.Append(key);
                builder.Append('=');
                builder.Append(tags[key] ?? string.Empty);
            }
        }

        /// <summary>Appends one <c>  {label}: {value}</c> line when the value is present.</summary>
        private static void AppendMetadata(StringBuilder builder, string label, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            builder.Append('\n');
            builder.Append(Indent);
            builder.Append(label);
            builder.Append(": ");
            builder.Append(value);
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
