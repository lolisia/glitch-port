using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Writes the free-form values found in <c>contexts</c>, <c>extra</c> and
    /// breadcrumb <c>data</c> as compact JSON.
    /// </summary>
    /// <remarks>
    /// The writer has no serializer dependency: it understands the scalar types
    /// used by game code, both generic and non-generic dictionaries, and any
    /// <see cref="IEnumerable"/>. Unknown objects fall back to their invariant
    /// string representation so that a payload can always be produced.
    /// </remarks>
    internal static class JsonValueWriter
    {
        private const int MaxDepth = 32;
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>Appends a JSON object for <paramref name="values"/>.</summary>
        public static void AppendObject(StringBuilder builder, IDictionary<string, object?>? values)
        {
            AppendObject(builder, values, 0);
        }

        /// <summary>Appends any supported value as compact JSON.</summary>
        public static void AppendValue(StringBuilder builder, object? value)
        {
            AppendValue(builder, value, 0);
        }

        /// <summary>Formats a timestamp the same way as the event envelope.</summary>
        public static string FormatTimestamp(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        private static void AppendObject(
            StringBuilder builder,
            IDictionary<string, object?>? values,
            int depth)
        {
            if (values is null)
            {
                builder.Append("null");
                return;
            }

            if (depth >= MaxDepth)
            {
                builder.Append("null");
                return;
            }

            builder.Append('{');
            var first = true;

            foreach (var pair in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                JsonPayloadWriter.AppendString(builder, pair.Key ?? string.Empty);
                builder.Append(':');
                AppendValue(builder, pair.Value, depth + 1);
            }

            builder.Append('}');
        }

        private static void AppendValue(StringBuilder builder, object? value, int depth)
        {
            if (value is null)
            {
                builder.Append("null");
                return;
            }

            if (depth >= MaxDepth)
            {
                // Cycles and pathologically deep data must not overflow the stack
                // while an error is being reported.
                builder.Append("null");
                return;
            }

            switch (value)
            {
                case string text:
                    JsonPayloadWriter.AppendString(builder, text);
                    return;

                case char character:
                    JsonPayloadWriter.AppendString(builder, character.ToString());
                    return;

                case bool boolean:
                    builder.Append(boolean ? "true" : "false");
                    return;

                case DateTimeOffset dateTimeOffsetValue:
                    JsonPayloadWriter.AppendString(builder, FormatTimestamp(dateTimeOffsetValue));
                    return;

                case DateTime dateTimeValue:
                    JsonPayloadWriter.AppendString(
                        builder,
                        dateTimeValue.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
                    return;

                case Guid guid:
                    JsonPayloadWriter.AppendString(builder, guid.ToString("D", CultureInfo.InvariantCulture));
                    return;

                case sbyte sbyteValue:
                    builder.Append(sbyteValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case byte byteValue:
                    builder.Append(byteValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case short shortValue:
                    builder.Append(shortValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case ushort ushortValue:
                    builder.Append(ushortValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case int intValue:
                    builder.Append(intValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case uint uintValue:
                    builder.Append(uintValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case long longValue:
                    builder.Append(longValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case ulong ulongValue:
                    builder.Append(ulongValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case float floatValue:
                    AppendFloatingPoint(builder, floatValue);
                    return;

                case double doubleValue:
                    AppendFloatingPoint(builder, doubleValue);
                    return;

                case decimal decimalValue:
                    builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
                    return;

                case IDictionary<string, object?> map:
                    AppendObject(builder, map, depth);
                    return;

                case IDictionary dictionary:
                    AppendDictionary(builder, dictionary, depth);
                    return;

                case IEnumerable sequence:
                    AppendArray(builder, sequence, depth);
                    return;

                default:
                    JsonPayloadWriter.AppendString(
                        builder,
                        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return;
            }
        }

        private static void AppendFloatingPoint(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                // NaN and the infinities are not valid JSON numbers.
                builder.Append("null");
                return;
            }

            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendFloatingPoint(StringBuilder builder, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                builder.Append("null");
                return;
            }

            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendDictionary(StringBuilder builder, IDictionary dictionary, int depth)
        {
            builder.Append('{');
            var first = true;

            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                JsonPayloadWriter.AppendString(
                    builder,
                    Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                builder.Append(':');
                AppendValue(builder, entry.Value, depth + 1);
            }

            builder.Append('}');
        }

        private static void AppendArray(StringBuilder builder, IEnumerable sequence, int depth)
        {
            builder.Append('[');
            var first = true;

            foreach (var item in sequence)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                AppendValue(builder, item, depth + 1);
            }

            builder.Append(']');
        }
    }
}
