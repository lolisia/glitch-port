using System;
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
    /// line is an event envelope <c>{"type":"event","payload":{...}}</c>. The host
    /// never disposes the supplied reader or writer.
    /// </remarks>
    public static class TerminalHost
    {
        private const string InvalidLineMessage = "\u26a0 invalid line skipped";
        private const string UnsupportedProtocolPrefix = "\u26a0 unsupported protocol";

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

        /// <summary>Rebuilds a <see cref="CapturedEvent"/> from an event payload.</summary>
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

                if (!TryGetString(root, "message", out var message))
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

                capturedEvent = new CapturedEvent(eventId, timestamp, level, message, sdkName, sdkVersion, payloadJson);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
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
    }
}
