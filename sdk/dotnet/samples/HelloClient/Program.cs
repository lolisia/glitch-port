using System;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.HelloClient
{
    /// <summary>
    /// Console entry point for the end-to-end hello sample.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            using var cancellation = new CancellationTokenSource();

            void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
            {
                // Keep the process alive long enough to report the cancellation as
                // a regular error and exit with a non-zero code.
                eventArgs.Cancel = true;
                cancellation.Cancel();
            }

            Console.CancelKeyPress += OnCancelKeyPress;

            try
            {
                return await HelloClientApp
                    .RunAsync(args, Console.Out, Console.Error, cancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }
        }
    }
}
