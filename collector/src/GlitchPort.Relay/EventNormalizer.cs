using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlitchPort.Relay;

/// <summary>
/// Validates and normalizes protocol v1 event bodies. Only format errors are
/// rejected; over-length strings are truncated, extra array entries are
/// dropped and oversized free-form values are omitted.
/// </summary>
/// <remarks>
/// Request bodies are read with <see cref="JsonDocument"/>, which does not use
/// reflection. Normalized payloads are written through the source-generated
/// <see cref="RelayJsonContext"/> so the Relay stays AOT-safe.
/// </remarks>
internal static partial class EventNormalizer
{
    private const string LevelError =
        "must be one of 'debug', 'info', 'warning', 'error' or 'fatal'";

    private const string EventIdError =
        "must be a 32-character hexadecimal string or a UUID";

    private const string TimestampError =
        "must be an ISO 8601 date-time with a timezone";

    private const string PlatformRequiredError = "platform is required";

    private const string PlatformFormatError = "platform must match [a-z0-9._-]";

    private const string AutoIpAddress = "{{auto}}";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Validates and normalizes a <c>POST /api/{projectId}/events</c> body.
    /// </summary>
    /// <param name="body">Decompressed request body.</param>
    /// <param name="clientIp">Remote address of the request, if any.</param>
    /// <param name="receivedAt">Time at which the Relay accepted the event.</param>
    public static NormalizeResult NormalizeEvent(
        byte[] body,
        string? clientIp,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!TryParse(body, out var document, out var parseError))
        {
            return NormalizeResult.Failure(parseError!);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NormalizeResult.Failure("body: must be a JSON object");
            }

            try
            {
                return NormalizeResult.Success(NormalizeEvent(root, clientIp, receivedAt));
            }
            catch (EventFormatException exception)
            {
                return NormalizeResult.Failure(exception.Message);
            }
        }
    }

    /// <summary>
    /// Validates a legacy <c>POST /api/{projectId}/messages</c> body and
    /// converts it to a protocol v1 event.
    /// </summary>
    /// <param name="body">Decompressed request body.</param>
    /// <param name="receivedAt">Time at which the Relay accepted the event.</param>
    public static NormalizeResult NormalizeMessage(byte[] body, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!TryParse(body, out var document, out var parseError))
        {
            return NormalizeResult.Failure(parseError!);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NormalizeResult.Failure("body: must be a JSON object");
            }

            try
            {
                return NormalizeResult.Success(NormalizeMessage(root, receivedAt));
            }
            catch (EventFormatException exception)
            {
                return NormalizeResult.Failure(exception.Message);
            }
        }
    }

    private static NormalizedEvent NormalizeEvent(
        JsonElement root,
        string? clientIp,
        DateTimeOffset receivedAt)
    {
        var eventId = ParseEventId(OptionalString(root, "event_id", "event_id", maxLength: null));

        var timestampText = OptionalString(root, "timestamp", "timestamp", maxLength: null);
        var clientTimestamp = timestampText is null
            ? (DateTimeOffset?)null
            : ParseTimestamp(timestampText, "timestamp");

        var exceptions = NormalizeExceptions(root);
        var hasException = exceptions is { Length: > 0 };

        var level = OptionalString(root, "level", "level", maxLength: null);
        if (level is null)
        {
            level = hasException
                ? RelayConstants.DefaultExceptionLevel
                : RelayConstants.DefaultMessageLevel;
        }
        else
        {
            ValidateLevel(level, "level");
        }

        var message = EmptyToNull(
            OptionalString(root, "message", "message", RelayConstants.MaxMessageLength));

        if (!hasException && message is null)
        {
            throw Format(
                "message",
                "at least one of 'message' or 'exception' is required");
        }

        var platform = RequiredPlatform(root);

        var sdk = NormalizeSdk(root);

        var payload = new EventV1Payload
        {
            EventId = eventId.ToString("D"),
            Timestamp = FormatTimestamp(clientTimestamp ?? receivedAt),
            Level = level,
            Platform = platform,
            Logger = EmptyToNull(
                OptionalString(root, "logger", "logger", RelayConstants.MaxLoggerLength)),
            Message = message,
            Release = EmptyToNull(
                OptionalString(root, "release", "release", RelayConstants.MaxReleaseLength)),
            Environment = EmptyToNull(
                OptionalString(
                    root,
                    "environment",
                    "environment",
                    RelayConstants.MaxEnvironmentLength)),
            ServerName = EmptyToNull(
                OptionalString(
                    root,
                    "server_name",
                    "server_name",
                    RelayConstants.MaxServerNameLength)),
            Sdk = sdk,
            Exception = exceptions,
            Tags = NormalizeTags(root),
            User = NormalizeUser(root, clientIp),
            Contexts = NormalizeFreeForm(root, "contexts"),
            Breadcrumbs = NormalizeBreadcrumbs(root),
            Fingerprint = NormalizeFingerprint(root),
            Extra = NormalizeFreeForm(root, "extra"),
        };

        return new NormalizedEvent
        {
            EventId = eventId,
            Payload = payload,
            Message = message ?? string.Empty,
            ClientTimestamp = clientTimestamp,
            SdkName = sdk?.Name,
            SdkVersion = sdk?.Version,
        };
    }

    private static NormalizedEvent NormalizeMessage(JsonElement root, DateTimeOffset receivedAt)
    {
        var eventId = ParseEventId(OptionalString(root, "event_id", "event_id", maxLength: null));

        var timestampText = OptionalString(root, "timestamp", "timestamp", maxLength: null);
        var clientTimestamp = timestampText is null
            ? (DateTimeOffset?)null
            : ParseTimestamp(timestampText, "timestamp");

        var level = OptionalString(root, "level", "level", maxLength: null);
        if (level is not null)
        {
            ValidateLevel(level, "level");
        }

        var message = EmptyToNull(OptionalString(root, "message", "message", maxLength: null));
        if (message is null)
        {
            throw Format("message", "the 'message' field is required and must not be blank");
        }

        if (message.Length > RelayConstants.MaxMessageLength)
        {
            throw Format(
                "message",
                $"must be at most {RelayConstants.MaxMessageLength} characters");
        }

        var sdk = NormalizeSdk(root);

        var payload = new EventV1Payload
        {
            EventId = eventId.ToString("D"),
            Timestamp = FormatTimestamp(clientTimestamp ?? receivedAt),
            Level = level ?? RelayConstants.DefaultMessageLevel,
            Platform = RelayConstants.DefaultPlatform,
            Message = message,
            Sdk = sdk,
        };

        return new NormalizedEvent
        {
            EventId = eventId,
            Payload = payload,
            Message = message,
            ClientTimestamp = clientTimestamp,
            SdkName = sdk?.Name,
            SdkVersion = sdk?.Version,
        };
    }

    private static ExceptionPayload[]? NormalizeExceptions(JsonElement root)
    {
        if (!root.TryGetProperty("exception", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Format("exception", "expected an array");
        }

        var count = Math.Min(element.GetArrayLength(), RelayConstants.MaxExceptions);
        var exceptions = new ExceptionPayload[count];
        var index = 0;

        foreach (var item in element.EnumerateArray())
        {
            if (index == count)
            {
                break;
            }

            exceptions[index] = NormalizeException(item, index);
            index++;
        }

        return exceptions;
    }

    private static ExceptionPayload NormalizeException(JsonElement item, int index)
    {
        var path = $"exception[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw Format(path, "expected an object");
        }

        var type = OptionalString(
            item,
            "type",
            $"{path}.type",
            RelayConstants.MaxExceptionTypeLength);

        if (string.IsNullOrEmpty(type))
        {
            throw Format($"{path}.type", "must not be blank");
        }

        return new ExceptionPayload
        {
            Type = type,
            Value = EmptyToNull(
                OptionalString(
                    item,
                    "value",
                    $"{path}.value",
                    RelayConstants.MaxExceptionValueLength)),
            Module = EmptyToNull(
                OptionalString(
                    item,
                    "module",
                    $"{path}.module",
                    RelayConstants.MaxExceptionModuleLength)),
            Stacktrace = NormalizeStacktrace(item, path),
        };
    }

    private static StacktracePayload? NormalizeStacktrace(JsonElement item, string exceptionPath)
    {
        if (!item.TryGetProperty("stacktrace", out var stacktrace)
            || stacktrace.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (stacktrace.ValueKind != JsonValueKind.Object)
        {
            throw Format($"{exceptionPath}.stacktrace", "expected an object");
        }

        if (!stacktrace.TryGetProperty("frames", out var frames)
            || frames.ValueKind == JsonValueKind.Null)
        {
            return new StacktracePayload();
        }

        if (frames.ValueKind != JsonValueKind.Array)
        {
            throw Format($"{exceptionPath}.stacktrace.frames", "expected an array");
        }

        // Frames are ordered deepest to shallowest and the last frame is the
        // crash site, so an over-long stack keeps its last entries.
        var total = frames.GetArrayLength();
        var skip = Math.Max(0, total - RelayConstants.MaxFrames);
        var kept = new List<StackFramePayload>(Math.Min(total, RelayConstants.MaxFrames));
        var index = 0;

        foreach (var frame in frames.EnumerateArray())
        {
            if (index >= skip)
            {
                kept.Add(NormalizeFrame(frame, exceptionPath, index));
            }

            index++;
        }

        return new StacktracePayload { Frames = kept.ToArray() };
    }

    private static StackFramePayload NormalizeFrame(
        JsonElement frame,
        string exceptionPath,
        int index)
    {
        var path = $"{exceptionPath}.stacktrace.frames[{index}]";
        if (frame.ValueKind != JsonValueKind.Object)
        {
            throw Format(path, "expected an object");
        }

        return new StackFramePayload
        {
            Function = EmptyToNull(
                OptionalString(
                    frame,
                    "function",
                    $"{path}.function",
                    RelayConstants.MaxFrameStringLength)),
            Module = EmptyToNull(
                OptionalString(
                    frame,
                    "module",
                    $"{path}.module",
                    RelayConstants.MaxFrameStringLength)),
            Filename = EmptyToNull(
                OptionalString(
                    frame,
                    "filename",
                    $"{path}.filename",
                    RelayConstants.MaxFrameStringLength)),
            AbsPath = EmptyToNull(
                OptionalString(
                    frame,
                    "abs_path",
                    $"{path}.abs_path",
                    RelayConstants.MaxFrameStringLength)),
            ContextLine = EmptyToNull(
                OptionalString(
                    frame,
                    "context_line",
                    $"{path}.context_line",
                    RelayConstants.MaxFrameStringLength)),
            Lineno = OptionalInteger(frame, "lineno", $"{path}.lineno"),
            Colno = OptionalInteger(frame, "colno", $"{path}.colno"),
            InApp = OptionalBoolean(frame, "in_app", $"{path}.in_app"),
        };
    }

    private static Dictionary<string, string>? NormalizeTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (tags.ValueKind != JsonValueKind.Object)
        {
            throw Format("tags", "expected an object");
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var property in tags.EnumerateObject())
        {
            if (index == RelayConstants.MaxTags)
            {
                break;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw Format($"tags.{property.Name}", "must be a string");
            }

            var key = Truncate(
                property.Name.Trim(),
                RelayConstants.MaxTagKeyLength);
            var value = Truncate(
                (property.Value.GetString() ?? string.Empty).Trim(),
                RelayConstants.MaxTagValueLength);

            normalized[key] = value;
            index++;
        }

        return normalized;
    }

    private static UserPayload? NormalizeUser(JsonElement root, string? clientIp)
    {
        if (!root.TryGetProperty("user", out var user) || user.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (user.ValueKind != JsonValueKind.Object)
        {
            throw Format("user", "expected an object");
        }

        var ipAddress = EmptyToNull(
            OptionalString(user, "ip_address", "user.ip_address", maxLength: null));

        if (string.Equals(ipAddress, AutoIpAddress, StringComparison.Ordinal))
        {
            ipAddress = clientIp;
        }

        var payload = new UserPayload
        {
            Id = EmptyToNull(OptionalString(user, "id", "user.id", maxLength: null)),
            Username = EmptyToNull(
                OptionalString(user, "username", "user.username", maxLength: null)),
            Email = EmptyToNull(OptionalString(user, "email", "user.email", maxLength: null)),
            IpAddress = ipAddress,
        };

        return payload.Id is null
            && payload.Username is null
            && payload.Email is null
            && payload.IpAddress is null
            ? null
            : payload;
    }

    private static BreadcrumbPayload[]? NormalizeBreadcrumbs(JsonElement root)
    {
        if (!root.TryGetProperty("breadcrumbs", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Format("breadcrumbs", "expected an array");
        }

        var count = Math.Min(element.GetArrayLength(), RelayConstants.MaxBreadcrumbs);
        var breadcrumbs = new BreadcrumbPayload[count];
        var index = 0;

        foreach (var item in element.EnumerateArray())
        {
            if (index == count)
            {
                break;
            }

            breadcrumbs[index] = NormalizeBreadcrumb(item, index);
            index++;
        }

        return breadcrumbs;
    }

    private static BreadcrumbPayload NormalizeBreadcrumb(JsonElement item, int index)
    {
        var path = $"breadcrumbs[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw Format(path, "expected an object");
        }

        var timestamp = EmptyToNull(
            OptionalString(item, "timestamp", $"{path}.timestamp", maxLength: null));
        if (timestamp is not null)
        {
            timestamp = FormatTimestamp(ParseTimestamp(timestamp, $"{path}.timestamp"));
        }

        var level = OptionalString(item, "level", $"{path}.level", maxLength: null);
        if (level is not null)
        {
            ValidateLevel(level, $"{path}.level");
        }

        JsonElement? data = null;
        if (item.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind != JsonValueKind.Null)
        {
            data = dataElement.Clone();
        }

        return new BreadcrumbPayload
        {
            Timestamp = timestamp,
            Category = EmptyToNull(
                OptionalString(item, "category", $"{path}.category", maxLength: null)),
            Message = EmptyToNull(
                OptionalString(item, "message", $"{path}.message", maxLength: null)),
            Level = level,
            Type = EmptyToNull(OptionalString(item, "type", $"{path}.type", maxLength: null)),
            Data = data,
        };
    }

    private static string[]? NormalizeFingerprint(JsonElement root)
    {
        if (!root.TryGetProperty("fingerprint", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Format("fingerprint", "expected an array");
        }

        var count = Math.Min(element.GetArrayLength(), RelayConstants.MaxFingerprintEntries);
        var fingerprint = new string[count];
        var index = 0;

        foreach (var item in element.EnumerateArray())
        {
            if (index == count)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                throw Format($"fingerprint[{index}]", "expected a string");
            }

            fingerprint[index] = Truncate(
                (item.GetString() ?? string.Empty).Trim(),
                RelayConstants.MaxFingerprintEntryLength);
            index++;
        }

        return fingerprint;
    }

    private static SdkPayload? NormalizeSdk(JsonElement root)
    {
        if (!root.TryGetProperty("sdk", out var sdk) || sdk.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (sdk.ValueKind != JsonValueKind.Object)
        {
            throw Format("sdk", "expected an object");
        }

        var name = EmptyToNull(OptionalString(sdk, "name", "sdk.name", maxLength: null));
        var version = EmptyToNull(OptionalString(sdk, "version", "sdk.version", maxLength: null));

        return name is null && version is null
            ? null
            : new SdkPayload { Name = name, Version = version };
    }

    private static JsonElement? NormalizeFreeForm(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Format(name, "expected an object");
        }

        var clone = element.Clone();
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            clone,
            RelayJsonContext.Default.JsonElement);

        return serialized.Length > RelayConstants.MaxFreeFormBytes ? null : clone;
    }

    private static Guid ParseEventId(string? value)
    {
        if (value is null)
        {
            return Guid.NewGuid();
        }

        if (!EventIdPattern().IsMatch(value) || !Guid.TryParse(value, out var eventId))
        {
            throw Format("event_id", EventIdError);
        }

        return eventId;
    }

    private static DateTimeOffset ParseTimestamp(string value, string path)
    {
        if (!TimestampPattern().IsMatch(value)
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw Format(path, TimestampError);
        }

        return timestamp;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the mandatory protocol v1 platform. The value is trimmed and
    /// truncated to <see cref="RelayConstants.MaxPlatformLength"/> before it is
    /// lowercased and validated, so an over-long but otherwise valid platform
    /// is accepted.
    /// </summary>
    private static string RequiredPlatform(JsonElement root)
    {
        var platform = EmptyToNull(
            OptionalString(root, "platform", "platform", RelayConstants.MaxPlatformLength));

        if (platform is null)
        {
            throw new EventFormatException(PlatformRequiredError);
        }

        platform = platform.ToLowerInvariant();

        if (!PlatformPattern().IsMatch(platform))
        {
            throw new EventFormatException(PlatformFormatError);
        }

        return platform;
    }

    private static void ValidateLevel(string level, string path)
    {
        if (level is not "debug" and not "info" and not "warning" and not "error" and not "fatal")
        {
            throw Format(path, LevelError);
        }
    }

    private static string? OptionalString(
        JsonElement root,
        string name,
        string path,
        int? maxLength)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw Format(path, "expected a string");
        }

        var value = (element.GetString() ?? string.Empty).Trim();
        return maxLength is int limit ? Truncate(value, limit) : value;
    }

    private static long? OptionalInteger(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            throw Format(path, "expected an integer");
        }

        return value;
    }

    private static bool? OptionalBoolean(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Format(path, "expected a boolean");
        }

        return element.GetBoolean();
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Truncates a value to at most <paramref name="maxLength"/> Unicode code
    /// points without splitting surrogate pairs.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var builder = new StringBuilder(maxLength);
        var count = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (count == maxLength)
            {
                break;
            }

            builder.Append(rune);
            count++;
        }

        return builder.ToString();
    }

    private static bool TryParse(
        byte[] body,
        out JsonDocument document,
        out string? error)
    {
        try
        {
            document = JsonDocument.Parse(body);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            error = "body: the request body is not valid JSON";
            return false;
        }
    }

    private static EventFormatException Format(string path, string reason) => new(path, reason);

    [GeneratedRegex(
        "^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EventIdPattern();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    // Anchored with \A and \z so a trailing newline cannot sneak through after
    // truncation ('$' would also match immediately before a trailing '\n').
    [GeneratedRegex(@"\A[a-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformPattern();

    /// <summary>Raised internally when a field has an invalid format.</summary>
    private sealed class EventFormatException : Exception
    {
        public EventFormatException(string path, string reason)
            : base($"{path}: {reason}")
        {
        }

        public EventFormatException(string message)
            : base(message)
        {
        }
    }
}
