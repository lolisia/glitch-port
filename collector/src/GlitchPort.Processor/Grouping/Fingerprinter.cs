using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GlitchPort.Processor.Grouping;

/// <summary>
/// A single frame of an exception stack trace.
/// </summary>
/// <param name="Function">Function or method name, when known.</param>
/// <param name="Module">Module, assembly or package name, when known.</param>
/// <param name="Filename">Source file name, when known.</param>
/// <param name="LineNo">One-based source line number. Never part of the fingerprint.</param>
/// <param name="ColNo">One-based source column number. Never part of the fingerprint.</param>
/// <param name="InApp">Whether the frame belongs to the user's application code.</param>
public sealed record EventFrame(
    string? Function,
    string? Module,
    string? Filename,
    int? LineNo,
    int? ColNo,
    bool InApp);

/// <summary>
/// An exception with its stack trace.
/// </summary>
/// <param name="Type">Exception type name, for example <c>NullReferenceException</c>.</param>
/// <param name="Value">Exception message, when known.</param>
/// <param name="Module">Module that raised the exception, when known.</param>
/// <param name="Frames">Stack trace frames ordered from the outermost call to the crash site.</param>
public sealed record EventException(
    string Type,
    string? Value,
    string? Module,
    IReadOnlyList<EventFrame> Frames);

/// <summary>
/// Everything the fingerprint, title and culprit are derived from.
/// </summary>
/// <param name="Exceptions">Exceptions, ordered from the innermost (cause) to the outermost.</param>
/// <param name="Message">Free-form event message, used when no exception is present.</param>
/// <param name="Logger">Logger or subsystem name, appended to message fingerprints.</param>
/// <param name="CustomFingerprint">Explicit fingerprint components that override grouping.</param>
public sealed record FingerprintInput(
    IReadOnlyList<EventException> Exceptions,
    string? Message,
    string? Logger,
    IReadOnlyList<string>? CustomFingerprint);

/// <summary>
/// The outcome of grouping an event.
/// </summary>
/// <param name="Fingerprint">Lowercase SHA-256 hex digest of the grouping material.</param>
/// <param name="Title">Human-readable issue title.</param>
/// <param name="Culprit">The last in-app frame formatted as <c>module.function</c>, when available.</param>
public sealed record FingerprintResult(string Fingerprint, string Title, string? Culprit);

/// <summary>
/// Computes stable issue fingerprints, titles and culprits for events.
/// </summary>
/// <remarks>
/// The fingerprint is the SHA-256 digest of a normalized grouping key, so two events
/// that only differ in volatile data such as line numbers, ids or memory addresses
/// are grouped together.
/// </remarks>
public static partial class Fingerprinter
{
    private const int MaxFrames = 5;
    private const int MaxTitleLength = 200;
    private const string CustomPrefix = "custom:";
    private const string ExceptionPrefix = "exception:";
    private const string MessagePrefix = "message:";
    private const char ComponentSeparator = '\n';

    /// <summary>
    /// Groups <paramref name="input"/> into a fingerprint, title and culprit.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    public static FingerprintResult Compute(FingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var exception = input.Exceptions is { Count: > 0 } exceptions ? exceptions[^1] : null;

        var material = BuildGroupingMaterial(input, exception);
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));

        return new FingerprintResult(
            fingerprint,
            BuildTitle(input, exception),
            BuildCulprit(exception));
    }

    private static string BuildGroupingMaterial(FingerprintInput input, EventException? exception)
    {
        if (input.CustomFingerprint is { Count: > 0 } customFingerprint)
        {
            return CustomPrefix + string.Join(ComponentSeparator, customFingerprint);
        }

        if (exception is not null)
        {
            var frames = SelectCrashSideFrames(exception.Frames);
            var frameParts = frames.Count == 0
                ? Normalize(exception.Value)
                : string.Join(ComponentSeparator, frames.Select(FormatFrame));

            return ExceptionPrefix + exception.Type + ComponentSeparator + frameParts;
        }

        return MessagePrefix + Normalize(FirstLine(input.Message)) + ComponentSeparator + input.Logger;
    }

    private static IReadOnlyList<EventFrame> SelectCrashSideFrames(IReadOnlyList<EventFrame>? frames)
    {
        if (frames is not { Count: > 0 })
        {
            return Array.Empty<EventFrame>();
        }

        var inApp = frames.Where(frame => frame.InApp).ToList();
        var candidates = inApp.Count > 0 ? inApp : frames;
        var skip = Math.Max(0, candidates.Count - MaxFrames);

        return candidates.Skip(skip).ToList();
    }

    private static string FormatFrame(EventFrame frame)
    {
        var function = Clean(frame.Function);
        var module = Clean(frame.Module);

        if (module.Length > 0 && function.Length > 0)
        {
            return $"{module}.{function}";
        }

        if (function.Length > 0)
        {
            return function;
        }

        return Clean(frame.Filename);
    }

    private static string BuildTitle(FingerprintInput input, EventException? exception)
    {
        if (exception is not null)
        {
            var type = Clean(exception.Type);
            var valueLine = FirstLine(exception.Value);

            if (valueLine.Length > 0)
            {
                return Truncate(type.Length > 0 ? $"{type}: {valueLine}" : valueLine);
            }

            if (type.Length > 0)
            {
                return Truncate(type);
            }
        }

        return Truncate(FirstLine(input.Message));
    }

    private static string? BuildCulprit(EventException? exception)
    {
        if (exception?.Frames is not { Count: > 0 } frames)
        {
            return null;
        }

        for (var index = frames.Count - 1; index >= 0; index--)
        {
            var frame = frames[index];

            if (!frame.InApp)
            {
                continue;
            }

            var module = Clean(frame.Module);
            var function = Clean(frame.Function);

            if (module.Length > 0 && function.Length > 0)
            {
                return $"{module}.{function}";
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces volatile values so that events differing only in them group together.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = Patterns.Uuid().Replace(value, "<uuid>");
        normalized = Patterns.HexLiteral().Replace(normalized, "<hex>");
        normalized = Patterns.LongHex().Replace(normalized, "<hex>");
        normalized = Patterns.Number().Replace(normalized, "<n>");
        normalized = Patterns.Whitespace().Replace(normalized, " ").Trim();

        return normalized;
    }

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lineBreak = value.AsSpan().IndexOfAny('\r', '\n');
        var line = lineBreak < 0 ? value : value[..lineBreak];

        return line.Trim();
    }

    private static string Truncate(string value) =>
        value.Length <= MaxTitleLength ? value : value[..MaxTitleLength];

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static partial class Patterns
    {
        [GeneratedRegex(
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            RegexOptions.CultureInvariant)]
        internal static partial Regex Uuid();

        [GeneratedRegex(
            @"\b0[xX][0-9a-fA-F]+\b",
            RegexOptions.CultureInvariant)]
        internal static partial Regex HexLiteral();

        [GeneratedRegex(
            @"\b[0-9a-fA-F]{16,}\b",
            RegexOptions.CultureInvariant)]
        internal static partial Regex LongHex();

        [GeneratedRegex(
            @"[0-9]+",
            RegexOptions.CultureInvariant)]
        internal static partial Regex Number();

        [GeneratedRegex(
            @"\s+",
            RegexOptions.CultureInvariant)]
        internal static partial Regex Whitespace();
    }
}
