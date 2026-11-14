using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Starts <c>glitchport-terminal</c> as a child process on the first captured
    /// event and streams the line protocol to its standard input. The child is
    /// finished at most once: a broken pipe disables the relay instead of starting
    /// a second renderer.
    /// </summary>
    /// <remarks>
    /// Protocol (standard input, UTF-8 without BOM, one JSON object per line):
    /// <code>
    /// {"type":"hello","protocol":1,"parent_pid":N,"app":"..."}
    /// {"type":"event","payload":{...}}
    /// </code>
    /// On Windows the child is assigned to a job object limited with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so it dies with the parent
    /// process even when the parent crashes.
    /// </remarks>
    public sealed class TerminalRelay : QueuedRelay, IDisposable
    {
        private const string TypeName = "terminal";
        private const string DefaultName = "terminal";
        private const string DisabledMessage = "The terminal relay is disabled.";
        private const int ChildShutdownTimeoutMilliseconds = 2000;

        private readonly TerminalRelayOptions _options;
        private readonly object _childGate = new object();
        private readonly int _parentProcessId;

        private Process? _process;
        private StreamWriter? _writer;
        private IntPtr _jobHandle;
        private string? _executablePath;
        private int? _processId;
        private string? _failureText;
        private bool _shutdown;
        private bool _shutdownCompleted;

        /// <summary>Creates a terminal relay. The child process starts on the first event.</summary>
        /// <param name="options">The relay options.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public TerminalRelay(TerminalRelayOptions options)
            : base(
                options is null ? DefaultName : options.Name ?? DefaultName,
                TypeName,
                options is null ? Level.Debug : options.MinLevel,
                options is null ? 1000 : options.QueueCapacity)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _parentProcessId = TryGetCurrentProcessId();

            // Resolution is cheap (a few File.Exists probes) and does not start the
            // child; exposing the path lets callers and tests inspect it up front.
            // Starting still happens lazily on the first event.
            _executablePath = TerminalProcessLocator.Resolve(options);
        }

        /// <summary>
        /// Gets the process id of the child renderer, or <see langword="null"/>
        /// until it has been started.
        /// </summary>
        public int? ProcessId
        {
            get
            {
                lock (_childGate)
                {
                    return _processId;
                }
            }
        }

        /// <summary>
        /// Gets the renderer executable resolved at construction, or
        /// <see langword="null"/> when no candidate exists.
        /// </summary>
        public string? ExecutablePath
        {
            get
            {
                lock (_childGate)
                {
                    return _executablePath;
                }
            }
        }

        /// <summary>
        /// Flushes the queue, closes the child's standard input (which makes the
        /// renderer exit by itself), waits up to two seconds and then kills it.
        /// </summary>
        /// <remarks>
        /// <see cref="QueuedRelay.Dispose"/> is not virtual. Hiding it and
        /// re-implementing <see cref="IDisposable"/> makes sure the child is also
        /// released when the relay is reached through <see cref="IRelay"/> (the
        /// path used by <see cref="GlitchPortClient"/> and <see cref="RelayFanout"/>).
        /// </remarks>
        public new void Dispose()
        {
            try
            {
                base.Dispose();
            }
            finally
            {
                ShutdownChild();
                GC.SuppressFinalize(this);
            }
        }

        void IDisposable.Dispose()
        {
            Dispose();
        }

        /// <inheritdoc/>
        protected override void Handle(CapturedEvent e)
        {
            string? writeError = null;

            lock (_childGate)
            {
                if (_shutdown || IsDisabled)
                {
                    MarkFailed(_failureText ?? DisabledMessage);
                    return;
                }

                if (_writer is null && !TryStartLocked(out var startError))
                {
                    MarkFailed(startError ?? DisabledMessage);
                    return;
                }

                try
                {
                    _writer!.WriteLine(BuildEventLine(e.Json));
                }
                catch (Exception ex)
                {
                    writeError = "The terminal process is no longer accepting events: "
                        + RelayErrorText.Describe(ex);
                    _failureText = writeError;
                }
            }

            if (writeError is null)
            {
                MarkDelivered();
                return;
            }

            // The child is gone (or its window was closed). Disable the relay and
            // release the process; never start a replacement.
            Disable(writeError);
            ShutdownChild();
            MarkFailed(writeError);
        }

        /// <summary>Starts the child and writes the hello line. Called under the child gate.</summary>
        private bool TryStartLocked(out string? error)
        {
            var executablePath = TerminalProcessLocator.Resolve(_options);
            _executablePath = executablePath;

            if (executablePath is null)
            {
                error = "glitchport-terminal was not found. Searched: "
                    + string.Join("; ", TerminalProcessLocator.GetSearchLocations(_options));

                _failureText = error;
                Disable(error);
                return false;
            }

            var appName = ResolveAppName();
            Process? process = null;
            StreamWriter? writer = null;
            var jobHandle = IntPtr.Zero;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = BuildArguments(appName),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = false,
                };

                process = new Process { StartInfo = startInfo };

                if (!process.Start())
                {
                    throw new InvalidOperationException("The terminal process did not start.");
                }

                jobHandle = WindowsJobObject.TryCreate(process);

                writer = new StreamWriter(
                    process.StandardInput.BaseStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };

                writer.WriteLine(BuildHelloLine(appName));

                _process = process;
                _writer = writer;
                _jobHandle = jobHandle;
                _processId = process.Id;

                // Ownership moved to the relay; do not clean up in the catch.
                process = null;
                writer = null;
                jobHandle = IntPtr.Zero;

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                // Covers Process.Start failures (missing loader, invalid image,
                // PlatformNotSupportedException, ...). Nothing escapes the relay.
                error = "The terminal process could not be started from '"
                    + executablePath
                    + "': "
                    + RelayErrorText.Describe(ex);

                _failureText = error;
                Disable(error);
                CleanUpFailedStart(process, writer, jobHandle);
                return false;
            }
        }

        private string BuildArguments(string appName)
        {
            var builder = new StringBuilder(96);

            builder.Append("--parent-pid ");
            builder.Append(_parentProcessId.ToString(CultureInfo.InvariantCulture));
            builder.Append(" --format ");
            builder.Append(GetFormatName(_options.Format));
            builder.Append(" --window ");
            builder.Append(GetWindowName(_options.Window));
            builder.Append(" --app ");
            AppendQuotedArgument(builder, appName);

            return builder.ToString();
        }

        private string BuildHelloLine(string appName)
        {
            var builder = new StringBuilder(96);

            builder.Append("{\"type\":\"hello\",\"protocol\":1,\"parent_pid\":");
            builder.Append(_parentProcessId.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"app\":");
            JsonPayloadWriter.AppendString(builder, appName);
            builder.Append('}');

            return builder.ToString();
        }

        private static string BuildEventLine(string payloadJson)
        {
            var builder = new StringBuilder(payloadJson.Length + 32);

            builder.Append("{\"type\":\"event\",\"payload\":");
            builder.Append(payloadJson);
            builder.Append('}');

            return builder.ToString();
        }

        private string ResolveAppName()
        {
            var configured = _options.AppName;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured!;
            }

            return GetCurrentProcessName();
        }

        private static string GetCurrentProcessName()
        {
            try
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    return currentProcess.ProcessName;
                }
            }
            catch (Exception)
            {
                return "glitchport";
            }
        }

        private static int TryGetCurrentProcessId()
        {
            try
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    return currentProcess.Id;
                }
            }
            catch (Exception)
            {
                // Some restricted platforms do not expose process information;
                // starting the child will fail there and disable the relay.
                return 0;
            }
        }

        private static string GetFormatName(TerminalFormat format)
        {
            switch (format)
            {
                case TerminalFormat.Compact:
                    return "compact";
                case TerminalFormat.Json:
                    return "json";
                case TerminalFormat.Pretty:
                default:
                    return "pretty";
            }
        }

        private static string GetWindowName(TerminalWindow window)
        {
            return window == TerminalWindow.New ? "new" : "inherit";
        }

        /// <summary>
        /// Appends one argument using the Windows command line quoting rules (the
        /// same algorithm .NET uses to split <see cref="ProcessStartInfo.Arguments"/>
        /// on every platform).
        /// </summary>
        private static void AppendQuotedArgument(StringBuilder builder, string value)
        {
            builder.Append('"');

            var backslashes = 0;

            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(character);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
        }

        private static void CleanUpFailedStart(Process? process, StreamWriter? writer, IntPtr jobHandle)
        {
            if (writer is not null)
            {
                try
                {
                    writer.Dispose();
                }
                catch (Exception)
                {
                    // The pipe is already broken; nothing to flush.
                }
            }

            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(ChildShutdownTimeoutMilliseconds);
                    }
                }
                catch (Exception)
                {
                    // The process may have exited between the checks.
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception)
                    {
                        // Best effort.
                    }
                }
            }

            WindowsJobObject.Close(jobHandle);
        }

        private void ShutdownChild()
        {
            Process? process;
            StreamWriter? writer;
            IntPtr jobHandle;

            lock (_childGate)
            {
                if (_shutdown)
                {
                    // Another thread (normally the worker after a broken pipe) is
                    // already releasing the child; wait for it so Dispose never
                    // returns while the process is still alive.
                    while (!_shutdownCompleted)
                    {
                        Monitor.Wait(_childGate);
                    }

                    return;
                }

                _shutdown = true;
                process = _process;
                writer = _writer;
                jobHandle = _jobHandle;
                _process = null;
                _writer = null;
                _jobHandle = IntPtr.Zero;
            }

            try
            {
                if (writer is not null)
                {
                    // EOF makes glitchport-terminal print the remaining events and exit.
                    try
                    {
                        writer.Dispose();
                    }
                    catch (Exception)
                    {
                        // A broken pipe is the expected shutdown path.
                    }
                }

                if (process is not null)
                {
                    try
                    {
                        if (!process.WaitForExit(ChildShutdownTimeoutMilliseconds))
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch (Exception)
                            {
                                // The process may have exited just after the wait.
                            }

                            try
                            {
                                process.WaitForExit(ChildShutdownTimeoutMilliseconds);
                            }
                            catch (Exception)
                            {
                                // Nothing useful can be done with a process that cannot be waited on.
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Waiting on an inaccessible process must not break Dispose.
                    }
                    finally
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch (Exception)
                        {
                            // Best effort.
                        }
                    }
                }

                WindowsJobObject.Close(jobHandle);
            }
            finally
            {
                lock (_childGate)
                {
                    _shutdownCompleted = true;
                    Monitor.PulseAll(_childGate);
                }
            }
        }
    }
}
