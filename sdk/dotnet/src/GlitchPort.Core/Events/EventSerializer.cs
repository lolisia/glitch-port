using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Serializes a <see cref="GlitchPortEvent"/> as the compact, snake_case JSON
    /// body of protocol v1.
    /// </summary>
    /// <remarks>
    /// Every protocol limit is applied here, right before the event leaves the
    /// SDK: strings are truncated, collections are cut down to the documented
    /// subset and <c>contexts</c> namespaces or <c>extra</c> that would exceed
    /// 64 KB are omitted. The output never contains insignificant whitespace and
    /// always includes <c>event_id</c>, <c>timestamp</c>, <c>level</c>,
    /// <c>platform</c> and <c>sdk</c>.
    /// </remarks>
    internal static class EventSerializer
    {
        internal const int MaxMessageLength = 8192;
        internal const int MaxPlatformLength = 32;
        internal const int MaxLoggerLength = 128;
        internal const int MaxReleaseLength = 200;
        internal const int MaxEnvironmentLength = 64;
        internal const int MaxServerNameLength = 200;
        internal const int MaxExceptions = 10;
        internal const int MaxExceptionTypeLength = 200;
        internal const int MaxExceptionValueLength = 8192;
        internal const int MaxExceptionModuleLength = 200;
        internal const int MaxFrames = 250;
        internal const int MaxFrameStringLength = 1024;
        internal const int MaxTags = 50;
        internal const int MaxTagKeyLength = 32;
        internal const int MaxTagValueLength = 200;
        internal const int MaxBreadcrumbs = 100;
        internal const int MaxFingerprint = 10;
        internal const int MaxFingerprintLength = 200;
        internal const int MaxContextBytes = 65536;
        internal const int MaxExtraBytes = 65536;

        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>Serializes <paramref name="e"/> as compact protocol v1 JSON.</summary>
        /// <param name="e">The event to serialize.</param>
        /// <param name="sdkName">The SDK name reported in <c>sdk.name</c>.</param>
        /// <param name="sdkVersion">The SDK version reported in <c>sdk.version</c>.</param>
        /// <returns>The JSON request body.</returns>
        public static string Serialize(GlitchPortEvent e, string sdkName, string sdkVersion)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            var builder = new StringBuilder(512);

            builder.Append("{\"event_id\":");
            JsonPayloadWriter.AppendString(builder, e.EventId.ToString("D", CultureInfo.InvariantCulture));

            builder.Append(",\"timestamp\":");
            JsonPayloadWriter.AppendString(builder, FormatTimestamp(e.Timestamp));

            builder.Append(",\"level\":");
            JsonPayloadWriter.AppendString(builder, LevelNames.ToJson(e.Level));

            builder.Append(",\"platform\":");
            JsonPayloadWriter.AppendString(
                builder,
                Truncate(
                    string.IsNullOrWhiteSpace(e.Platform) ? GlitchPortEvent.DefaultPlatform : e.Platform,
                    MaxPlatformLength));

            AppendOptionalField(builder, "logger", e.Logger, MaxLoggerLength);
            AppendOptionalField(builder, "message", e.Message, MaxMessageLength);
            AppendOptionalField(builder, "release", e.Release, MaxReleaseLength);
            AppendOptionalField(builder, "environment", e.Environment, MaxEnvironmentLength);
            AppendOptionalField(builder, "server_name", e.ServerName, MaxServerNameLength);

            builder.Append(",\"sdk\":{\"name\":");
            JsonPayloadWriter.AppendString(builder, sdkName ?? string.Empty);
            builder.Append(",\"version\":");
            JsonPayloadWriter.AppendString(builder, sdkVersion ?? string.Empty);
            builder.Append('}');

            AppendExceptions(builder, e.Exceptions);
            AppendTags(builder, e.Tags);
            AppendUser(builder, e.User);
            AppendContexts(builder, e.Contexts);
            AppendBreadcrumbs(builder, e.Breadcrumbs);
            AppendFingerprint(builder, e.Fingerprint);
            AppendExtra(builder, e.Extra);

            builder.Append('}');

            return builder.ToString();
        }

        private static string FormatTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static void AppendOptionalField(StringBuilder builder, string name, string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            builder.Append(',');
            JsonPayloadWriter.AppendString(builder, name);
            builder.Append(':');
            JsonPayloadWriter.AppendString(builder, Truncate(value, maxLength));
        }

        private static void AppendPropertyName(StringBuilder builder, ref bool wroteProperty, string name)
        {
            if (wroteProperty)
            {
                builder.Append(',');
            }

            JsonPayloadWriter.AppendString(builder, name);
            builder.Append(':');
            wroteProperty = true;
        }

        private static void AppendOptionalProperty(
            StringBuilder builder,
            ref bool wroteProperty,
            string name,
            string? value,
            int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            AppendPropertyName(builder, ref wroteProperty, name);
            JsonPayloadWriter.AppendString(builder, Truncate(value, maxLength));
        }

        private static void AppendExceptions(StringBuilder builder, IList<ExceptionInfo>? exceptions)
        {
            if (exceptions is null || exceptions.Count == 0)
            {
                return;
            }

            // The list is ordered inner to outer, so the first entries are the
            // innermost causes that must be kept.
            var count = Math.Min(exceptions.Count, MaxExceptions);
            var opened = false;

            for (var i = 0; i < count; i++)
            {
                var exception = exceptions[i];
                if (exception is null)
                {
                    continue;
                }

                if (!opened)
                {
                    builder.Append(",\"exception\":[");
                    opened = true;
                }
                else
                {
                    builder.Append(',');
                }

                AppendException(builder, exception);
            }

            if (opened)
            {
                builder.Append(']');
            }
        }

        private static void AppendException(StringBuilder builder, ExceptionInfo exception)
        {
            builder.Append('{');

            // The schema requires type on every exception; a blank type is
            // written as an empty string rather than dropping the entry.
            builder.Append("\"type\":");
            JsonPayloadWriter.AppendString(
                builder,
                Truncate(exception.Type ?? string.Empty, MaxExceptionTypeLength));

            var wroteProperty = true;
            AppendOptionalProperty(builder, ref wroteProperty, "value", exception.Value, MaxExceptionValueLength);
            AppendOptionalProperty(builder, ref wroteProperty, "module", exception.Module, MaxExceptionModuleLength);

            AppendFrames(builder, exception.Frames, ref wroteProperty);

            builder.Append('}');
        }

        private static void AppendFrames(
            StringBuilder builder,
            IList<StackFrameInfo>? frames,
            ref bool wroteProperty)
        {
            if (frames is null || frames.Count == 0)
            {
                return;
            }

            // Frames are ordered shallowest to deepest, so keeping the tail keeps
            // the frames closest to the crash site.
            var start = Math.Max(0, frames.Count - MaxFrames);
            var opened = false;

            for (var i = start; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame is null)
                {
                    continue;
                }

                if (!opened)
                {
                    AppendPropertyName(builder, ref wroteProperty, "stacktrace");
                    builder.Append("{\"frames\":[");
                    opened = true;
                }
                else
                {
                    builder.Append(',');
                }

                AppendFrame(builder, frame);
            }

            if (opened)
            {
                builder.Append("]}");
            }
        }

        private static void AppendFrame(StringBuilder builder, StackFrameInfo frame)
        {
            builder.Append('{');
            var wroteProperty = false;

            AppendOptionalProperty(builder, ref wroteProperty, "function", frame.Function, MaxFrameStringLength);
            AppendOptionalProperty(builder, ref wroteProperty, "module", frame.Module, MaxFrameStringLength);
            AppendOptionalProperty(builder, ref wroteProperty, "filename", frame.FileName, MaxFrameStringLength);
            AppendOptionalProperty(builder, ref wroteProperty, "abs_path", frame.AbsPath, MaxFrameStringLength);

            if (frame.LineNumber.HasValue)
            {
                AppendPropertyName(builder, ref wroteProperty, "lineno");
                builder.Append(frame.LineNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (frame.ColumnNumber.HasValue)
            {
                AppendPropertyName(builder, ref wroteProperty, "colno");
                builder.Append(frame.ColumnNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            AppendPropertyName(builder, ref wroteProperty, "in_app");
            builder.Append(frame.InApp ? "true" : "false");

            builder.Append('}');
        }

        private static void AppendTags(StringBuilder builder, IDictionary<string, string>? tags)
        {
            if (tags is null || tags.Count == 0)
            {
                return;
            }

            var opened = false;
            var written = 0;

            foreach (var tag in tags)
            {
                if (written >= MaxTags)
                {
                    break;
                }

                if (tag.Key is null)
                {
                    continue;
                }

                if (!opened)
                {
                    builder.Append(",\"tags\":{");
                    opened = true;
                }
                else
                {
                    builder.Append(',');
                }

                JsonPayloadWriter.AppendString(builder, Truncate(tag.Key, MaxTagKeyLength));
                builder.Append(':');
                JsonPayloadWriter.AppendString(
                    builder,
                    Truncate(tag.Value ?? string.Empty, MaxTagValueLength));
                written++;
            }

            if (opened)
            {
                builder.Append('}');
            }
        }

        private static void AppendUser(StringBuilder builder, UserInfo? user)
        {
            if (user is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(user.Id)
                && string.IsNullOrEmpty(user.Username)
                && string.IsNullOrEmpty(user.Email)
                && string.IsNullOrEmpty(user.IpAddress))
            {
                return;
            }

            builder.Append(",\"user\":{");
            var wroteProperty = false;

            AppendOptionalProperty(builder, ref wroteProperty, "id", user.Id, int.MaxValue);
            AppendOptionalProperty(builder, ref wroteProperty, "username", user.Username, int.MaxValue);
            AppendOptionalProperty(builder, ref wroteProperty, "email", user.Email, int.MaxValue);
            AppendOptionalProperty(builder, ref wroteProperty, "ip_address", user.IpAddress, int.MaxValue);

            builder.Append('}');
        }

        private static void AppendContexts(
            StringBuilder builder,
            IDictionary<string, IDictionary<string, object?>>? contexts)
        {
            if (contexts is null || contexts.Count == 0)
            {
                return;
            }

            // Each namespace is serialized on its own so an oversized namespace
            // can be omitted without losing the smaller ones.
            var namespaceBuilder = new StringBuilder(256);
            var entriesBuilder = new StringBuilder(256);
            var opened = false;

            foreach (var context in contexts)
            {
                if (context.Value is null)
                {
                    continue;
                }

                namespaceBuilder.Clear();
                JsonValueWriter.AppendObject(namespaceBuilder, context.Value);

                if (Encoding.UTF8.GetByteCount(namespaceBuilder.ToString()) > MaxContextBytes)
                {
                    continue;
                }

                if (!opened)
                {
                    builder.Append(",\"contexts\":{");
                    opened = true;
                }
                else
                {
                    entriesBuilder.Append(',');
                }

                JsonPayloadWriter.AppendString(entriesBuilder, context.Key ?? string.Empty);
                entriesBuilder.Append(':');
                entriesBuilder.Append(namespaceBuilder);
            }

            if (opened)
            {
                builder.Append(entriesBuilder);
                builder.Append('}');
            }
        }

        private static void AppendBreadcrumbs(StringBuilder builder, IList<Breadcrumb>? breadcrumbs)
        {
            if (breadcrumbs is null || breadcrumbs.Count == 0)
            {
                return;
            }

            // Breadcrumbs are oldest first, so the oldest entries are kept.
            var count = Math.Min(breadcrumbs.Count, MaxBreadcrumbs);
            var opened = false;

            for (var i = 0; i < count; i++)
            {
                var breadcrumb = breadcrumbs[i];
                if (breadcrumb is null)
                {
                    continue;
                }

                if (!opened)
                {
                    builder.Append(",\"breadcrumbs\":[");
                    opened = true;
                }
                else
                {
                    builder.Append(',');
                }

                AppendBreadcrumb(builder, breadcrumb);
            }

            if (opened)
            {
                builder.Append(']');
            }
        }

        private static void AppendBreadcrumb(StringBuilder builder, Breadcrumb breadcrumb)
        {
            builder.Append('{');
            var wroteProperty = false;

            AppendPropertyName(builder, ref wroteProperty, "timestamp");
            JsonPayloadWriter.AppendString(builder, FormatTimestamp(breadcrumb.Timestamp));

            AppendOptionalProperty(builder, ref wroteProperty, "category", breadcrumb.Category, int.MaxValue);
            AppendOptionalProperty(builder, ref wroteProperty, "message", breadcrumb.Message, int.MaxValue);

            AppendPropertyName(builder, ref wroteProperty, "level");
            JsonPayloadWriter.AppendString(builder, LevelNames.ToJson(breadcrumb.Level));

            AppendOptionalProperty(builder, ref wroteProperty, "type", breadcrumb.Type, int.MaxValue);

            if (breadcrumb.Data is { Count: > 0 } data)
            {
                AppendPropertyName(builder, ref wroteProperty, "data");
                JsonValueWriter.AppendObject(builder, data);
            }

            builder.Append('}');
        }

        private static void AppendFingerprint(StringBuilder builder, IList<string>? fingerprint)
        {
            if (fingerprint is null || fingerprint.Count == 0)
            {
                return;
            }

            var count = Math.Min(fingerprint.Count, MaxFingerprint);

            builder.Append(",\"fingerprint\":[");

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                JsonPayloadWriter.AppendString(
                    builder,
                    Truncate(fingerprint[i] ?? string.Empty, MaxFingerprintLength));
            }

            builder.Append(']');
        }

        private static void AppendExtra(StringBuilder builder, IDictionary<string, object?>? extra)
        {
            if (extra is null || extra.Count == 0)
            {
                return;
            }

            var extraBuilder = new StringBuilder(256);
            JsonValueWriter.AppendObject(extraBuilder, extra);

            // Oversized extra data is dropped instead of failing the request.
            if (Encoding.UTF8.GetByteCount(extraBuilder.ToString()) > MaxExtraBytes)
            {
                return;
            }

            builder.Append(",\"extra\":");
            builder.Append(extraBuilder);
        }
    }
}
