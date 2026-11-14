using System.Text.Json.Serialization;
using GlitchPort.Processor.Data;
using GlitchPort.Processor.Management;

namespace GlitchPort.Processor;

/// <summary>
/// Serialization context for Processor-owned responses. Property names are
/// snake_case so the management API matches the collector wire format.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MessageListItem))]
[JsonSerializable(typeof(List<MessageListItem>))]
[JsonSerializable(typeof(StoredEnvelopesResponse))]
[JsonSerializable(typeof(LabelResponse))]
[JsonSerializable(typeof(List<LabelResponse>))]
[JsonSerializable(typeof(IssueResponse))]
[JsonSerializable(typeof(IssueListResponse))]
[JsonSerializable(typeof(IssueDetailResponse))]
[JsonSerializable(typeof(EventSummaryResponse))]
[JsonSerializable(typeof(EventListResponse))]
[JsonSerializable(typeof(EventDetailResponse))]
[JsonSerializable(typeof(StatsBucketResponse))]
[JsonSerializable(typeof(List<StatsBucketResponse>))]
[JsonSerializable(typeof(UpdateIssueRequest))]
[JsonSerializable(typeof(BulkIssuesRequest))]
[JsonSerializable(typeof(BulkIssuesResponse))]
[JsonSerializable(typeof(CreateLabelRequest))]
[JsonSerializable(typeof(UpdateLabelRequest))]
[JsonSerializable(typeof(ManagementErrorResponse))]
internal sealed partial class ProcessorJsonContext : JsonSerializerContext
{
}

/// <summary>Response body of <c>POST /internal/envelopes</c>.</summary>
internal sealed record StoredEnvelopesResponse(int Stored);
