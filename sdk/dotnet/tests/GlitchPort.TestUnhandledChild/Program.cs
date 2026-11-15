using System;
using System.Threading;

namespace GlitchPort.TestUnhandledChild
{
    /// <summary>
    /// Crashes with an unhandled exception after configuring a file relay and
    /// enabling <see cref="GlitchPortOptions.CaptureUnhandledExceptions"/>.
    /// </summary>
    /// <remarks>
    /// The first command line argument is the directory the file relay writes to.
    /// The process is expected to terminate with a non-zero exit code; the test
    /// asserts that a Fatal event reached the directory before it died.
    /// </remarks>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.Error.WriteLine("usage: GlitchPort.TestUnhandledChild <log-directory>");
                return 2;
            }

            var options = new GlitchPortOptions
            {
                CaptureUnhandledExceptions = true,
                Release = "unhandled-child@1.0.0",
                Environment = "test",
                ServerName = "unhandled-child",
            };

            options.Relays.Add(Relay.File(file =>
            {
                file.Directory = args[0];
                file.FlushIntervalMs = 600000;
            }));

            var client = new GlitchPortClient(options);

            // A dedicated foreground thread keeps the process alive until the
            // runtime handles the escaping exception.
            var thread = new Thread(() =>
            {
                throw new InvalidOperationException("unhandled child boom");
            })
            {
                IsBackground = false,
                Name = "glitchport unhandled child",
            };

            thread.Start();
            thread.Join();

            // The runtime terminates the process when the exception escapes; this
            // line is only reached if the behaviour changes.
            GC.KeepAlive(client);
            return 0;
        }
    }
}
