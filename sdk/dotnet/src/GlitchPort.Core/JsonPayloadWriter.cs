using System;
using System.Globalization;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Hand-built JSON writer for the ingest payload.
    /// </summary>
    /// <remarks>
    /// The SDK must not depend on System.Text.Json or any other serialization
    /// package. Strings are escaped by hand (quotes, backslashes, the short C#
    /// control escapes and <c>\uXXXX</c> for the remaining control characters);
    /// non-ASCII text is written as-is and therefore ends up as raw UTF-8 when the
    /// request content is encoded.
    /// </remarks>
    internal static class JsonPayloadWriter
    {
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>Builds the <c>application/json</c> body for a captured message.</summary>
        public static string CreateMessagePayload(
            Guid eventId,
            DateTimeOffset timestamp,
            string message,
            string sdkName,
            string sdkVersion)
        {
            var builder = new StringBuilder(message.Length + 192);

            builder.Append("{\"event_id\":");
            AppendString(builder, eventId.ToString("D", CultureInfo.InvariantCulture));
            builder.Append(",\"timestamp\":");
            AppendString(builder, timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
            builder.Append(",\"message\":");
            AppendString(builder, message);
            builder.Append(",\"sdk\":{\"name\":");
            AppendString(builder, sdkName);
            builder.Append(",\"version\":");
            AppendString(builder, sdkVersion);
            builder.Append("}}");

            return builder.ToString();
        }

        /// <summary>Appends a JSON string literal, including its surrounding quotes.</summary>
        internal static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');

            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }
}
