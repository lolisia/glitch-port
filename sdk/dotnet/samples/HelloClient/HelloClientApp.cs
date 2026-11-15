using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.HelloClient
{
    /// <summary>
    /// The testable body of the hello sample: parses the command line, resolves the
    /// relay configuration, captures the message (or, with <c>--exception</c>, a
    /// sample exception) through <see cref="GlitchPortClient"/>, flushes the relays
    /// and reports each relay's delivery counters.
    /// </summary>
    internal static class HelloClientApp
    {
        /// <summary>The message captured when the user passes no argument.</summary>
        internal const string DefaultMessage = "hello";

        /// <summary>The message of the sample exception captured by <c>--exception</c>.</summary>
        internal const string SampleExceptionMessage = "sample exception from HelloClient";

        /// <summary>The message of the sample exception's inner exception.</summary>
        internal const string SampleInnerExceptionMessage = "sample inner argument";

        /// <summary>The DSN used when <see cref="DsnEnvironmentVariable"/> is not set.</summary>
        internal const string DefaultDsn = "http://dev-public-key@localhost:5080/1";

        /// <summary>The environment variable that overrides the DSN.</summary>
        internal const string DsnEnvironmentVariable = "GLITCHPORT_DSN";

        /// <summary>
        /// Parses the arguments, captures the message (or a sample exception when
        /// <c>--exception</c> is set) <c>--count</c> times, flushes the relays,
        /// prints the terminal child pid (when one was started) and one delivery
        /// line per relay, optionally holds the process open, and writes a
        /// single-line <c>error: ...</c> message when anything fails.
        /// </summary>
        /// <param name="args">
        /// The command line: <c>[message] [--level &lt;debug|info|warning|error|fatal&gt;]
        /// [--count &lt;n&gt;] [--hold &lt;seconds&gt;] [--exception]</c>.
        /// </param>
        /// <param name="stdout">The writer that receives the event ids and report.</param>
        /// <param name="stderr">The writer that receives the error on failure.</param>
        /// <param name="cancellationToken">A token linked to Ctrl+C by the entry point.</param>
        /// <param name="clientFactory">
        /// Creates the client to use. Tests override this to supply a fake transport;
        /// production code leaves it <see langword="null"/>.
        /// </param>
        /// <returns>0 when every relay delivered the message; otherwise 1.</returns>
        internal static async Task<int> RunAsync(
            string[] args,
            TextWriter stdout,
            TextWriter stderr,
            CancellationToken cancellationToken = default,
            Func<GlitchPortOptions, GlitchPortClient>? clientFactory = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(stdout);
            ArgumentNullException.ThrowIfNull(stderr);

            try
            {
                if (!TryParseArguments(args, out var arguments, out var error))
                {
                    stderr.WriteLine("error: " + error);
                    return 1;
                }

                var options = CreateOptions();

                using var client = (clientFactory ?? CreateClient)(options);

                // A captured exception is an error unless --level asked for
                // another severity; a captured message keeps the parsed default.
                var level = arguments.Exception && !arguments.LevelSpecified
                    ? Level.Error
                    : arguments.Level;

                for (var i = 0; i < arguments.Count; i++)
                {
                    var eventId = arguments.Exception
                        ? await CaptureSampleExceptionAsync(client, level, cancellationToken).ConfigureAwait(false)
                        : await client.CaptureMessageAsync(arguments.Message, level, cancellationToken)
                            .ConfigureAwait(false);

                    stdout.WriteLine("event_id: " + eventId.ToString("D", CultureInfo.InvariantCulture));
                }

                var report = await client.FlushAsync().ConfigureAwait(false);

                WriteTerminalProcessIds(client, stdout);
                WriteDeliveryReport(report, stdout);

                if (arguments.HoldSeconds > 0)
                {
                    // Keeps a terminal relay window alive and gives the renderer time
                    // to show the events; Ctrl+C interrupts the wait.
                    await Task.Delay(TimeSpan.FromSeconds(arguments.HoldSeconds), cancellationToken)
                        .ConfigureAwait(false);
                }

                return report.AllSucceeded ? 0 : 1;
            }
            catch (Exception exception)
            {
                stderr.WriteLine("error: " + Describe(exception));
                return 1;
            }
        }

        /// <summary>
        /// Reads the DSN from <see cref="DsnEnvironmentVariable"/>, falling back to
        /// <see cref="DefaultDsn"/> when the variable is missing or blank.
        /// </summary>
        /// <returns>The configured DSN string.</returns>
        internal static string ResolveDsn()
        {
            var configured = Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
            return string.IsNullOrWhiteSpace(configured) ? DefaultDsn : configured;
        }

        /// <summary>
        /// Builds the client options. A configured <see cref="DsnEnvironmentVariable"/>
        /// is parsed as the semicolon separated relay configuration understood by
        /// <see cref="GlitchPortOptions.FromEnvironment"/>; otherwise the sample uses
        /// the local <see cref="DefaultDsn"/> HTTP relay.
        /// </summary>
        /// <returns>The options for the client.</returns>
        internal static GlitchPortOptions CreateOptions()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DsnEnvironmentVariable)))
            {
                return GlitchPortOptions.FromEnvironment();
            }

            return new GlitchPortOptions { Dsn = Dsn.Parse(ResolveDsn()) };
        }

        private static GlitchPortClient CreateClient(GlitchPortOptions options)
        {
            return new GlitchPortClient(options);
        }

        /// <summary>
        /// Throws and catches a sample exception, then reports it through
        /// <see cref="GlitchPortClient.CaptureExceptionAsync"/> — exactly what an
        /// application error boundary does with an exception it handled itself.
        /// </summary>
        /// <param name="client">The client that captures the exception.</param>
        /// <param name="level">The severity of the resulting event.</param>
        /// <param name="cancellationToken">A token that cancels the capture.</param>
        /// <returns>The generated event id.</returns>
        private static async Task<Guid> CaptureSampleExceptionAsync(
            GlitchPortClient client,
            Level level,
            CancellationToken cancellationToken)
        {
            try
            {
                throw new InvalidOperationException(
                    SampleExceptionMessage,
                    new ArgumentException(SampleInnerExceptionMessage));
            }
            catch (InvalidOperationException exception)
            {
                return await client
                    .CaptureExceptionAsync(exception, level, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Prints <c>terminal_pid: &lt;pid&gt;</c> for every terminal relay that
        /// started its child renderer.
        /// </summary>
        private static void WriteTerminalProcessIds(GlitchPortClient client, TextWriter stdout)
        {
            var relays = client.Relays;
            for (var i = 0; i < relays.Count; i++)
            {
                if (relays[i] is TerminalRelay terminal && terminal.ProcessId is int processId)
                {
                    stdout.WriteLine("terminal_pid: " + processId.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>Prints one delivery line per relay: counters and type.</summary>
        private static void WriteDeliveryReport(DeliveryReport report, TextWriter stdout)
        {
            for (var i = 0; i < report.Relays.Count; i++)
            {
                var result = report.Relays[i];

                stdout.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "relay {0} ({1}): delivered={2} failed={3} dropped={4}",
                    result.Name,
                    result.Type,
                    result.Delivered,
                    result.Failed,
                    result.Dropped));
            }
        }

        private static bool TryParseArguments(
            string[] args,
            out ParsedArguments arguments,
            out string error)
        {
            arguments = new ParsedArguments(
                DefaultMessage,
                Level.Info,
                levelSpecified: false,
                count: 1,
                exception: false,
                holdSeconds: 0d);
            error = string.Empty;

            var message = DefaultMessage;
            var messageSet = false;
            var level = Level.Info;
            var levelSpecified = false;
            var count = 1;
            var exception = false;
            var holdSeconds = 0d;

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i];

                switch (argument)
                {
                    case "--level":
                        if (!TryTakeValue(args, ref i, out var levelText))
                        {
                            error = "--level requires a value (debug, info, warning, error or fatal).";
                            return false;
                        }

                        if (!LevelNames.TryParse(levelText, out level))
                        {
                            error = "unknown level '" + levelText
                                + "'; expected debug, info, warning, error or fatal.";
                            return false;
                        }

                        levelSpecified = true;
                        break;

                    case "--exception":
                        exception = true;
                        break;

                    case "--count":
                        if (!TryTakeValue(args, ref i, out var countText))
                        {
                            error = "--count requires a value.";
                            return false;
                        }

                        if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                            || count < 1)
                        {
                            error = "--count must be an integer >= 1, but was '" + countText + "'.";
                            return false;
                        }

                        break;

                    case "--hold":
                        if (!TryTakeValue(args, ref i, out var holdText))
                        {
                            error = "--hold requires a value.";
                            return false;
                        }

                        if (!double.TryParse(holdText, NumberStyles.Float, CultureInfo.InvariantCulture, out holdSeconds)
                            || !double.IsFinite(holdSeconds)
                            || holdSeconds < 0)
                        {
                            error = "--hold must be a number of seconds >= 0, but was '" + holdText + "'.";
                            return false;
                        }

                        break;

                    default:
                        if (argument.Length > 0 && argument[0] == '-')
                        {
                            error = "unknown option '" + argument + "'.";
                            return false;
                        }

                        if (messageSet)
                        {
                            error = "unexpected argument '" + argument + "'.";
                            return false;
                        }

                        message = argument;
                        messageSet = true;
                        break;
                }
            }

            arguments = new ParsedArguments(message, level, levelSpecified, count, exception, holdSeconds);
            return true;
        }

        private static bool TryTakeValue(string[] args, ref int index, out string value)
        {
            if (index + 1 >= args.Length)
            {
                value = string.Empty;
                return false;
            }

            var candidate = args[index + 1];
            if (candidate.Length > 1 && candidate[0] == '-' && candidate[1] == '-')
            {
                // The next option was passed instead of a value; report the missing
                // value rather than treating the option itself as the value.
                value = string.Empty;
                return false;
            }

            index++;
            value = candidate;
            return true;
        }

        /// <summary>Renders an exception as a single line that always includes its message.</summary>
        private static string Describe(Exception exception)
        {
            var message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;

            return FoldToSingleLine(message);
        }

        /// <summary>Folds any line breaks so every diagnostic stays on one line.</summary>
        private static string FoldToSingleLine(string value)
        {
            return value
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        }

        private readonly struct ParsedArguments
        {
            public ParsedArguments(
                string message,
                Level level,
                bool levelSpecified,
                int count,
                bool exception,
                double holdSeconds)
            {
                Message = message;
                Level = level;
                LevelSpecified = levelSpecified;
                Count = count;
                Exception = exception;
                HoldSeconds = holdSeconds;
            }

            public string Message { get; }

            public Level Level { get; }

            /// <summary>Gets a value indicating whether <c>--level</c> was given.</summary>
            public bool LevelSpecified { get; }

            public int Count { get; }

            /// <summary>Gets a value indicating whether <c>--exception</c> was given.</summary>
            public bool Exception { get; }

            public double HoldSeconds { get; }
        }
    }
}
