#nullable enable
using System;

namespace E2EGame
{
    /// <summary>
    /// Creates the nested exception that <c>scripts/e2e-unity.sh</c> reports
    /// through <c>Debug.LogException</c>.
    /// </summary>
    /// <remarks>
    /// The namespace deliberately does not start with <c>GlitchPort.</c>: the
    /// Unity stack trace parser marks <c>GlitchPort.</c> modules as SDK code, so
    /// a frame from this type is what proves that game frames are still reported
    /// as in-app.
    /// </remarks>
    public static class FaultFactory
    {
        /// <summary>The message of the outer exception the e2e check reports.</summary>
        public const string OuterMessage = "e2e unity exception";

        /// <summary>The message of the inner exception the e2e check reports.</summary>
        public const string InnerMessage = "e2e unity inner exception";

        /// <summary>
        /// Throws the nested exception and returns it after catching it, so the
        /// caller can log it without terminating the player. Throwing and
        /// catching here also keeps the outer exception's stack frames inside
        /// this non-<c>GlitchPort</c> namespace.
        /// </summary>
        /// <returns>The caught <see cref="InvalidOperationException"/>.</returns>
        public static InvalidOperationException CreateNested()
        {
            try
            {
                throw new InvalidOperationException(
                    OuterMessage,
                    new ArgumentException(InnerMessage));
            }
            catch (InvalidOperationException exception)
            {
                return exception;
            }
        }
    }
}
