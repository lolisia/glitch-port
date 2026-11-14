using System.Buffers;
using System.IO.Compression;

namespace GlitchPort.Relay;

/// <summary>Outcome of reading an ingest request body.</summary>
internal enum BodyReadStatus
{
    /// <summary>The body was read successfully.</summary>
    Success,

    /// <summary>The decompressed body exceeds the configured limit.</summary>
    TooLarge,

    /// <summary>The request declares an unsupported content encoding.</summary>
    UnsupportedEncoding,

    /// <summary>The request declares gzip but the body is not valid gzip data.</summary>
    InvalidGzip,
}

/// <summary>The result of reading an ingest request body.</summary>
internal readonly record struct BodyReadResult(BodyReadStatus Status, byte[]? Content)
{
    /// <summary>True when the body was read successfully.</summary>
    public bool IsSuccess => Status == BodyReadStatus.Success;

    /// <summary>Creates a successful result.</summary>
    public static BodyReadResult Success(byte[] content) => new(BodyReadStatus.Success, content);

    /// <summary>Creates a failed result.</summary>
    public static BodyReadResult Failure(BodyReadStatus status) => new(status, null);
}

/// <summary>
/// Reads an ingest request body, decoding gzip when requested and enforcing the
/// decompressed size limit while streaming so a decompression bomb cannot
/// exhaust Relay memory.
/// </summary>
internal static class RequestBodyReader
{
    private const int ReadBufferSize = 16 * 1024;

    /// <summary>Reads and optionally decompresses the request body.</summary>
    public static async Task<BodyReadResult> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var contentEncoding = request.Headers.ContentEncoding.ToString();
        if (!string.IsNullOrWhiteSpace(contentEncoding)
            && !string.Equals(
                contentEncoding.Trim(),
                RelayConstants.GzipEncoding,
                StringComparison.OrdinalIgnoreCase))
        {
            return BodyReadResult.Failure(BodyReadStatus.UnsupportedEncoding);
        }

        GZipStream? gzip = null;
        try
        {
            Stream source = request.Body;
            if (!string.IsNullOrWhiteSpace(contentEncoding))
            {
                gzip = new GZipStream(request.Body, CompressionMode.Decompress, leaveOpen: true);
                source = gzip;
            }

            return await ReadLimitedAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return BodyReadResult.Failure(BodyReadStatus.InvalidGzip);
        }
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<BodyReadResult> ReadLimitedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        try
        {
            while (true)
            {
                var read = await source
                    .ReadAsync(chunk.AsMemory(0, ReadBufferSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > RelayConstants.MaxRequestBodyBytes)
                {
                    return BodyReadResult.Failure(BodyReadStatus.TooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return BodyReadResult.Success(buffer.ToArray());
    }
}
