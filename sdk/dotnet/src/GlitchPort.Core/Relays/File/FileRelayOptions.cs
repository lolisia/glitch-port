namespace GlitchPort
{
    /// <summary>The on-disk representation of the events a file relay writes.</summary>
    public enum FileFormat
    {
        /// <summary>One JSON document per line (newline-delimited JSON).</summary>
        Ndjson,

        /// <summary>Human-readable text, one formatted event per line.</summary>
        Text,
    }

    /// <summary>Options for the file relay.</summary>
    public sealed class FileRelayOptions : RelayOptions
    {
        /// <summary>
        /// Gets or sets the directory that receives the log files. Relative paths
        /// are resolved against the current working directory when the relay is
        /// created. Defaults to <c>logs/glitchport</c>.
        /// </summary>
        public string Directory { get; set; } = "logs/glitchport";

        /// <summary>Gets or sets the file format. Defaults to <see cref="FileFormat.Ndjson"/>.</summary>
        public FileFormat Format { get; set; } = FileFormat.Ndjson;

        /// <summary>
        /// Gets or sets the size at which a new file is started. Defaults to 10 MB.
        /// A value of zero or less starts a new file for every event.
        /// </summary>
        public int MaxFileSizeMb { get; set; } = 10;

        /// <summary>
        /// Gets or sets the age in days after which log files are deleted when the
        /// relay starts. Zero disables age based retention. Defaults to 7 days.
        /// </summary>
        public int MaxAgeDays { get; set; } = 7;

        /// <summary>
        /// Gets or sets the total size in megabytes that log files may occupy.
        /// Older files are deleted first. Zero disables the limit. Defaults to 200 MB.
        /// </summary>
        public int MaxTotalSizeMb { get; set; } = 200;

        /// <summary>
        /// Gets or sets the maximum number of log files that are kept. Older files
        /// are deleted first. Zero disables the limit. Defaults to 100 files.
        /// </summary>
        public int MaxFiles { get; set; } = 100;

        /// <summary>
        /// Gets or sets how often, in milliseconds, buffered writes are flushed to
        /// disk while events are being logged. Defaults to 1000 ms.
        /// </summary>
        public int FlushIntervalMs { get; set; } = 1000;
    }
}
