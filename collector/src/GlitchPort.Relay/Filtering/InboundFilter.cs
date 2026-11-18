using System.Text.RegularExpressions;

namespace GlitchPort.Relay.Filtering;

/// <summary>
/// The compiled inbound filters of one project. Every glob is translated to an
/// anchored, case-insensitive regular expression exactly once when the project
/// configuration snapshot is built; matching a request only runs the compiled
/// expressions.
/// </summary>
/// <remarks>
/// A glob is matched against the whole value. <c>*</c> matches any string,
/// including the empty one and strings spanning line breaks, and every other
/// character is literal. A <see langword="null"/> value never matches. A
/// regular expression that times out is treated as no match so a pathological
/// value can never block an ingest request.
/// </remarks>
public sealed class InboundFilter
{
    /// <summary>Maximum time one compiled expression may run, in milliseconds.</summary>
    public const int RegexTimeoutMilliseconds = 50;

    /// <summary>A filter that matches nothing.</summary>
    public static InboundFilter Empty { get; } = new([], [], []);

    private readonly Regex[] _releases;
    private readonly Regex[] _environments;
    private readonly Regex[] _messagePatterns;

    /// <summary>Compiles the supplied glob patterns.</summary>
    public InboundFilter(
        IReadOnlyList<string>? releases,
        IReadOnlyList<string>? environments,
        IReadOnlyList<string>? messagePatterns)
    {
        _releases = Compile(releases);
        _environments = Compile(environments);
        _messagePatterns = Compile(messagePatterns);
    }

    /// <summary>Whether at least one of the three filters has a pattern.</summary>
    public bool HasFilters =>
        _releases.Length > 0 || _environments.Length > 0 || _messagePatterns.Length > 0;

    /// <summary>Whether <paramref name="release"/> matches a release filter.</summary>
    public bool MatchesRelease(string? release) => MatchesAny(_releases, release);

    /// <summary>Whether <paramref name="environment"/> matches an environment filter.</summary>
    public bool MatchesEnvironment(string? environment) => MatchesAny(_environments, environment);

    /// <summary>Whether <paramref name="message"/> matches a message filter.</summary>
    public bool MatchesMessage(string? message) => MatchesAny(_messagePatterns, message);

    /// <summary>
    /// Whether <c>{type}: {value}</c> matches a message filter, which is how an
    /// exception is exposed to message filtering.
    /// </summary>
    public bool MatchesException(string type, string? value) =>
        MatchesMessage($"{type}: {value}");

    /// <summary>
    /// Compiles one glob to an anchored regular expression. <c>*</c> becomes
    /// <c>[\s\S]*</c> (any string, newlines included) and everything else is
    /// escaped so it stays literal.
    /// </summary>
    public static Regex CompileGlob(string glob)
    {
        ArgumentException.ThrowIfNullOrEmpty(glob);

        var pattern = string.Concat(
            "\\A",
            Regex.Escape(glob).Replace("\\*", "[\\s\\S]*", StringComparison.Ordinal),
            "\\z");

        return new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));
    }

    private static Regex[] Compile(IReadOnlyList<string>? globs)
    {
        if (globs is null || globs.Count == 0)
        {
            return [];
        }

        var compiled = new List<Regex>(globs.Count);

        foreach (var glob in globs)
        {
            // The Processor rejects blank patterns; a hand-written or older
            // configuration may still carry one, so skip instead of failing
            // the whole refresh.
            if (string.IsNullOrEmpty(glob))
            {
                continue;
            }

            compiled.Add(CompileGlob(glob));
        }

        return compiled.ToArray();
    }

    private static bool MatchesAny(Regex[] patterns, string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(value))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A timeout counts as no match: filtering must never turn an
                // ingest request into an error.
            }
        }

        return false;
    }
}

/// <summary>
/// The raw inbound filter globs of one project, as received from the
/// Processor. <see cref="ProjectConfigSnapshot"/> compiles them once into an
/// <see cref="InboundFilter"/>.
/// </summary>
public sealed record InboundFilterConfig(
    IReadOnlyList<string> Releases,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> MessagePatterns)
{
    /// <summary>A configuration without any pattern.</summary>
    public static InboundFilterConfig Empty { get; } = new([], [], []);
}
