using System;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>
    /// Captures exceptions that escape application code: fatal exceptions raised
    /// through <see cref="AppDomain.UnhandledException"/> and unobserved faults
    /// reported by <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    /// <remarks>
    /// The native crash path is the only place where the SDK deliberately blocks:
    /// after capturing a fatal exception it waits at most two seconds for the
    /// queued event to reach the relays. Everything else stays asynchronous.
    /// A <see cref="GlitchPortClient"/> can register the integration automatically
    /// by setting <see cref="GlitchPortOptions.CaptureUnhandledExceptions"/>.
    /// </remarks>
    public static class UnhandledExceptionIntegration
    {
        private static readonly TimeSpan FatalFlushTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Subscribes to the process-wide unhandled exception events and reports
        /// them through <paramref name="client"/>.
        /// </summary>
        /// <param name="client">The client that receives the events.</param>
        /// <returns>
        /// A disposable that removes both subscriptions. Disposing it more than
        /// once is safe.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
        public static IDisposable Register(GlitchPortClient client)
        {
            if (client is null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            return new Registration(client);
        }

        private sealed class Registration : IDisposable
        {
            private readonly GlitchPortClient _client;
            private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;
            private readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobservedTaskExceptionHandler;

            private int _disposed;

            public Registration(GlitchPortClient client)
            {
                _client = client;
                _unhandledExceptionHandler = OnUnhandledException;
                _unobservedTaskExceptionHandler = OnUnobservedTaskException;

                AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
                TaskScheduler.UnobservedTaskException += _unobservedTaskExceptionHandler;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                try
                {
                    AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
                }
                catch (Exception)
                {
                    // The process is already shutting down; the subscription is
                    // harmless because the client is being disposed as well.
                }

                try
                {
                    TaskScheduler.UnobservedTaskException -= _unobservedTaskExceptionHandler;
                }
                catch (Exception)
                {
                    // Unsubscribing must never throw out of Dispose.
                }
            }

            private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
            {
                try
                {
                    if (e.ExceptionObject is Exception exception)
                    {
                        _client
                            .CaptureExceptionAsync(exception, Level.Fatal)
                            .GetAwaiter()
                            .GetResult();
                    }
                    else
                    {
                        _client
                            .CaptureMessageAsync(
                                "Unhandled exception: " + e.ExceptionObject,
                                Level.Fatal)
                            .GetAwaiter()
                            .GetResult();
                    }
                }
                catch (Exception)
                {
                    // Capturing a crash must never throw on the crashing thread.
                }

                try
                {
                    // The runtime is about to terminate the process. Give the
                    // relays a bounded chance to drain the fatal event.
                    _client.FlushAsync(FatalFlushTimeout).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Flushing during process termination is best effort.
                }
            }

            private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
            {
                try
                {
                    _client
                        .CaptureExceptionAsync(e.Exception, Level.Error)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception)
                {
                    // The finalizer thread must not observe a capture failure.
                }
                finally
                {
                    // The SDK has reported the fault; the process must not be torn
                    // down by a task nobody awaited.
                    e.SetObserved();
                }
            }
        }
    }
}
