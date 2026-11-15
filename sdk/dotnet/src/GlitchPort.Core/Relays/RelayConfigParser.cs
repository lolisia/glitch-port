using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Parses the semicolon separated relay configuration strings understood by
    /// <see cref="GlitchPortOptions.FromEnvironment"/>.
    /// </summary>
    /// <remarks>
    /// Each non-empty item is one relay and may use one of the following forms:
    /// <list type="bullet">
    /// <item><description><c>http://key@host:port/projectId?min_level=error&amp;name=main&amp;offline_dir=queue&amp;offline_max_events=500</c></description></item>
    /// <item><description><c>terminal:?window=new&amp;format=pretty&amp;min_level=debug&amp;name=term&amp;path=...</c></description></item>
    /// <item><description><c>file:?dir=logs&amp;format=ndjson&amp;max_file_size_mb=10&amp;min_level=info&amp;name=file</c></description></item>
    /// </list>
    /// </remarks>
    public static class RelayConfigParser
    {
        private const string HttpType = "http";
        private const string TerminalType = "terminal";
        private const string FileType = "file";

        private const string MinLevelParameter = "min_level";
        private const string NameParameter = "name";
        private const string WindowParameter = "window";
        private const string FormatParameter = "format";
        private const string PathParameter = "path";
        private const string DirectoryParameter = "dir";
        private const string MaxFileSizeParameter = "max_file_size_mb";
        private const string MaxAgeParameter = "max_age_days";
        private const string MaxTotalSizeParameter = "max_total_size_mb";
        private const string MaxFilesParameter = "max_files";
        private const string FlushIntervalParameter = "flush_interval_ms";
        private const string OfflineDirectoryParameter = "offline_dir";
        private const string OfflineMaxEventsParameter = "offline_max_events";
        private const string OfflineMaxAgeHoursParameter = "offline_max_age_hours";
        private const string OfflineRetryIntervalSecondsParameter = "offline_retry_interval_seconds";

        private const string DefaultDirectory = "logs/glitchport";
        private const int DefaultOfflineMaxEvents = 500;
        private const int DefaultOfflineMaxAgeHours = 72;
        private const int DefaultOfflineRetryIntervalSeconds = 30;

        private static readonly string[] HttpParameters =
        {
            MinLevelParameter,
            NameParameter,
            OfflineDirectoryParameter,
            OfflineMaxEventsParameter,
            OfflineMaxAgeHoursParameter,
            OfflineRetryIntervalSecondsParameter,
        };

        private static readonly string[] TerminalParameters =
        {
            MinLevelParameter,
            NameParameter,
            WindowParameter,
            FormatParameter,
            PathParameter,
        };

        private static readonly string[] FileParameters =
        {
            MinLevelParameter,
            NameParameter,
            DirectoryParameter,
            FormatParameter,
            MaxFileSizeParameter,
            MaxAgeParameter,
            MaxTotalSizeParameter,
            MaxFilesParameter,
            FlushIntervalParameter,
        };

        /// <summary>
        /// Parses a semicolon separated relay configuration string.
        /// </summary>
        /// <param name="value">The configuration string. Items are trimmed and empty items are ignored.</param>
        /// <returns>The enabled relay definitions in input order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
        /// <exception cref="FormatException">An item is not a valid relay configuration. The message contains the 1-based item index and the reason.</exception>
        public static IReadOnlyList<RelayDefinition> Parse(string value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var definitions = new List<RelayDefinition>();
            var items = value.Split(new[] { ';' }, StringSplitOptions.None);
            var index = 0;

            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i].Trim();
                if (item.Length == 0)
                {
                    continue;
                }

                index++;
                definitions.Add(ParseItem(item, index));
            }

            return definitions.AsReadOnly();
        }

        private static RelayDefinition ParseItem(string item, int index)
        {
            if (item.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                SplitDsnAndQuery(item, out var dsn, out var query);
                return ParseHttp(dsn, query, index);
            }

            var separator = item.IndexOf(':');
            if (separator < 0)
            {
                if (string.Equals(item, TerminalType, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseTerminal(string.Empty, index);
                }

                if (string.Equals(item, FileType, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseFile(string.Empty, index);
                }

                throw Error(index, $"unknown relay type '{item}'.");
            }

            var scheme = item.Substring(0, separator);
            var remainder = item.Substring(separator + 1);

            if (string.Equals(scheme, TerminalType, StringComparison.OrdinalIgnoreCase))
            {
                return ParseTerminal(ExtractQuery(remainder, TerminalType, index), index);
            }

            if (string.Equals(scheme, FileType, StringComparison.OrdinalIgnoreCase))
            {
                return ParseFile(ExtractQuery(remainder, FileType, index), index);
            }

            if (string.Equals(scheme, HttpType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                // Not the documented "http://" prefix (for example "http:/host/1"),
                // but still an HTTP relay attempt: let the DSN parser explain why it
                // is malformed.
                SplitDsnAndQuery(item, out var dsn, out var query);
                return ParseHttp(dsn, query, index);
            }

            throw Error(index, $"unknown relay type '{scheme}'.");
        }

        private static RelayDefinition ParseHttp(string dsn, string query, int index)
        {
            var parameters = ParseParameters(query, HttpType, HttpParameters, index);
            var minLevel = ParseMinLevel(parameters, index);
            var name = ParseName(parameters, HttpType);

            string? offlineDirectory = null;
            if (parameters.TryGetValue(OfflineDirectoryParameter, out var offlineDirectoryValue))
            {
                if (string.IsNullOrWhiteSpace(offlineDirectoryValue))
                {
                    throw Error(index, $"parameter '{OfflineDirectoryParameter}' must not be blank.");
                }

                offlineDirectory = ExpandDirectory(offlineDirectoryValue);
            }

            var offlineMaxEvents = ParseInteger(
                parameters,
                OfflineMaxEventsParameter,
                DefaultOfflineMaxEvents,
                1,
                index);
            var offlineMaxAgeHours = ParseInteger(
                parameters,
                OfflineMaxAgeHoursParameter,
                DefaultOfflineMaxAgeHours,
                0,
                index);
            var offlineRetryIntervalSeconds = ParseInteger(
                parameters,
                OfflineRetryIntervalSecondsParameter,
                DefaultOfflineRetryIntervalSeconds,
                1,
                index);

            RelayDefinition definition;
            try
            {
                definition = Relay.Http(dsn, http =>
                {
                    http.OfflineQueue.Directory = offlineDirectory;
                    http.OfflineQueue.MaxEvents = offlineMaxEvents;
                    http.OfflineQueue.MaxAgeHours = offlineMaxAgeHours;
                    http.OfflineQueue.RetryIntervalSeconds = offlineRetryIntervalSeconds;
                });
            }
            catch (FormatException ex)
            {
                throw Error(index, "malformed DSN: " + ex.Message, ex);
            }

            definition.Name = name;
            definition.MinLevel = minLevel;
            definition.Enabled = true;
            return definition;
        }

        private static RelayDefinition ParseTerminal(string query, int index)
        {
            var parameters = ParseParameters(query, TerminalType, TerminalParameters, index);
            var minLevel = ParseMinLevel(parameters, index);
            var name = ParseName(parameters, TerminalType);

            var window = TerminalWindow.Inherit;
            if (parameters.TryGetValue(WindowParameter, out var windowValue))
            {
                if (string.Equals(windowValue, "new", StringComparison.Ordinal))
                {
                    window = TerminalWindow.New;
                }
                else if (string.Equals(windowValue, "inherit", StringComparison.Ordinal))
                {
                    window = TerminalWindow.Inherit;
                }
                else
                {
                    throw Error(index, $"unknown window value '{windowValue}'; expected 'new' or 'inherit'.");
                }
            }

            var format = TerminalFormat.Pretty;
            if (parameters.TryGetValue(FormatParameter, out var formatValue))
            {
                if (string.Equals(formatValue, "pretty", StringComparison.Ordinal))
                {
                    format = TerminalFormat.Pretty;
                }
                else if (string.Equals(formatValue, "compact", StringComparison.Ordinal))
                {
                    format = TerminalFormat.Compact;
                }
                else if (string.Equals(formatValue, "json", StringComparison.Ordinal))
                {
                    format = TerminalFormat.Json;
                }
                else
                {
                    throw Error(index, $"unknown format value '{formatValue}'; expected 'pretty', 'compact' or 'json'.");
                }
            }

            parameters.TryGetValue(PathParameter, out var executablePath);

            var definition = Relay.Terminal(options =>
            {
                options.Window = window;
                options.Format = format;
                options.ExecutablePath = executablePath;
            });

            definition.Name = name;
            definition.MinLevel = minLevel;
            definition.Enabled = true;
            return definition;
        }

        private static RelayDefinition ParseFile(string query, int index)
        {
            var parameters = ParseParameters(query, FileType, FileParameters, index);
            var minLevel = ParseMinLevel(parameters, index);
            var name = ParseName(parameters, FileType);

            var directory = DefaultDirectory;
            if (parameters.TryGetValue(DirectoryParameter, out var directoryValue))
            {
                directory = ExpandDirectory(directoryValue);
            }

            var format = FileFormat.Ndjson;
            if (parameters.TryGetValue(FormatParameter, out var formatValue))
            {
                if (string.Equals(formatValue, "ndjson", StringComparison.Ordinal))
                {
                    format = FileFormat.Ndjson;
                }
                else if (string.Equals(formatValue, "text", StringComparison.Ordinal))
                {
                    format = FileFormat.Text;
                }
                else
                {
                    throw Error(index, $"unknown format value '{formatValue}'; expected 'ndjson' or 'text'.");
                }
            }

            var maxFileSizeMb = ParseInteger(parameters, MaxFileSizeParameter, 10, 1, index);
            var maxAgeDays = ParseInteger(parameters, MaxAgeParameter, 7, 0, index);
            var maxTotalSizeMb = ParseInteger(parameters, MaxTotalSizeParameter, 200, 0, index);
            var maxFiles = ParseInteger(parameters, MaxFilesParameter, 100, 0, index);
            var flushIntervalMs = ParseInteger(parameters, FlushIntervalParameter, 1000, 1, index);

            var definition = Relay.File(options =>
            {
                options.Directory = directory;
                options.Format = format;
                options.MaxFileSizeMb = maxFileSizeMb;
                options.MaxAgeDays = maxAgeDays;
                options.MaxTotalSizeMb = maxTotalSizeMb;
                options.MaxFiles = maxFiles;
                options.FlushIntervalMs = flushIntervalMs;
            });

            definition.Name = name;
            definition.MinLevel = minLevel;
            definition.Enabled = true;
            return definition;
        }

        private static void SplitDsnAndQuery(string item, out string dsn, out string query)
        {
            var separator = item.IndexOf('?');
            if (separator < 0)
            {
                dsn = item;
                query = string.Empty;
                return;
            }

            dsn = item.Substring(0, separator);
            query = item.Substring(separator + 1);
        }

        private static string ExtractQuery(string remainder, string typeName, int index)
        {
            if (remainder.Length == 0)
            {
                return string.Empty;
            }

            if (remainder[0] != '?')
            {
                throw Error(index, $"invalid {typeName} relay; expected '?' before the parameters.");
            }

            return remainder.Substring(1);
        }

        private static Dictionary<string, string> ParseParameters(
            string query,
            string typeName,
            string[] allowedParameters,
            int index)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (query.Length == 0)
            {
                return parameters;
            }

            var pairs = query.Split('&');
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                if (pair.Length == 0)
                {
                    continue;
                }

                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair.Substring(0, separator);
                var rawValue = separator < 0 ? string.Empty : pair.Substring(separator + 1);

                if (!IsAllowedParameter(key, allowedParameters))
                {
                    throw Error(index, $"unknown parameter '{key}' for {typeName} relay.");
                }

                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(rawValue);
                }
                catch (UriFormatException ex)
                {
                    throw Error(index, $"invalid value for parameter '{key}'.", ex);
                }

                parameters[key] = decoded;
            }

            return parameters;
        }

        private static bool IsAllowedParameter(string key, string[] allowedParameters)
        {
            for (var i = 0; i < allowedParameters.Length; i++)
            {
                if (string.Equals(allowedParameters[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ParseName(Dictionary<string, string> parameters, string typeName)
        {
            return parameters.TryGetValue(NameParameter, out var name) ? name : typeName;
        }

        private static Level ParseMinLevel(Dictionary<string, string> parameters, int index)
        {
            if (!parameters.TryGetValue(MinLevelParameter, out var value))
            {
                return Level.Debug;
            }

            if (!LevelNames.TryParse(value, out var level))
            {
                throw Error(index, $"unknown min_level value '{value}'; expected debug, info, warning, error or fatal.");
            }

            return level;
        }

        private static int ParseInteger(
            Dictionary<string, string> parameters,
            string key,
            int defaultValue,
            int minimum,
            int index)
        {
            if (!parameters.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw Error(index, $"parameter '{key}' is not a valid integer: '{value}'.");
            }

            if (parsed < minimum)
            {
                throw Error(index, $"parameter '{key}' must be >= {minimum} but was {parsed}.");
            }

            return parsed;
        }

        private static string ExpandDirectory(string value)
        {
            return ExpandEnvironmentVariables(ExpandUserProfile(value));
        }

        private static string ExpandUserProfile(string value)
        {
            if (value.Length == 0 || value[0] != '~')
            {
                return value;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
            {
                return value;
            }

            if (value.Length == 1)
            {
                return profile;
            }

            if (value[1] == '/' || value[1] == '\\')
            {
                return Path.Combine(profile, value.Substring(2));
            }

            // `~user` style paths are not supported; leave them literal.
            return value;
        }

        private static string ExpandEnvironmentVariables(string value)
        {
            if (value.IndexOf('%') < 0 && value.IndexOf('$') < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            var i = 0;
            while (i < value.Length)
            {
                var current = value[i];

                if (current == '%')
                {
                    var end = value.IndexOf('%', i + 1);
                    if (end > i + 1)
                    {
                        var name = value.Substring(i + 1, end - i - 1);
                        var replacement = Environment.GetEnvironmentVariable(name);
                        if (replacement != null)
                        {
                            builder.Append(replacement);
                            i = end + 1;
                            continue;
                        }
                    }
                }
                else if (current == '$' && i + 1 < value.Length)
                {
                    if (value[i + 1] == '{')
                    {
                        var end = value.IndexOf('}', i + 2);
                        if (end > i + 2)
                        {
                            var name = value.Substring(i + 2, end - i - 2);
                            var replacement = Environment.GetEnvironmentVariable(name);
                            if (replacement != null)
                            {
                                builder.Append(replacement);
                                i = end + 1;
                                continue;
                            }
                        }
                    }
                    else if (IsVariableNameStart(value[i + 1]))
                    {
                        var end = i + 2;
                        while (end < value.Length && IsVariableNameChar(value[end]))
                        {
                            end++;
                        }

                        var name = value.Substring(i + 1, end - i - 1);
                        var replacement = Environment.GetEnvironmentVariable(name);
                        if (replacement != null)
                        {
                            builder.Append(replacement);
                            i = end;
                            continue;
                        }
                    }
                }

                builder.Append(current);
                i++;
            }

            return builder.ToString();
        }

        private static bool IsVariableNameStart(char value)
        {
            return value == '_' || (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
        }

        private static bool IsVariableNameChar(char value)
        {
            return value == '_'
                || (value >= 'a' && value <= 'z')
                || (value >= 'A' && value <= 'Z')
                || (value >= '0' && value <= '9');
        }

        private static FormatException Error(int index, string reason)
        {
            return new FormatException($"Relay #{index}: {reason}");
        }

        private static FormatException Error(int index, string reason, Exception innerException)
        {
            return new FormatException($"Relay #{index}: {reason}", innerException);
        }
    }
}
