using System.Globalization;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Query parameter, request body and cursor validation shared by the
/// management endpoints.
/// </summary>
internal static class ManagementValidation
{
    /// <summary>Page size used when a request does not specify <c>limit</c>.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Largest accepted <c>limit</c>.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Largest accepted number of ids in a bulk action.</summary>
    public const int MaxBulkIds = 500;

    private static readonly string[] Statuses = ["unresolved", "resolved", "ignored"];

    private static readonly string[] Levels = ["debug", "info", "warning", "error", "fatal"];

    private static readonly string[] BulkActions =
        ["resolve", "resolve_next_release", "unresolve", "ignore", "delete", "add_labels", "remove_labels"];

    /// <summary>
    /// Normalizes the <c>status</c> filter. An absent value defaults to
    /// <c>unresolved</c> and <c>all</c> disables the filter.
    /// </summary>
    /// <param name="value">Raw query value.</param>
    /// <param name="status">Normalized filter, or <see langword="null"/> for all statuses.</param>
    /// <returns><see langword="false"/> when the value is not a known status.</returns>
    public static bool TryNormalizeStatusFilter(string? value, out string? status)
    {
        status = "unresolved";

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "all")
        {
            status = null;
            return true;
        }

        if (Array.IndexOf(Statuses, normalized) < 0)
        {
            return false;
        }

        status = normalized;
        return true;
    }

    /// <summary>Validates the <c>level</c> filter.</summary>
    public static bool TryNormalizeLevelFilter(string? value, out string? level)
    {
        level = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (Array.IndexOf(Levels, normalized) < 0)
        {
            return false;
        }

        level = normalized;
        return true;
    }

    /// <summary>Validates the issue <c>sort</c> parameter.</summary>
    public static bool TryParseSort(string? value, out IssueSort sort)
    {
        sort = IssueSort.LastSeen;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "last_seen":
                sort = IssueSort.LastSeen;
                return true;
            case "first_seen":
                sort = IssueSort.FirstSeen;
                return true;
            case "count":
                sort = IssueSort.Count;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Trims an optional filter and treats blank values as absent.</summary>
    public static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Parses the comma-separated <c>platform</c> filter into distinct,
    /// trimmed, lowercase platform names. Blank entries are dropped, and a
    /// value without any platform yields <see langword="null"/> (no filter).
    /// </summary>
    public static IReadOnlyList<string>? NormalizePlatforms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var platforms = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static platform => platform.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return platforms.Length == 0 ? null : platforms;
    }

    /// <summary>Validates a <c>limit</c> query value, applying the default.</summary>
    public static bool TryResolveLimit(string? value, out int pageSize)
    {
        pageSize = DefaultPageSize;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed is < 1 or > MaxPageSize)
        {
            return false;
        }

        pageSize = parsed;
        return true;
    }

    /// <summary>
    /// Parses a bulk action name into a typed action.
    /// </summary>
    /// <param name="value">Raw <c>action</c> value.</param>
    /// <param name="kind">Parsed action.</param>
    /// <param name="error">Validation error, when parsing fails.</param>
    public static bool TryParseBulkAction(string? value, out BulkIssueActionKind kind, out string? error)
    {
        kind = default;
        error = null;

        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized is null || Array.IndexOf(BulkActions, normalized) < 0)
        {
            error = $"action must be one of {string.Join(", ", BulkActions)}.";
            return false;
        }

        kind = normalized switch
        {
            "resolve" => BulkIssueActionKind.Resolve,
            "resolve_next_release" => BulkIssueActionKind.ResolveNextRelease,
            "unresolve" => BulkIssueActionKind.Unresolve,
            "ignore" => BulkIssueActionKind.Ignore,
            "delete" => BulkIssueActionKind.Delete,
            "add_labels" => BulkIssueActionKind.AddLabels,
            "remove_labels" => BulkIssueActionKind.RemoveLabels,
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unhandled bulk action validated above."),
        };

        return true;
    }

    /// <summary>
    /// Parses the <c>resolve_in</c> value of an issue status change. An absent
    /// value defaults to <see cref="IssueResolveMode.Now"/>.
    /// </summary>
    /// <param name="value">Raw <c>resolve_in</c> value.</param>
    /// <param name="mode">Parsed resolution mode.</param>
    /// <returns><see langword="false"/> when the value is not a known mode.</returns>
    public static bool TryParseResolveIn(string? value, out IssueResolveMode mode)
    {
        mode = IssueResolveMode.Now;

        if (value is null)
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "now":
                mode = IssueResolveMode.Now;
                return true;
            case "next_release":
                mode = IssueResolveMode.NextRelease;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parses the <c>regressed</c> filter: it is either absent or exactly
    /// <c>true</c>. Any other value is rejected.
    /// </summary>
    public static bool TryParseRegressedFilter(string? value, out bool regressed)
    {
        regressed = false;

        if (value is null)
        {
            return true;
        }

        if (!string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        regressed = true;
        return true;
    }

    /// <summary>
    /// Validates, trims and de-duplicates label names.
    /// </summary>
    public static bool TryNormalizeLabels(
        IReadOnlyList<string>? values,
        out List<string> names,
        out string? error)
    {
        names = [];
        error = null;

        if (values is null)
        {
            error = "labels must be an array of names.";
            return false;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "labels must not be blank.";
                return false;
            }

            var name = value.Trim();
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes an issue list cursor and checks that it matches
    /// <paramref name="sort"/>.
    /// </summary>
    public static bool TryDecodeIssueCursor(string raw, IssueSort sort, out IssueCursor cursor)
    {
        cursor = default;

        if (!ManagementCursor.TryDecode(raw, out var sortValue, out var idText))
        {
            return false;
        }

        if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return false;
        }

        switch (sort)
        {
            case IssueSort.Count:
                if (!long.TryParse(sortValue, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }

                break;

            default:
                if (!DateTimeOffset.TryParse(
                        sortValue,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out _))
                {
                    return false;
                }

                break;
        }

        cursor = new IssueCursor(sortValue, id);
        return true;
    }

    /// <summary>Decodes an issue event list cursor.</summary>
    public static bool TryDecodeEventCursor(string raw, out EventCursor cursor)
    {
        cursor = default;

        if (!ManagementCursor.TryDecode(raw, out var timestampText, out var idText))
        {
            return false;
        }

        if (!Guid.TryParseExact(idText, "D", out var id))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return false;
        }

        cursor = new EventCursor(timestamp, id);
        return true;
    }
}
