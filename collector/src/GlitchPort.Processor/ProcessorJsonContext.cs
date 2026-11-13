using System.Text.Json.Serialization;
using GlitchPort.Processor.Data;

namespace GlitchPort.Processor;

/// <summary>
/// Serialization context for Processor-owned responses. Property names are
/// snake_case so the management API matches the collector wire format.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MessageListItem))]
[JsonSerializable(typeof(List<MessageListItem>))]
[JsonSerializable(typeof(StoredEnvelopesResponse))]
internal sealed partial class ProcessorJsonContext : JsonSerializerContext
{
}

/// <summary>Response body of <c>POST /internal/envelopes</c>.</summary>
internal sealed record StoredEnvelopesResponse(int Stored);
