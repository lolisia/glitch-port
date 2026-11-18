using System.Text.Json.Serialization;
using GlitchPort.Processor.Alerts;
using GlitchPort.Processor.Data;
using GlitchPort.Processor.Endpoints;
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
[JsonSerializable(typeof(PlatformStatsResponse))]
[JsonSerializable(typeof(ManagementStatsResponse))]
[JsonSerializable(typeof(UpdateIssueRequest))]
[JsonSerializable(typeof(BulkIssuesRequest))]
[JsonSerializable(typeof(BulkIssuesResponse))]
[JsonSerializable(typeof(CreateLabelRequest))]
[JsonSerializable(typeof(UpdateLabelRequest))]
[JsonSerializable(typeof(ManagementErrorResponse))]
[JsonSerializable(typeof(RetentionStatusResponse))]
[JsonSerializable(typeof(ProjectListItemResponse))]
[JsonSerializable(typeof(List<ProjectListItemResponse>))]
[JsonSerializable(typeof(ProjectDetailResponse))]
[JsonSerializable(typeof(InboundFiltersResponse))]
[JsonSerializable(typeof(CreatedProjectResponse))]
[JsonSerializable(typeof(ProjectKeyResponse))]
[JsonSerializable(typeof(List<ProjectKeyResponse>))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(UpdateProjectRequest))]
[JsonSerializable(typeof(CreateProjectKeyRequest))]
[JsonSerializable(typeof(UpdateProjectKeyRequest))]
[JsonSerializable(typeof(ProjectConfigResponse))]
[JsonSerializable(typeof(ProjectConfigProject))]
[JsonSerializable(typeof(List<ProjectConfigProject>))]
[JsonSerializable(typeof(ProjectConfigInboundFilters))]
[JsonSerializable(typeof(AlertPayload))]
[JsonSerializable(typeof(AlertRuleResponse))]
[JsonSerializable(typeof(List<AlertRuleResponse>))]
[JsonSerializable(typeof(AlertRuleListResponse))]
[JsonSerializable(typeof(CreateAlertRuleRequest))]
[JsonSerializable(typeof(UpdateAlertRuleRequest))]
[JsonSerializable(typeof(AlertTestResponse))]
[JsonSerializable(typeof(AlertNotificationResponse))]
[JsonSerializable(typeof(List<AlertNotificationResponse>))]
[JsonSerializable(typeof(AlertNotificationListResponse))]
[JsonSerializable(typeof(ReleaseResponse))]
[JsonSerializable(typeof(ReleaseListResponse))]
[JsonSerializable(typeof(TagValueResponse))]
[JsonSerializable(typeof(TagFacetResponse))]
[JsonSerializable(typeof(IssueTagsResponse))]
internal sealed partial class ProcessorJsonContext : JsonSerializerContext
{
}

/// <summary>Response body of <c>POST /internal/envelopes</c>.</summary>
internal sealed record StoredEnvelopesResponse(int Stored);
