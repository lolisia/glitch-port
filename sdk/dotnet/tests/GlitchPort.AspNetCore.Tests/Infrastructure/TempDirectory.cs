using System.Text;

namespace GlitchPort.AspNetCore.Tests.Infrastructure;

/// <summary>
/// A unique, disposable directory for the <c>file:</c> relay used by the tests.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "glitchport-aspnetcore-tests",
            Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file may still be held by a relay that is shutting down; the
            // operating system cleans the temporary directory up later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Builds and reads the relay configuration used by the tests.</summary>
internal static class TestRelay
{
    /// <summary>Creates a <c>file:</c> relay DSN that writes NDJSON into <paramref name="directory"/>.</summary>
    public static string CreateDsn(string directory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var builder = new StringBuilder("file:?dir=");
        builder.Append(Uri.EscapeDataString(directory));
        builder.Append("&format=ndjson&flush_interval_ms=10&min_level=debug");
        if (!string.IsNullOrEmpty(name))
        {
            builder.Append("&name=");
            builder.Append(Uri.EscapeDataString(name));
        }

        return builder.ToString();
    }

    /// <summary>Reads every NDJSON event in <paramref name="directory"/>.</summary>
    public static IReadOnlyList<System.Text.Json.Nodes.JsonObject> Read(string directory)
    {
        var events = new List<System.Text.Json.Nodes.JsonObject>();

        if (!Directory.Exists(directory))
        {
            return events;
        }

        var files = Directory.EnumerateFiles(directory, "*.ndjson").ToArray();
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (System.Text.Json.Nodes.JsonNode.Parse(line) is System.Text.Json.Nodes.JsonObject captured)
                {
                    events.Add(captured);
                }
            }
        }

        return events;
    }
}
