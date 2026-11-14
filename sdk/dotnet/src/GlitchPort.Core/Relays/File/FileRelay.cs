using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GlitchPort
{
    /// <summary>
    /// Writes captured events to rotating local log files. Every relay instance
    /// owns exactly one file at a time and only appends to files it created
    /// itself, so no cross-process locking is required.
    /// </summary>
    /// <remarks>
    /// Events are buffered by the underlying <see cref="FileStream"/> and flushed
    /// on a timer, immediately after an <see cref="Level.Error"/> or
    /// <see cref="Level.Fatal"/> event, and whenever the relay is flushed or
    /// disposed. Any I/O failure disables the relay: later events are counted as
    /// failed by <see cref="QueuedRelay"/> instead of throwing at the capture site.
    /// </remarks>
    public sealed class FileRelay : QueuedRelay, IRelay
    {
        private const string TypeName = "file";
        private const string DefaultName = "file";
        private const string FileNamePrefix = "glitchport-";
        private const string NdjsonExtension = ".ndjson";
        private const string TextExtension = ".log";
        private const string TimestampFormat = "yyyyMMdd-HHmmss";
        private const int MaxNameSuffixes = 1000;

        // 0600 decimal: the owner may read and write the file, nobody else can.
        private const int OwnerReadWriteMode = 384;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static readonly byte[] LineFeed = { (byte)'\n' };
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool IsLinuxOrMacOs =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private readonly object _gate = new object();
        private readonly FileRelayOptions _options;
        private readonly string _directory;
        private readonly long _maxFileBytes;

        private Timer? _flushTimer;
        private FileStream? _stream;
        private string? _activePath;
        private DateTime _fileDate;
        private long _bytesWritten;
        private bool _closedForGood;
        private bool _retentionDone;
        private string? _lastErrorText;

        /// <summary>Creates a file relay.</summary>
        /// <param name="options">The relay options.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public FileRelay(FileRelayOptions options)
            : base(
                options is null ? DefaultName : options.Name ?? DefaultName,
                TypeName,
                options is null ? Level.Debug : options.MinLevel,
                options is null ? 1000 : options.QueueCapacity)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;
            _maxFileBytes = options.MaxFileSizeMb * 1024L * 1024L;
            _directory = string.Empty;

            try
            {
                // Resolve once so later rotation and retention never depend on a
                // changed current working directory. Creating the directory here
                // also surfaces misconfiguration before the first captured event.
                var directory = Path.GetFullPath(options.Directory);
                Directory.CreateDirectory(directory);
                _directory = directory;
            }
            catch (Exception ex)
            {
                // A relay that cannot write must not break client construction.
                Disable("The file relay directory could not be used: " + RelayErrorText.Describe(ex));
            }
        }

        /// <summary>
        /// Releases the relay: queued events are drained, buffered writes are
        /// flushed and the active file handle is closed.
        /// </summary>
        /// <remarks>
        /// The base <see cref="QueuedRelay.Dispose"/> is not virtual, so
        /// <see cref="IDisposable.Dispose"/> is re-implemented to run this cleanup
        /// for every dispatch path (a <c>using</c> statement, an
        /// <see cref="IRelay"/> reference or a directly typed variable).
        /// </remarks>
        public new void Dispose()
        {
            DisposeCore();
        }

        void IDisposable.Dispose()
        {
            DisposeCore();
        }

        private void DisposeCore()
        {
            lock (_gate)
            {
                DisposeFlushTimerLocked();
            }

            // Drains the queue and flushes; a late worker that did not finish
            // within the base timeout is stopped from reopening the file below.
            base.Dispose();

            lock (_gate)
            {
                _closedForGood = true;

                try
                {
                    CloseStreamLocked();
                }
                catch (Exception)
                {
                    // Closing a log file must never throw during shutdown.
                }
            }
        }

        /// <inheritdoc/>
        protected override void Handle(CapturedEvent e)
        {
            try
            {
                lock (_gate)
                {
                    HandleLocked(e);
                }
            }
            catch (Exception ex) when (IsIoException(ex))
            {
                FailIo(ex);
            }
        }

        /// <inheritdoc/>
        protected override void FlushBuffer()
        {
            try
            {
                lock (_gate)
                {
                    FlushLocked();

                    if (!IsDisabled)
                    {
                        RunRetentionOnceLocked();
                    }
                }
            }
            catch (Exception ex) when (IsIoException(ex))
            {
                FailIo(ex);
            }
        }

        private void HandleLocked(CapturedEvent e)
        {
            if (IsDisabled || _closedForGood)
            {
                MarkFailed(_lastErrorText ?? "The file relay is disabled.");
                return;
            }

            var localNow = DateTime.Now;

            if (_stream is null)
            {
                // A flush closes the handle so the data is freely readable; the
                // next event continues the same file unless it has to rotate.
                if (_activePath is not null)
                {
                    if (localNow.Date == _fileDate && _bytesWritten <= _maxFileBytes)
                    {
                        _stream = OpenForAppendLocked(_activePath);
                    }
                    else
                    {
                        _activePath = null;
                    }
                }

                if (_stream is null)
                {
                    OpenLocked(localNow);
                }
            }
            else if (localNow.Date != _fileDate || _bytesWritten > _maxFileBytes)
            {
                RotateLocked(localNow);
            }

            WriteLocked(e);

            // Housekeeping runs once on the worker thread, right after the relay
            // created (and wrote to) its first file. It can therefore safely skip
            // the file that is currently being written.
            RunRetentionOnceLocked();

            if (e.Level >= Level.Error)
            {
                FlushLocked();
            }
            else
            {
                EnsureFlushTimerLocked();
            }

            MarkDelivered();
        }

        private void OpenLocked(DateTime localNow)
        {
            var timestamp = localNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var pid = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            var extension = _options.Format == FileFormat.Text ? TextExtension : NdjsonExtension;
            var baseName = FileNamePrefix + timestamp + "-" + pid;

            for (var suffix = 0; suffix < MaxNameSuffixes; suffix++)
            {
                var fileName = suffix == 0
                    ? baseName + extension
                    : baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture) + extension;

                var path = Path.Combine(_directory, fileName);
                if (File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var stream = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);

                    RestrictToOwner(path);

                    _stream = stream;
                    _activePath = path;
                    _fileDate = localNow.Date;
                    _bytesWritten = 0;
                    return;
                }
                catch (IOException)
                {
                    // Another writer may have claimed this exact name between the
                    // existence check and CreateNew; try the next suffix.
                    if (File.Exists(path))
                    {
                        continue;
                    }

                    throw;
                }
            }

            throw new IOException(
                "The file relay could not find a free file name in '" + _directory + "'.");
        }

        private void RotateLocked(DateTime localNow)
        {
            CloseStreamLocked();
            OpenLocked(localNow);
        }

        private FileStream OpenForAppendLocked(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            RestrictToOwner(path);
            return stream;
        }

        private void WriteLocked(CapturedEvent e)
        {
            var line = _options.Format == FileFormat.Text
                ? EventFormatter.FormatCompact(e, color: false)
                : e.Json;

            var bytes = Utf8NoBom.GetBytes(line);
            var stream = _stream!;

            stream.Write(bytes, 0, bytes.Length);
            stream.Write(LineFeed, 0, LineFeed.Length);
            _bytesWritten += bytes.Length + LineFeed.Length;
        }

        private void CloseStreamLocked()
        {
            var stream = _stream;
            _stream = null;
            _activePath = null;
            stream?.Dispose();
        }

        private void FlushLocked()
        {
            // The pending timer is only needed while writes are buffered; a caller
            // driven flush (or the timer itself) makes it obsolete.
            DisposeFlushTimerLocked();

            var stream = _stream;
            if (stream is null)
            {
                return;
            }

            // Closing after the flush releases the handle, so the content can be
            // read by any tool (on Windows a reader may not open a file that is
            // still held for writing). The next event appends to the same file.
            _stream = null;

            try
            {
                stream.Flush();
            }
            finally
            {
                stream.Dispose();
            }
        }

        private void EnsureFlushTimerLocked()
        {
            if (_flushTimer is not null)
            {
                return;
            }

            var interval = _options.FlushIntervalMs;
            if (interval <= 0)
            {
                FlushLocked();
                return;
            }

            _flushTimer = new Timer(OnFlushTimer, null, interval, Timeout.Infinite);
        }

        private void DisposeFlushTimerLocked()
        {
            var timer = _flushTimer;
            _flushTimer = null;
            timer?.Dispose();
        }

        private void OnFlushTimer(object? state)
        {
            if (IsDisabled)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_stream is not null && !IsDisabled)
                    {
                        FlushLocked();
                    }
                }
            }
            catch (Exception ex) when (IsIoException(ex))
            {
                FailIo(ex);
            }
        }

        private void FailIo(Exception ex)
        {
            var error = RelayErrorText.Describe(ex);
            _lastErrorText = error;

            lock (_gate)
            {
                DisposeFlushTimerLocked();

                try
                {
                    CloseStreamLocked();
                }
                catch (Exception)
                {
                    // The relay is already being disabled; a failing close adds nothing.
                }
            }

            MarkFailed(error);
            Disable(error);
        }

        /// <summary>
        /// Runs retention at most once per relay, on the worker thread. It is
        /// triggered by the first event and by an explicit flush, whichever comes
        /// first, so an idle relay that is only flushed still applies the limits.
        /// </summary>
        private void RunRetentionOnceLocked()
        {
            if (_retentionDone)
            {
                return;
            }

            _retentionDone = true;
            RunRetentionLocked();
        }

        /// <summary>
        /// Deletes old log files once. Runs on the worker thread with
        /// <see cref="_gate"/> held and never throws: retention problems must not
        /// affect event delivery.
        /// </summary>
        private void RunRetentionLocked()
        {
            try
            {
                var files = CollectOwnedFiles();
                DeleteFilesOlderThanMaxAge(files);
                DeleteOldestFilesUntilWithinLimits(files);
            }
            catch (Exception)
            {
                // A vanished or unreadable directory simply skips retention.
            }
        }

        private List<FileInfo> CollectOwnedFiles()
        {
            var files = new List<FileInfo>();

            if (string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory))
            {
                return files;
            }

            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                if (!IsOwnedFileName(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    files.Add(new FileInfo(path));
                }
                catch (Exception)
                {
                    // The file vanished between enumeration and inspection.
                }
            }

            return files;
        }

        private void DeleteFilesOlderThanMaxAge(List<FileInfo> files)
        {
            if (_options.MaxAgeDays <= 0)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(_options.MaxAgeDays));

            for (var i = files.Count - 1; i >= 0; i--)
            {
                if (IsActiveFile(files[i]))
                {
                    continue;
                }

                if (GetLastWriteTimeUtc(files[i]) < cutoff && TryDelete(files[i]))
                {
                    files.RemoveAt(i);
                }
            }
        }

        private void DeleteOldestFilesUntilWithinLimits(List<FileInfo> files)
        {
            var maxFiles = _options.MaxFiles;
            var maxTotalBytes = _options.MaxTotalSizeMb > 0
                ? _options.MaxTotalSizeMb * 1024L * 1024L
                : long.MaxValue;

            if (maxFiles <= 0 && maxTotalBytes == long.MaxValue)
            {
                return;
            }

            // Oldest first, so the loop always removes the least useful files.
            files.Sort(CompareByLastWriteTime);

            var totalBytes = 0L;
            for (var i = 0; i < files.Count; i++)
            {
                totalBytes += GetSize(files[i]);
            }

            var index = 0;
            while (index < files.Count
                && ((maxFiles > 0 && files.Count > maxFiles) || totalBytes > maxTotalBytes))
            {
                var file = files[index];
                var size = GetSize(file);

                if (IsActiveFile(file))
                {
                    // The file being written is part of the total but is never
                    // removed; the limit is simply reported as unsatisfiable once
                    // it is the only candidate left.
                    index++;
                    continue;
                }

                if (TryDelete(file))
                {
                    totalBytes -= size;
                    files.RemoveAt(index);
                }
                else
                {
                    // Leave unreadable or locked files alone and try the next one.
                    index++;
                }
            }
        }

        private bool IsActiveFile(FileInfo file)
        {
            return _activePath is not null && PathEquals(file.FullName, _activePath);
        }

        /// <summary>
        /// Matches only <c>glitchport-*-*.ndjson</c> and <c>glitchport-*-*.log</c>.
        /// Everything else in the directory belongs to someone else.
        /// </summary>
        private static bool IsOwnedFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(FileNamePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string stem;
            if (name.EndsWith(NdjsonExtension, StringComparison.Ordinal))
            {
                stem = name.Substring(0, name.Length - NdjsonExtension.Length);
            }
            else if (name.EndsWith(TextExtension, StringComparison.Ordinal))
            {
                stem = name.Substring(0, name.Length - TextExtension.Length);
            }
            else
            {
                return false;
            }

            // The wildcard separator between timestamp and pid must be present.
            return stem.IndexOf('-', FileNamePrefix.Length) >= 0;
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(
                left,
                right,
                IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static int CompareByLastWriteTime(FileInfo left, FileInfo right)
        {
            var result = GetLastWriteTimeUtc(left).CompareTo(GetLastWriteTimeUtc(right));
            return result != 0
                ? result
                : string.CompareOrdinal(left.FullName, right.FullName);
        }

        private static DateTime GetLastWriteTimeUtc(FileInfo file)
        {
            try
            {
                return file.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private long GetSize(FileInfo file)
        {
            // The active file may still have buffered bytes that are not reflected
            // in its on-disk length yet; count what will eventually be written.
            return IsActiveFile(file) ? _bytesWritten : GetLength(file);
        }

        private static long GetLength(FileInfo file)
        {
            try
            {
                return file.Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static bool TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsIoException(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        private static void RestrictToOwner(string path)
        {
            if (!IsLinuxOrMacOs)
            {
                return;
            }

            try
            {
                // The return value is deliberately ignored: a platform that cannot
                // tighten the mode must not stop the relay from logging.
                chmod(path, OwnerReadWriteMode);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string pathname, int mode);
    }
}
