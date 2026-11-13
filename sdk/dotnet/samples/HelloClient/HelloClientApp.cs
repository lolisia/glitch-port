using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.HelloClient
{
    /// <summary>
    /// The testable body of the hello sample: resolves the message and DSN, captures
    /// the message through <see cref="GlitchPortClient"/> and reports the outcome.
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
        /// Captures the requested message and writes either <c>event_id: &lt;guid&gt;</c>
        /// or a single-line <c>error: &lt;message&gt;</c> line.
        /// </summary>
        /// <param name="args">Command-line arguments; <c>args[0]</c> is the message.</param>
        /// <param name="stdout">The writer that receives the event id on success.</param>
        /// <param name="stderr">The writer that receives the error on failure.</param>
        /// <param name="cancellationToken">A token linked to Ctrl+C by the entry point.</param>
        /// <param name="clientFactory">
        /// Creates the client to use. Tests override this to supply a fake transport;
        /// production code leaves it <see langword="null"/>.
        /// </param>
        /// <returns>0 when the message was captured; otherwise 1.</returns>
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
                return 0;
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

        /// <summary>Renders an exception as a single line that always includes its message.</summary>
        private static string Describe(Exception exception)
        {
            var message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;

            // The sample contract is one line: fold any line breaks the exception
            // message may contain instead of splitting the error across lines.
            return message
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        }
    }
}
