using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.Terminal
{
    /// <summary>
    /// Renders the line protocol written by the terminal relay to a text writer.
    /// </summary>
    /// <remarks>
    /// The first non-blank line is the hello object
    /// <c>{"type":"hello","protocol":1,"parent_pid":N,"app":"..."}</c>; every later
    /// line is an event envelope <c>{"type":"event","payload":{...}}</c>. The
    /// payload is a protocol v1 event: it may carry a message, an exception chain
    /// (inner to outer, frames outermost caller first), tags, release and
    /// environment. The host never disposes the supplied reader or writer.
    /// </remarks>
    public static class TerminalHost
    {
        private const string InvalidLineMessage = "\u26a0 invalid line skipped";
        private const string UnsupportedProtocolPrefix = "\u26a0 unsupported protocol";

        /// <summary>How many in-app frames are summarised for the pretty output.</summary>
        private const int MaxDisplayFrames = 5;

        /// <summary>Runs the renderer until <paramref name="input"/> reaches EOF.</summary>
        /// <param name="input">The line source, normally the process's standard input.</param>
        /// <param name="output">The line sink, normally the process's standard output.</param>
        /// <param name="options">Rendering options.</param>
        /// <param name="ct">Cancellation token checked between lines.</param>
        /// <returns>0 when the input reaches EOF.</returns>
        public static async Task<int> RunAsync(
            TextReader input,
            TextWriter output,
            TerminalHostOptions options,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(options);

            var helloLineHandled = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return 0;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!helloLineHandled)
                {
                    helloLineHandled = true;
                    WriteHello(line, output, options);
                    continue;
                }

                WriteEvent(line, output, options);
            }
        }

        /// <summary>
        /// Writes the session header exactly once and reports a malformed hello
        /// line or an unsupported protocol.
        /// </summary>
        private static void WriteHello(string line, TextWriter output, TerminalHostOptions options)
        {
            var appName = options.AppName;
            var isHello = false;
            int? protocol = null;

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object && HasType(root, "hello"))
                {
                    isHello = true;

                    if (root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.String)
                    {
                        var value = app.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            appName = value!;
                        }
                    }

                    if (root.TryGetProperty("protocol", out var protocolElement)
                        && protocolElement.ValueKind == JsonValueKind.Number
                        && protocolElement.TryGetInt32(out var protocolValue))
                    {
                        protocol = protocolValue;
                    }
                }
            }
            catch (JsonException)
            {
                // Reported as an invalid line below; the session banner is still useful.
            }
            finally
            {
                document?.Dispose();
            }

            output.WriteLine(EventFormatter.FormatHeader(appName, Environment.ProcessId, DateTimeOffset.Now));

            if (!isHello)
            {
                output.WriteLine(InvalidLineMessage);
                return;
            }

            if (protocol != 1)
            {
                output.WriteLine(protocol is null
                    ? string.Concat(UnsupportedProtocolPrefix, " (expected 1)")
                    : string.Format(CultureInfo.InvariantCulture, "{0} {1} (expected 1)", UnsupportedProtocolPrefix, protocol.Value));
            }
        }

        /// <summary>Renders one event envelope, or reports the line as invalid.</summary>
        private static void WriteEvent(string line, TextWriter output, TerminalHostOptions options)
        {
            if (!TryReadEnvelope(line, out var payloadJson))
            {
                output.WriteLine(InvalidLineMessage);
                return;
            }

            if (options.Format == TerminalHostFormat.Json)
            {
                output.WriteLine(payloadJson);
                return;
            }

            if (!TryCreateEvent(payloadJson, out var capturedEvent))
            {
                output.WriteLine(InvalidLineMessage);
                return;
            }

            switch (options.Format)
            {
                case TerminalHostFormat.Pretty:
                    output.WriteLine(EventFormatter.FormatPretty(capturedEvent, options.Color));
                    break;
                case TerminalHostFormat.Compact:
                    output.WriteLine(EventFormatter.FormatCompact(capturedEvent, options.Color));
                    break;
                default:
                    output.WriteLine(InvalidLineMessage);
                    break;
            }
        }

        /// <summary>Extracts the raw <c>payload</c> JSON from an event envelope.</summary>
        private static bool TryReadEnvelope(string line, out string payloadJson)
        {
            payloadJson = string.Empty;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object || !HasType(root, "event"))
                {
                    return false;
                }

                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                payloadJson = payload.GetRawText();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>Rebuilds a <see cref="CapturedEvent"/> from a protocol v1 event payload.</summary>
        /// <remarks>
        /// A payload is valid when it carries its identifiers and SDK plus either a
        /// message or at least one exception. Exception-only events (for example a
        /// crash report) therefore need no message field.
        /// </remarks>
        private static bool TryCreateEvent(string payloadJson, out CapturedEvent capturedEvent)
        {
            capturedEvent = null!;

            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!TryGetString(root, "event_id", out var eventIdText)
                    || !Guid.TryParse(eventIdText, out var eventId))
                {
                    return false;
                }

                if (!TryGetString(root, "timestamp", out var timestampText)
                    || !DateTimeOffset.TryParse(
                        timestampText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var timestamp))
                {
                    return false;
                }

                if (!TryGetString(root, "level", out var levelText) || !LevelNames.TryParse(levelText, out var level))
                {
                    return false;
                }

                if (!root.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!TryGetString(sdk, "name", out var sdkName) || !TryGetString(sdk, "version", out var sdkVersion))
                {
                    return false;
                }

                if (!TryReadOptionalString(root, "message", out var message))
                {
                    return false;
                }

                if (!TryReadExceptions(root, out var exceptions))
                {
                    return false;
                }

                if (message is null && exceptions.Count == 0)
                {
                    return false;
                }

                if (!TryReadTags(root, out var tags)
                    || !TryReadOptionalString(root, "release", out var release)
                    || !TryReadOptionalString(root, "environment", out var environment))
                {
                    return false;
                }

                capturedEvent = new CapturedEvent(
                    eventId,
                    timestamp,
                    level,
                    message ?? string.Empty,
                    sdkName,
                    sdkVersion,
                    payloadJson,
                    exceptions,
                    tags,
                    release,
                    environment);

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the <c>exception</c> array (inner to outer) into summaries whose
        /// frames start at the crash site, keep only in-app frames and stop after
        /// five.
        /// </summary>
        private static bool TryReadExceptions(JsonElement root, out IReadOnlyList<CapturedExceptionSummary> exceptions)
        {
            exceptions = Array.Empty<CapturedExceptionSummary>();

            if (!root.TryGetProperty("exception", out var array) || array.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var summaries = new List<CapturedExceptionSummary>();

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                // The protocol requires a non-blank type on every exception.
                if (!TryGetString(item, "type", out var type) || string.IsNullOrWhiteSpace(type))
                {
                    return false;
                }

                var value = TryGetString(item, "value", out var valueText) ? valueText : string.Empty;

                if (!TryReadFrames(item, out var frames))
                {
                    return false;
                }

                summaries.Add(new CapturedExceptionSummary(type, value, frames));
            }

            exceptions = summaries;
            return true;
        }

        /// <summary>
        /// Converts <c>stacktrace.frames</c> (outermost caller first) into the
        /// crash-site-first, in-app-only summary stored on a
        /// <see cref="CapturedExceptionSummary"/>.
        /// </summary>
        private static bool TryReadFrames(JsonElement exception, out IReadOnlyList<CapturedStackFrame> frames)
        {
            frames = Array.Empty<CapturedStackFrame>();

            if (!exception.TryGetProperty("stacktrace", out var stacktrace) || stacktrace.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (stacktrace.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!stacktrace.TryGetProperty("frames", out var array) || array.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<ParsedFrame>(array.GetArrayLength());

            foreach (var item in array.EnumerateArray())
            {
                if (!TryReadFrame(item, out var frame, out var inApp))
                {
                    return false;
                }

                parsed.Add(new ParsedFrame(frame, inApp));
            }

            var selected = new List<CapturedStackFrame>(MaxDisplayFrames);

            // The wire order is outermost caller first and the crash site last;
            // walking backwards visits the throw site first.
            for (var i = parsed.Count - 1; i >= 0 && selected.Count < MaxDisplayFrames; i--)
            {
                if (parsed[i].InApp)
                {
                    selected.Add(parsed[i].Frame);
                }
            }

            frames = selected;
            return true;
        }

        /// <summary>
        /// Reads one frame object. A frame without an <c>in_app</c> flag (or with
        /// an explicit null) is an application frame, following the documented
        /// protocol; only an explicit <c>false</c> excludes it.
        /// </summary>
        private static bool TryReadFrame(JsonElement element, out CapturedStackFrame frame, out bool inApp)
        {
            frame = null!;
            inApp = true;

            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var function = ReadStringOrNull(element, "function");
            var module = ReadStringOrNull(element, "module");
            var fileName = ReadStringOrNull(element, "filename");

            int? lineNumber = null;

            if (element.TryGetProperty("lineno", out var lineno) && lineno.ValueKind != JsonValueKind.Null)
            {
                if (lineno.ValueKind != JsonValueKind.Number || !lineno.TryGetInt32(out var number))
                {
                    return false;
                }

                lineNumber = number;
            }

            if (element.TryGetProperty("in_app", out var inAppElement))
            {
                if (inAppElement.ValueKind == JsonValueKind.False)
                {
                    inApp = false;
                }
                else if (inAppElement.ValueKind != JsonValueKind.True
                    && inAppElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            frame = new CapturedStackFrame(module, function, fileName, lineNumber);
            return true;
        }

        /// <summary>Reads the string-valued <c>tags</c> object; other value types are ignored.</summary>
        private static bool TryReadTags(JsonElement root, out IReadOnlyDictionary<string, string>? tags)
        {
            tags = null;

            if (!root.TryGetProperty("tags", out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = property.Value.GetString();
                if (text is not null)
                {
                    values[property.Name] = text;
                }
            }

            tags = values;
            return true;
        }

        /// <summary>
        /// Reads an optional string property. A missing or null property means
        /// no value; any other non-string value is invalid.
        /// </summary>
        private static bool TryReadOptionalString(JsonElement element, string propertyName, out string? value)
        {
            value = null;

            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return true;
        }

        private static string? ReadStringOrNull(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
        }

        private static bool HasType(JsonElement root, string expectedType)
        {
            return root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), expectedType, StringComparison.Ordinal);
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                var text = property.GetString();
                if (text is not null)
                {
                    value = text;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        /// <summary>One parsed wire frame plus its <c>in_app</c> flag.</summary>
        private readonly struct ParsedFrame
        {
            public ParsedFrame(CapturedStackFrame frame, bool inApp)
            {
                Frame = frame;
                InApp = inApp;
            }

            public CapturedStackFrame Frame { get; }

            public bool InApp { get; }
        }
    }
}
