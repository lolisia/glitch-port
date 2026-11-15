using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>
    /// The mutable state that is merged into every event captured by a
    /// <see cref="GlitchPortClient"/>: tags, the affected user, context
    /// namespaces, breadcrumbs, a custom fingerprint and an optional level.
    /// </summary>
    /// <remarks>
    /// A client always has one global scope. <see cref="GlitchPortClient.PushScope"/>
    /// additionally pushes a temporary scope for the current asynchronous flow.
    /// Scopes are merged from the global scope through the outermost pushed scope
    /// to the innermost one; the event wins over all of them for scalar values.
    /// </remarks>
    public sealed class Scope
    {
        /// <summary>
        /// Gets or sets the string tags used for filtering and grouping. Later
        /// merges overwrite values with the same key.
        /// </summary>
        public IDictionary<string, string> Tags { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Gets or sets information about the affected user, or <see langword="null"/>.</summary>
        public UserInfo? User { get; set; }

        /// <summary>
        /// Gets or sets the free-form context namespaces. Later merges overwrite
        /// individual keys inside a namespace.
        /// </summary>
        public IDictionary<string, IDictionary<string, object?>> Contexts { get; set; } =
            new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal);

        /// <summary>Gets or sets the breadcrumbs in chronological order (oldest first).</summary>
        public IList<Breadcrumb> Breadcrumbs { get; set; } = new List<Breadcrumb>();

        /// <summary>Gets or sets the custom grouping fingerprint, at most ten components.</summary>
        public IList<string> Fingerprint { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the level applied to captured events. When set, it
        /// overrides the level passed to the capture method.
        /// </summary>
        public Level? Level { get; set; }

        /// <summary>
        /// Gets the enclosing scope of a pushed scope, used to merge from the
        /// outermost scope to the innermost one.
        /// </summary>
        internal Scope? Parent { get; set; }

        /// <summary>Sets a tag, replacing any value already stored under the same key.</summary>
        /// <param name="key">The tag name.</param>
        /// <param name="value">The tag value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
        public void SetTag(string key, string value)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            Tags[key] = value ?? string.Empty;
        }

        /// <summary>Sets the affected user, replacing the previous value.</summary>
        /// <param name="user">The user information, or <see langword="null"/> to clear it.</param>
        public void SetUser(UserInfo? user)
        {
            User = user;
        }

        /// <summary>Sets a context namespace, replacing any previous namespace with the same name.</summary>
        /// <param name="key">The context name, for example <c>game</c>.</param>
        /// <param name="value">The context entries.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
        public void SetContext(string key, IDictionary<string, object?> value)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            Contexts[key] = value;
        }

        /// <summary>Appends a breadcrumb to the scope.</summary>
        /// <param name="breadcrumb">The breadcrumb to add.</param>
        /// <exception cref="ArgumentNullException"><paramref name="breadcrumb"/> is <see langword="null"/>.</exception>
        public void AddBreadcrumb(Breadcrumb breadcrumb)
        {
            if (breadcrumb is null)
            {
                throw new ArgumentNullException(nameof(breadcrumb));
            }

            Breadcrumbs.Add(breadcrumb);
        }
    }
}
