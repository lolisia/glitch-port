using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>
    /// A single captured event plus its pre-serialized JSON body. Every relay
    /// forwards the same <see cref="Json"/> string.
    /// </summary>
    public sealed class CapturedEvent
    {
        private static readonly IReadOnlyList<CapturedExceptionSummary> EmptyExceptions =
            Array.Empty<CapturedExceptionSummary>();

        private static readonly IReadOnlyDictionary<string, string> EmptyTags =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Creates a captured event without exception summaries, tags, release or environment.</summary>
        /// <param name="eventId">The unique event id.</param>
        /// <param name="timestamp">The capture timestamp; stored in UTC.</param>
        /// <param name="level">The event severity.</param>
        /// <param name="message">The captured message.</param>
        /// <param name="sdkName">The SDK name reported to relays.</param>
        /// <param name="sdkVersion">The SDK version reported to relays.</param>
        /// <param name="json">The serialized event JSON shared by every relay.</param>
        public CapturedEvent(
            Guid eventId,
            DateTimeOffset timestamp,
            Level level,
            string message,
            string sdkName,
            string sdkVersion,
            string json)
            : this(eventId, timestamp, level, message, sdkName, sdkVersion, json, null, null, null, null)
        {
        }

        /// <summary>Creates a captured event with the display data exposed to relays and formatters.</summary>
        /// <param name="eventId">The unique event id.</param>
        /// <param name="timestamp">The capture timestamp; stored in UTC.</param>
        /// <param name="level">The event severity.</param>
        /// <param name="message">The captured message. Exception-only events use a type-and-message summary.</param>
        /// <param name="sdkName">The SDK name reported to relays.</param>
        /// <param name="sdkVersion">The SDK version reported to relays.</param>
        /// <param name="json">The serialized event JSON shared by every relay.</param>
        /// <param name="exceptions">
        /// The exception summaries ordered from the innermost cause to the
        /// outermost observed exception, or <see langword="null"/>.
        /// </param>
        /// <param name="tags">The merged event tags, or <see langword="null"/>.</param>
        /// <param name="release">The release identifier, or <see langword="null"/>.</param>
        /// <param name="environment">The deployment environment, or <see langword="null"/>.</param>
        public CapturedEvent(
            Guid eventId,
            DateTimeOffset timestamp,
            Level level,
            string message,
            string sdkName,
            string sdkVersion,
            string json,
            IEnumerable<CapturedExceptionSummary>? exceptions = null,
            IEnumerable<KeyValuePair<string, string>>? tags = null,
            string? release = null,
            string? environment = null)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (sdkName is null)
            {
                throw new ArgumentNullException(nameof(sdkName));
            }

            if (sdkVersion is null)
            {
                throw new ArgumentNullException(nameof(sdkVersion));
            }

            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            EventId = eventId;
            Timestamp = timestamp.ToUniversalTime();
            Level = level;
            Message = message;
            SdkName = sdkName;
            SdkVersion = sdkVersion;
            Json = json;
            Exceptions = exceptions is null
                ? EmptyExceptions
                : new List<CapturedExceptionSummary>(exceptions).AsReadOnly();
            Tags = tags is null
                ? EmptyTags
                : new Dictionary<string, string>(tags, StringComparer.Ordinal);
            Release = release;
            Environment = environment;
        }

        /// <summary>Gets the unique event id.</summary>
        public Guid EventId { get; }

        /// <summary>Gets the capture timestamp in UTC.</summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>Gets the event severity.</summary>
        public Level Level { get; }

        /// <summary>Gets the captured message.</summary>
        public string Message { get; }

        /// <summary>Gets the SDK name reported to relays.</summary>
        public string SdkName { get; }

        /// <summary>Gets the SDK version reported to relays.</summary>
        public string SdkVersion { get; }

        /// <summary>Gets the serialized event JSON that every relay forwards.</summary>
        public string Json { get; }

        /// <summary>
        /// Gets the exception summaries ordered from the innermost cause (index
        /// 0) to the outermost observed exception (last item).
        /// </summary>
        public IReadOnlyList<CapturedExceptionSummary> Exceptions { get; }

        /// <summary>
        /// Gets the outermost observed exception, or <see langword="null"/> for a
        /// message-only event.
        /// </summary>
        public CapturedExceptionSummary? Exception =>
            Exceptions.Count == 0 ? null : Exceptions[Exceptions.Count - 1];

        /// <summary>Gets the merged event tags.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; }

        /// <summary>Gets the release identifier, or <see langword="null"/>.</summary>
        public string? Release { get; }

        /// <summary>Gets the deployment environment, or <see langword="null"/>.</summary>
        public string? Environment { get; }
    }

    /// <summary>
    /// A compact exception summary rendered by terminals and log formatters.
    /// </summary>
    public sealed class CapturedExceptionSummary
    {
        /// <summary>Creates an empty summary.</summary>
        public CapturedExceptionSummary()
        {
        }

        /// <summary>Creates a summary from an exception type, value and selected frames.</summary>
        /// <param name="type">The exception type name.</param>
        /// <param name="value">The exception message.</param>
        /// <param name="frames">
        /// The frames ordered from the throw site backwards, or <see langword="null"/>.
        /// </param>
        public CapturedExceptionSummary(
            string? type,
            string? value,
            IEnumerable<CapturedStackFrame>? frames)
        {
            Type = type ?? string.Empty;
            Value = value ?? string.Empty;
            Frames = frames is null
                ? new List<CapturedStackFrame>()
                : new List<CapturedStackFrame>(frames);
        }

        /// <summary>Gets or sets the exception type, for example <c>System.InvalidOperationException</c>.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Gets or sets the exception message.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>Gets or sets the selected stack frames, ordered from the throw site backwards.</summary>
        public IList<CapturedStackFrame> Frames { get; set; } = new List<CapturedStackFrame>();
    }

    /// <summary>One stack frame of a <see cref="CapturedExceptionSummary"/>.</summary>
    public sealed class CapturedStackFrame
    {
        /// <summary>Creates an empty frame.</summary>
        public CapturedStackFrame()
        {
        }

        /// <summary>Creates a frame summary.</summary>
        /// <param name="module">The module or namespace that contains the frame.</param>
        /// <param name="function">The function or method name.</param>
        /// <param name="fileName">The source file name, when available.</param>
        /// <param name="lineNumber">The 1-based line number, or <see langword="null"/>.</param>
        public CapturedStackFrame(string? module, string? function, string? fileName, int? lineNumber)
        {
            Module = module;
            Function = function;
            FileName = fileName;
            LineNumber = lineNumber;
        }

        /// <summary>Gets or sets the module or namespace that contains the frame.</summary>
        public string? Module { get; set; }

        /// <summary>Gets or sets the function or method name.</summary>
        public string? Function { get; set; }

        /// <summary>Gets or sets the source file name, when available.</summary>
        public string? FileName { get; set; }

        /// <summary>Gets or sets the 1-based line number, or <see langword="null"/> when unavailable.</summary>
        public int? LineNumber { get; set; }
    }
}
