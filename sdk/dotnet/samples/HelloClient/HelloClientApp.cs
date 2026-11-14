using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.HelloClient
{
    /// <summary>
    /// The testable body of the hello sample: resolves the message and DSN, captures
    /// the message through <see cref="GlitchPortClient"/>, flushes the relays and
    /// reports the outcome.
    /// </summary>
    internal static class HelloClientApp
    {
        /// <summary>The message captured when the user passes no argument.</summary>
        internal const string DefaultMessage = "hello";

        /// <summary>The DSN used when <see cref="DsnEnvironmentVariable"/> is not set.</summary>
        internal const string DefaultDsn = "http://dev-public-key@localhost:5080/1";

        /// <summary>The environment variable that overrides the DSN.</summary>
        internal const string DsnEnvironmentVariable = "GLITCHPORT_DSN";

        /// <summary>
        /// Captures the requested message, flushes the relays and writes either
        /// <c>event_id: &lt;guid&gt;</c> or a single-line <c>error: ...</c> line.
        /// </summary>
        /// <param name="args">Command-line arguments; <c>args[0]</c> is the message.</param>
        /// <param name="stdout">The writer that receives the event id on success.</param>
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
                var message = args.Length > 0 ? args[0] : DefaultMessage;
                var options = new GlitchPortOptions { Dsn = Dsn.Parse(ResolveDsn()) };

                using var client = (clientFactory ?? CreateClient)(options);

                var eventId = await client
                    .CaptureMessageAsync(message, cancellationToken)
                    .ConfigureAwait(false);

                stdout.WriteLine("event_id: " + eventId.ToString("D", CultureInfo.InvariantCulture));

                var report = await client.FlushAsync().ConfigureAwait(false);
                if (report.AllSucceeded)
                {
                    return 0;
                }

                var failure = FirstFailure(report);
                stderr.WriteLine("error: relay " + failure.Name + " failed: " + DescribeFailure(failure));
                return 1;
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

        private static GlitchPortClient CreateClient(GlitchPortOptions options)
        {
            return new GlitchPortClient(options);
        }

        private static RelayDeliveryResult FirstFailure(DeliveryReport report)
        {
            for (var i = 0; i < report.Relays.Count; i++)
            {
                if (!report.Relays[i].Succeeded)
                {
                    return report.Relays[i];
                }
            }

            return new RelayDeliveryResult("unknown", "unknown", 0, 1, 0, "no relay reported");
        }

        private static string DescribeFailure(RelayDeliveryResult result)
        {
            var detail = string.IsNullOrWhiteSpace(result.LastError)
                ? "failed=" + result.Failed.ToString(CultureInfo.InvariantCulture)
                    + " dropped=" + result.Dropped.ToString(CultureInfo.InvariantCulture)
                : result.LastError!;

            return FoldToSingleLine(detail);
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
    }
}
