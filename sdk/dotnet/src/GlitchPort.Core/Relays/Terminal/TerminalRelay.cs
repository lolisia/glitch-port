using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GlitchPort
{
    /// <summary>
    /// Starts <c>glitchport-terminal</c> as a child process on the first captured
    /// event and streams the line protocol to its standard input. The child is
    /// finished at most once: a broken pipe disables the relay instead of starting
    /// a second renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol (standard input, UTF-8 without BOM, one JSON object per line):
    /// <code>
    /// {"type":"hello","protocol":1,"parent_pid":N,"app":"..."}
    /// {"type":"event","payload":{...}}
    /// </code>
    /// </para>
    /// <para>
    /// The relay state lock is never held while writing to the child's stdin or
    /// while starting/killing the child. <see cref="ProcessId"/> and
    /// <see cref="ExecutablePath"/> are lock-free snapshots, so they cannot block
    /// behind a pipe write.
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> is idempotent and bounded: the queued relay waits at
    /// most about one second for its worker, and the child cleanup is limited to
    /// roughly two seconds (EOF grace period, kill and a bounded wait for the
    /// stdin writer to close). A renderer that stops reading stdin therefore
    /// cannot hang <see cref="GlitchPortClient.Dispose"/> or process exit.
    /// </para>
    /// <para>
    /// On Windows the child is assigned to a job object limited with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so it dies with the parent
    /// process even when the parent crashes.
    /// </para>
    /// </remarks>
    public sealed class TerminalRelay : QueuedRelay, IDisposable
    {
        private const string TypeName = "terminal";
        private const string DefaultName = "terminal";
        private const string DisabledMessage = "The terminal relay is disabled.";

        /// <summary>
        /// How long to give the child to exit after its stdin was closed (EOF)
        /// before it is killed.
        /// </summary>
        private const int ChildGracefulShutdownMilliseconds = 750;

        /// <summary>How long to wait for the process after it was killed.</summary>
        private const int ChildKillTimeoutMilliseconds = 500;

        /// <summary>
        /// How long <see cref="ReleaseChild"/> waits for the writer close helper
        /// before it decides that a pipe write is in flight and kills the child.
        /// </summary>
        private const int WriterCloseGraceMilliseconds = 200;

        /// <summary>
        /// How long to wait for the writer to close after the child was killed.
        /// Killing the child breaks the pipe and unblocks an in-flight write, so
        /// the close normally finishes immediately.
        /// </summary>
        private const int WriterCloseAfterKillMilliseconds = 500;

        /// <summary>
        /// How long a concurrent <see cref="ShutdownChild"/> caller waits for the
        /// thread that is already releasing the child. Slightly longer than the
        /// worst-case cleanup below, but still bounded.
        /// </summary>
        private const int ConcurrentShutdownWaitMilliseconds = 2500;

        private readonly TerminalRelayOptions _options;

        /// <summary>Guards the child fields and the shutdown flags. Never held during pipe I/O.</summary>
        private readonly object _childGate = new object();

        /// <summary>Serializes hello/event writes and the closing of the stdin writer.</summary>
        private readonly object _writeGate = new object();

        /// <summary>Makes sure at most one child is started.</summary>
        private readonly object _startGate = new object();

        private readonly ManualResetEventSlim _shutdownCompleted = new ManualResetEventSlim(false);
        private readonly int _parentProcessId;

        private Process? _process;
        private StreamWriter? _writer;
        private IntPtr _jobHandle;
        private string? _executablePath;
        private int _processId = -1;
        private string? _failureText;
        private bool _shutdown;

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
        /// <remarks>
        /// The getter is a lock-free snapshot: it never waits for a write to the
        /// child's stdin, even when the pipe is full.
        /// </remarks>
        public int? ProcessId
        {
            get
            {
                var processId = Volatile.Read(ref _processId);
                return processId < 0 ? (int?)null : processId;
            }
        }

        /// <summary>
        /// Gets the renderer executable resolved at construction (or on the first
        /// start attempt), or <see langword="null"/> when no candidate exists.
        /// </summary>
        /// <remarks>
        /// The getter is a lock-free snapshot: it never waits for a write to the
        /// child's stdin, even when the pipe is full.
        /// </remarks>
        public string? ExecutablePath => Volatile.Read(ref _executablePath);

        /// <summary>
        /// Flushes the queue, closes the child's standard input (which makes the
        /// renderer exit by itself), waits less than a second and then kills it.
        /// The method is idempotent and returns within a bounded budget.
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
            if (Volatile.Read(ref _shutdown) || IsDisabled)
            {
                MarkFailed(_failureText ?? DisabledMessage);
                return;
            }

            var writer = GetWriter();

            if (writer is null)
            {
                MarkFailed(_failureText ?? DisabledMessage);
                return;
            }

            string? writeError = null;

            try
            {
                WriteProtocolLine(writer, BuildEventLine(e.Json));
            }
            catch (Exception ex)
            {
                writeError = "The terminal process is no longer accepting events: "
                    + RelayErrorText.Describe(ex);
            }

            if (writeError is null)
            {
                MarkDelivered();
                return;
            }

            // The child is gone (or its window was closed). Disable the relay and
            // release the process; never start a replacement.
            _failureText = writeError;
            Disable(writeError);
            ShutdownChild();
            MarkFailed(writeError);
        }

        /// <summary>
        /// Returns the writer for this event, starting the child if necessary.
        /// Never called with the state lock held.
        /// </summary>
        private StreamWriter? GetWriter()
        {
            lock (_childGate)
            {
                if (Volatile.Read(ref _shutdown))
                {
                    return null;
                }

                if (_writer is not null)
                {
                    return _writer;
                }
            }

            return TryStartChild();
        }

        /// <summary>
        /// Starts the child and publishes it. The hello line is written after the
        /// state lock has been released, so a full stdin pipe can never block the
        /// relay state or the accessors.
        /// </summary>
        private StreamWriter? TryStartChild()
        {
            lock (_startGate)
            {
                lock (_childGate)
                {
                    if (Volatile.Read(ref _shutdown))
                    {
                        return null;
                    }

                    if (_writer is not null)
                    {
                        return _writer;
                    }
                }

                var executablePath = TerminalProcessLocator.Resolve(_options);
                Volatile.Write(ref _executablePath, executablePath);

                if (executablePath is null)
                {
                    var notFound = "glitchport-terminal was not found. Searched: "
                        + string.Join("; ", TerminalProcessLocator.GetSearchLocations(_options));

                    _failureText = notFound;
                    Disable(notFound);
                    return null;
                }

                var appName = ResolveAppName();
                Process? process = null;
                StreamWriter? streamWriter = null;
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

                    streamWriter = new StreamWriter(
                        process.StandardInput.BaseStream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true,
                        NewLine = "\n",
                    };
                }
                catch (Exception ex)
                {
                    // Covers Process.Start failures (missing loader, invalid image,
                    // PlatformNotSupportedException, ...). Nothing escapes the relay.
                    var startError = "The terminal process could not be started from '"
                        + executablePath
                        + "': "
                        + RelayErrorText.Describe(ex);

                    _failureText = startError;
                    Disable(startError);
                    CleanUpFailedStart(process, streamWriter, jobHandle);
                    return null;
                }

                StreamWriter publishedWriter;
                var abortStart = false;

                lock (_childGate)
                {
                    if (Volatile.Read(ref _shutdown))
                    {
                        // Dispose won the race with the start. Publish nothing and
                        // release the child outside the lock below.
                        abortStart = true;
                        publishedWriter = null!;
                    }
                    else
                    {
                        publishedWriter = streamWriter;
                        _process = process;
                        _writer = streamWriter;
                        _jobHandle = jobHandle;
                        Volatile.Write(ref _processId, process!.Id);

                        // Ownership moved to the relay; do not clean up in the catch.
                        process = null;
                        streamWriter = null;
                        jobHandle = IntPtr.Zero;
                    }
                }

                if (abortStart)
                {
                    CleanUpFailedStart(process, streamWriter, jobHandle);
                    return null;
                }

                try
                {
                    // The hello line is protocol data, so it is written through the
                    // same serialized (and checked) path as event lines.
                    WriteProtocolLine(publishedWriter, BuildHelloLine(appName));
                    return publishedWriter;
                }
                catch (Exception ex)
                {
                    var helloError = "The terminal process is no longer accepting events: "
                        + RelayErrorText.Describe(ex);

                    _failureText = helloError;
                    Disable(helloError);
                    ShutdownChild();
                    return null;
                }
            }
        }

        /// <summary>
        /// Writes one protocol line. The write gate serializes protocol writes with
        /// the shutdown code that closes the writer, so a write that blocks on a
        /// full pipe can always be interrupted by killing the child.
        /// </summary>
        private void WriteProtocolLine(StreamWriter writer, string line)
        {
            lock (_writeGate)
            {
                if (Volatile.Read(ref _shutdown))
                {
                    throw new ObjectDisposedException(nameof(TerminalRelay));
                }

                writer.WriteLine(line);
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
                KillAndWait(process);

                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }

            WindowsJobObject.Close(jobHandle);
        }

        /// <summary>
        /// Releases the child at most once. Concurrent callers wait for the first
        /// caller, but only for a bounded amount of time.
        /// </summary>
        private void ShutdownChild()
        {
            Process? process;
            StreamWriter? writer;
            IntPtr jobHandle;
            bool alreadyShuttingDown;

            lock (_childGate)
            {
                alreadyShuttingDown = Volatile.Read(ref _shutdown);

                if (alreadyShuttingDown)
                {
                    process = null;
                    writer = null;
                    jobHandle = IntPtr.Zero;
                }
                else
                {
                    Volatile.Write(ref _shutdown, true);
                    process = _process;
                    writer = _writer;
                    jobHandle = _jobHandle;

                    _process = null;
                    _writer = null;
                    _jobHandle = IntPtr.Zero;
                }
            }

            if (alreadyShuttingDown)
            {
                // Another thread (normally a Dispose racing with the worker after a
                // broken pipe) owns the cleanup. Wait so Dispose does not return
                // while the child is still alive, but never indefinitely.
                _shutdownCompleted.Wait(ConcurrentShutdownWaitMilliseconds);
                return;
            }

            try
            {
                ReleaseChild(process, writer, jobHandle);
            }
            finally
            {
                _shutdownCompleted.Set();
            }
        }

        /// <summary>
        /// Closes the stdin writer, kills the child if it does not exit, and never
        /// blocks indefinitely on a pipe write that is in flight.
        /// </summary>
        private void ReleaseChild(Process? process, StreamWriter? writer, IntPtr jobHandle)
        {
            var writerClosed = new ManualResetEventSlim(false);

            if (writer is not null)
            {
                // The relay worker may be blocked inside WriteLine because the
                // renderer stopped reading. Closing the writer from a dedicated
                // background thread keeps this method bounded; killing the child
                // below breaks the pipe and lets the write (and the close) finish.
                var closer = new Thread(() =>
                {
                    try
                    {
                        lock (_writeGate)
                        {
                            writer.Dispose();
                        }
                    }
                    catch (Exception)
                    {
                        // A broken pipe is the expected shutdown path.
                    }
                    finally
                    {
                        writerClosed.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "glitchport terminal stdin close",
                };

                closer.Start();
            }
            else
            {
                writerClosed.Set();
            }

            try
            {
                // A reading renderer sees EOF, prints the remaining events and
                // exits within the grace period.
                var closed = writerClosed.Wait(WriterCloseGraceMilliseconds);

                if (process is not null)
                {
                    var exited = TryWaitForExit(
                        process,
                        closed ? ChildGracefulShutdownMilliseconds : 0);

                    if (!exited)
                    {
                        KillAndWait(process);
                    }
                }

                if (!closed)
                {
                    // The child is gone, so the in-flight write must fail now. Give
                    // the close helper a bounded chance to finish; if the OS still
                    // does not release the thread, Dispose returns anyway (the
                    // helper is a background thread).
                    writerClosed.Wait(WriterCloseAfterKillMilliseconds);
                }
            }
            finally
            {
                WindowsJobObject.Close(jobHandle);

                if (process is not null)
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
        }

        private static void KillAndWait(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // The process may have exited (or become inaccessible) just now.
            }

            TryWaitForExit(process, ChildKillTimeoutMilliseconds);
        }

        private static bool TryWaitForExit(Process process, int milliseconds)
        {
            try
            {
                return process.WaitForExit(milliseconds);
            }
            catch (Exception)
            {
                // Waiting on an inaccessible process must not break Dispose; report
                // "not exited" so the caller still tries to kill it.
                return false;
            }
        }
    }
}
