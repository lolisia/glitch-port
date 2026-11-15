using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GlitchPort
{
    /// <summary>
    /// Keeps an exclusive lock on an offline queue directory for the lifetime of
    /// the instance.
    /// </summary>
    /// <remarks>
    /// The lock is an OS file handle opened with <see cref="FileShare.None"/>, so
    /// a second queue (in this or in another process) fails to open it instead
    /// of using the same files. The lock file is deliberately not deleted on
    /// <see cref="Dispose"/>: a leftover file from a crashed process is not held
    /// by the OS and is simply reused (its stale content is truncated and
    /// replaced with the current process id).
    /// </remarks>
    internal sealed class DirectoryLock : IDisposable
    {
        /// <summary>The name of the lock file inside the queue directory.</summary>
        internal const string LockFileName = ".glitchport-offline.lock";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private FileStream? _stream;

        /// <summary>Acquires the lock for <paramref name="directory"/>.</summary>
        /// <param name="directory">The directory that contains the lock file.</param>
        /// <exception cref="IOException">The lock is held by another process.</exception>
        /// <exception cref="UnauthorizedAccessException">The lock file cannot be opened.</exception>
        public DirectoryLock(string directory)
        {
            if (directory is null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            var path = Path.Combine(directory, LockFileName);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            try
            {
                stream.SetLength(0);

                var processId = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                var bytes = Utf8NoBom.GetBytes(processId);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                _stream = stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>Releases the lock. Safe to call more than once.</summary>
        public void Dispose()
        {
            var stream = _stream;
            _stream = null;
            stream?.Dispose();
        }
    }
}
