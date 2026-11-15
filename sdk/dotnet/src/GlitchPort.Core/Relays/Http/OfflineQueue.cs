using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GlitchPort
{
    /// <summary>
    /// A small, crash-safe on-disk FIFO of serialized events used by the HTTP
    /// relay while the network is unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every event is stored in its own
    /// <c>{timestamp}-{event_id}.json</c> file. A sibling temp file is flushed to
    /// disk and then renamed into place, so a reader never observes a partially
    /// written event and a crash never leaves a truncated <c>.json</c> file.
    /// </para>
    /// <para>
    /// The directory is guarded by <see cref="DirectoryLock"/>: a second queue
    /// (in this or in another process) reports <see cref="IsAvailable"/> as
    /// <see langword="false"/> instead of touching the same files. Failures to
    /// create the directory or acquire the lock never throw; they only disable
    /// the queue and record a sanitized <see cref="LastError"/>.
    /// </para>
    /// </remarks>
    internal sealed class OfflineQueue : IDisposable
    {
        private const string JsonExtension = ".json";
        private const string TempInfix = ".tmp-";
        private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
        private const string EventIdFormat = "D";
        private const int TimestampLength = 19;
        private const int EventIdLength = 36;
        private const int MaxCollisionSuffixes = 1000;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly object _gate = new object();
        private readonly int _maxEvents;
        private readonly int _maxAgeHours;

        private string _directory = string.Empty;
        private DirectoryLock? _lock;
        private bool _available;
        private bool _disposed;
        private long _dropped;
        private string? _lastError;

        /// <summary>Creates an offline queue rooted at <paramref name="directory"/>.</summary>
        /// <param name="directory">The directory that holds the queue files. Blank disables the queue.</param>
        /// <param name="maxEvents">The maximum number of queued files; must be at least 1.</param>
        /// <param name="maxAgeHours">The maximum age in hours; 0 disables the age limit.</param>
        /// <exception cref="ArgumentOutOfRangeException">A numeric limit is outside its supported range.</exception>
        public OfflineQueue(string directory, int maxEvents, int maxAgeHours)
        {
            if (maxEvents < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxEvents),
                    maxEvents,
                    "MaxEvents must be at least 1.");
            }

            if (maxAgeHours < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAgeHours),
                    maxAgeHours,
                    "MaxAgeHours must not be negative.");
            }

            _maxEvents = maxEvents;
            _maxAgeHours = maxAgeHours;

            if (string.IsNullOrWhiteSpace(directory))
            {
                _lastError = "The offline queue directory is not configured.";
                return;
            }

            try
            {
                var fullDirectory = Path.GetFullPath(directory);
                Directory.CreateDirectory(fullDirectory);
                _directory = fullDirectory;
                _lock = new DirectoryLock(fullDirectory);
                _available = true;
            }
            catch (Exception ex)
            {
                DisposeLock();
                _directory = string.Empty;
                _available = false;
                _lastError = "The offline queue directory could not be used: " + RelayErrorText.Describe(ex);
            }
        }

        /// <summary>
        /// Gets a value indicating whether the directory exists and the queue
        /// owns its lock. <see langword="false"/> after <see cref="Dispose"/>.
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                lock (_gate)
                {
                    return CanUse();
                }
            }
        }

        /// <summary>Gets the sanitized reason the queue is unavailable, when any.</summary>
        public string? LastError
        {
            get
            {
                lock (_gate)
                {
                    return _lastError;
                }
            }
        }

        /// <summary>Gets the number of event files currently on disk.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return CanUse() ? CollectOwnedFiles().Count : 0;
                }
            }
        }

        /// <summary>Gets the number of event files deleted by the queue limits.</summary>
        public long Dropped
        {
            get
            {
                lock (_gate)
                {
                    return Interlocked.Read(ref _dropped);
                }
            }
        }

        /// <summary>
        /// Atomically writes one event to the queue, then applies the age and
        /// count limits.
        /// </summary>
        /// <param name="json">The exact serialized event JSON.</param>
        /// <param name="eventId">The event id used in the file name.</param>
        /// <param name="timestamp">The event timestamp used in the file name.</param>
        /// <param name="evicted">The number of files deleted by the limits after the write.</param>
        /// <param name="error">A sanitized error when the write failed, otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when the file was written.</returns>
        public bool TryEnqueue(
            string json,
            Guid eventId,
            DateTimeOffset timestamp,
            out int evicted,
            out string? error)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            evicted = 0;
            error = null;

            lock (_gate)
            {
                if (!CanUse())
                {
                    error = _lastError ?? "The offline queue is not available.";
                    return false;
                }

                if (!TryWriteEvent(json, eventId, timestamp, out error))
                {
                    return false;
                }

                evicted = EnforceLimitsLocked();
                return true;
            }
        }

        /// <summary>
        /// Deletes own files older than <c>MaxAgeHours</c> and then the oldest
        /// remaining files until at most <c>MaxEvents</c> files remain.
        /// </summary>
        /// <returns>The number of deleted files, also added to <see cref="Dropped"/>.</returns>
        public int EnforceLimits()
        {
            lock (_gate)
            {
                return EnforceLimitsLocked();
            }
        }

        /// <summary>Reads the oldest queued event without removing it.</summary>
        /// <param name="entry">The oldest readable entry, or <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when an entry was read.</returns>
        public bool TryPeekOldest(out OfflineQueueEntry? entry)
        {
            entry = null;

            lock (_gate)
            {
                if (!CanUse())
                {
                    return false;
                }

                var files = CollectOwnedFiles();
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];

                    string json;
                    try
                    {
                        json = ReadJson(file.Path);
                    }
                    catch (Exception ex) when (IsIoFailure(ex))
                    {
                        // A corrupt or unreadable file must never block the queue:
                        // drop it (when possible) and move on to the next entry.
                        if (TryDelete(file.Path))
                        {
                            Interlocked.Increment(ref _dropped);
                        }

                        continue;
                    }

                    entry = new OfflineQueueEntry(file.Path, file.EventId, file.Timestamp, json);
                    return true;
                }

                return false;
            }
        }

        /// <summary>Deletes an entry returned by <see cref="TryPeekOldest"/>.</summary>
        /// <param name="entry">The entry to remove.</param>
        public void Remove(OfflineQueueEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (_gate)
            {
                if (!CanUse())
                {
                    return;
                }

                TryDelete(entry.Path);
            }
        }

        /// <summary>Releases the directory lock. Later calls become no-ops.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _available = false;
                DisposeLock();
            }
        }

        private bool CanUse()
        {
            return _available && !_disposed;
        }

        private void DisposeLock()
        {
            var directoryLock = _lock;
            _lock = null;
            directoryLock?.Dispose();
        }

        private bool TryWriteEvent(string json, Guid eventId, DateTimeOffset timestamp, out string? error)
        {
            error = null;

            var stem = timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)
                + "-"
                + eventId.ToString(EventIdFormat, CultureInfo.InvariantCulture);

            var tempPath = Path.Combine(
                _directory,
                stem + JsonExtension + TempInfix + Guid.NewGuid().ToString("N"));

            var moved = false;

            try
            {
                var bytes = Utf8NoBom.GetBytes(json);

                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                MoveIntoPlace(tempPath, stem);
                moved = true;
                return true;
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                error = RelayErrorText.Describe(ex);
                _lastError = error;
                return false;
            }
            finally
            {
                if (!moved)
                {
                    TryDelete(tempPath);
                }
            }
        }

        /// <summary>
        /// Renames the flushed temp file to its final name. A collision (the same
        /// event id and millisecond) appends <c>-1</c>, <c>-2</c>, ... before the
        /// extension instead of overwriting the existing event.
        /// </summary>
        private void MoveIntoPlace(string tempPath, string stem)
        {
            for (var suffix = 0; suffix <= MaxCollisionSuffixes; suffix++)
            {
                var fileName = suffix == 0
                    ? stem + JsonExtension
                    : stem + "-" + suffix.ToString(CultureInfo.InvariantCulture) + JsonExtension;

                var targetPath = Path.Combine(_directory, fileName);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                try
                {
                    File.Move(tempPath, targetPath);
                    return;
                }
                catch (IOException) when (File.Exists(targetPath))
                {
                    // Another writer claimed this exact name between the existence
                    // check and the move; try the next suffix.
                }
            }

            throw new IOException(
                "The offline queue could not find a free file name in '" + _directory + "'.");
        }

        private int EnforceLimitsLocked()
        {
            if (!CanUse())
            {
                return 0;
            }

            // Oldest first.
            var files = CollectOwnedFiles();
            var deleted = 0;

            if (_maxAgeHours > 0)
            {
                // An extremely large limit maps to "nothing is too old"; avoid
                // letting AddHours throw when the cutoff would leave the range.
                var cutoff = DateTimeOffset.MinValue;
                try
                {
                    cutoff = DateTimeOffset.UtcNow.AddHours(-(double)_maxAgeHours);
                }
                catch (ArgumentOutOfRangeException)
                {
                }

                for (var i = files.Count - 1; i >= 0; i--)
                {
                    if (files[i].Timestamp >= cutoff)
                    {
                        continue;
                    }

                    if (TryDelete(files[i].Path))
                    {
                        deleted++;
                        files.RemoveAt(i);
                    }
                }
            }

            var index = 0;
            while (files.Count > _maxEvents && index < files.Count)
            {
                if (TryDelete(files[index].Path))
                {
                    deleted++;
                    files.RemoveAt(index);
                }
                else
                {
                    // Leave a locked file alone and try the next oldest one.
                    index++;
                }
            }

            if (deleted > 0)
            {
                Interlocked.Add(ref _dropped, deleted);
            }

            return deleted;
        }

        private List<OwnedFile> CollectOwnedFiles()
        {
            var files = new List<OwnedFile>();

            if (_directory.Length == 0)
            {
                return files;
            }

            string[] paths;
            try
            {
                paths = Directory.GetFiles(_directory);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                return files;
            }

            for (var i = 0; i < paths.Length; i++)
            {
                var name = Path.GetFileName(paths[i]);
                if (!TryParseFileName(name, out var timestamp, out var eventId))
                {
                    continue;
                }

                files.Add(new OwnedFile(paths[i], name, timestamp, eventId));
            }

            files.Sort(CompareByFileName);
            return files;
        }

        /// <summary>
        /// Matches only <c>{yyyyMMddTHHmmssfffZ}-{event-id}[-N].json</c>. The lock
        /// file, temp files and everything else in the directory are ignored.
        /// </summary>
        private static bool TryParseFileName(string name, out DateTimeOffset timestamp, out Guid eventId)
        {
            timestamp = default;
            eventId = default;

            if (name.Length < TimestampLength + 1 + EventIdLength + JsonExtension.Length
                || !name.EndsWith(JsonExtension, StringComparison.Ordinal))
            {
                return false;
            }

            var stemLength = name.Length - JsonExtension.Length;

            if (!DateTimeOffset.TryParseExact(
                name.Substring(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
            {
                return false;
            }

            if (name[TimestampLength] != '-')
            {
                return false;
            }

            var eventIdStart = TimestampLength + 1;
            if (!Guid.TryParseExact(
                name.Substring(eventIdStart, EventIdLength),
                EventIdFormat,
                out eventId))
            {
                return false;
            }

            var suffixStart = eventIdStart + EventIdLength;
            if (suffixStart == stemLength)
            {
                return true;
            }

            if (name[suffixStart] != '-')
            {
                return false;
            }

            var digitsStart = suffixStart + 1;
            if (digitsStart >= stemLength)
            {
                return false;
            }

            for (var i = digitsStart; i < stemLength; i++)
            {
                if (name[i] < '0' || name[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReadJson(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
            {
                return reader.ReadToEnd();
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                return false;
            }
        }

        private static bool IsIoFailure(Exception ex)
        {
            return ex is IOException
                || ex is UnauthorizedAccessException
                || ex is NotSupportedException
                || ex is ArgumentException;
        }

        private static int CompareByFileName(OwnedFile left, OwnedFile right)
        {
            return string.CompareOrdinal(left.Name, right.Name);
        }

        private sealed class OwnedFile
        {
            public OwnedFile(string path, string name, DateTimeOffset timestamp, Guid eventId)
            {
                Path = path;
                Name = name;
                Timestamp = timestamp;
                EventId = eventId;
            }

            public string Path { get; }

            public string Name { get; }

            public DateTimeOffset Timestamp { get; }

            public Guid EventId { get; }
        }
    }
}
