using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>A single breadcrumb: a small step that led up to the event.</summary>
    public sealed class Breadcrumb
    {
        /// <summary>Gets or sets the time at which the breadcrumb was recorded.</summary>
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Gets or sets the breadcrumb category, for example <c>scene</c> or <c>http</c>.</summary>
        public string? Category { get; set; }

        /// <summary>Gets or sets the human-readable breadcrumb message.</summary>
        public string? Message { get; set; }

        /// <summary>Gets or sets the severity of the breadcrumb.</summary>
        public Level Level { get; set; } = Level.Info;

        /// <summary>Gets or sets the breadcrumb type, for example <c>default</c> or <c>http</c>.</summary>
        public string? Type { get; set; }

        /// <summary>Gets or sets the free-form JSON data attached to the breadcrumb.</summary>
        public IDictionary<string, object?>? Data { get; set; }
    }
}
