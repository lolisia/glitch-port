using System.Runtime.CompilerServices;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Process-wide marker that lets the request middleware and the logger provider
/// agree on which exception instances were already captured.
/// </summary>
/// <remarks>
/// The table holds the exception weakly, so marking an exception never keeps it
/// (or an object graph reachable from it) alive. <see cref="ConditionalWeakTable{TKey,TValue}.TryAdd"/>
/// is atomic, which makes the check-and-mark operation safe when the same
/// exception is logged from several request threads at once.
/// </remarks>
internal static class GlitchPortExceptionMarker
{
    private static readonly object Marker = new object();

    private static readonly ConditionalWeakTable<Exception, object> Captured =
        new ConditionalWeakTable<Exception, object>();

    /// <summary>
    /// Marks <paramref name="exception"/> as captured.
    /// </summary>
    /// <param name="exception">The exception instance to claim.</param>
    /// <returns>
    /// <see langword="true"/> when this call marked the exception and the caller
    /// should capture it; <see langword="false"/> when another middleware or
    /// logger instance already captured the same instance.
    /// </returns>
    internal static bool TryMark(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Captured.TryAdd(exception, Marker);
    }
}
