using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>
    /// A single protocol v1 event: what happened, when, where and to whom.
    /// </summary>
    /// <remarks>
    /// The model is mutable on purpose: callers build an event incrementally and
    /// can inspect or rewrite it in <see cref="GlitchPortOptions.BeforeSend"/>.
    /// Optional strings are omitted from the JSON when they are
    /// <see langword="null"/> or empty, and the protocol limits are applied by
    /// the serializer just before the event is sent.
    /// </remarks>
    public sealed class GlitchPortEvent
    {
        /// <summary>Gets or sets the unique event id. Serialized as <c>event_id</c>.</summary>
        public Guid EventId { get; set; }

        /// <summary>Gets or sets the time at which the event occurred, serialized in UTC.</summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>Gets or sets the event severity.</summary>
        public Level Level { get; set; } = Level.Info;

        /// <summary>Gets or sets the platform or SDK family. Defaults to <c>dotnet</c>.</summary>
        public string Platform { get; set; } = DefaultPlatform;

        /// <summary>Gets or sets the logger or subsystem that produced the event.</summary>
        public string? Logger { get; set; }

        /// <summary>Gets or sets the human-readable message. At least one of message or exception is required.</summary>
        public string? Message { get; set; }

        /// <summary>Gets or sets the release identifier, for example <c>mygame@1.2.3</c>.</summary>
        public string? Release { get; set; }

        /// <summary>Gets or sets the deployment environment, for example <c>production</c>.</summary>
        public string? Environment { get; set; }

        /// <summary>Gets or sets the host or server that produced the event.</summary>
        public string? ServerName { get; set; }

        /// <summary>
        /// Gets or sets the exceptions ordered from the innermost cause (index 0)
        /// to the outermost observed exception (last item).
        /// </summary>
        public IList<ExceptionInfo> Exceptions { get; set; } = new List<ExceptionInfo>();

        /// <summary>Gets or sets the string tags used for filtering and grouping.</summary>
        public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Gets or sets information about the affected user, or <see langword="null"/>.</summary>
        public UserInfo? User { get; set; }

        /// <summary>
        /// Gets or sets the free-form context namespaces, for example <c>os</c>,
        /// <c>device</c> or <c>game</c>. Namespaces larger than 64 KB are omitted
        /// when the event is serialized.
        /// </summary>
        public IDictionary<string, IDictionary<string, object?>> Contexts { get; set; } =
            new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal);

        /// <summary>Gets or sets the breadcrumbs in chronological order (oldest first).</summary>
        public IList<Breadcrumb> Breadcrumbs { get; set; } = new List<Breadcrumb>();

        /// <summary>Gets or sets the custom grouping fingerprint, at most ten components.</summary>
        public IList<string> Fingerprint { get; set; } = new List<string>();

        /// <summary>Gets or sets the free-form extra data. Values larger than 64 KB are omitted.</summary>
        public IDictionary<string, object?> Extra { get; set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>The platform reported when <see cref="Platform"/> is not set.</summary>
        public const string DefaultPlatform = "dotnet";
    }
}
