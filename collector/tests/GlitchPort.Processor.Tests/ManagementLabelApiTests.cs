using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementLabelApiTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public ManagementLabelApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Labels_can_be_listed_created_updated_and_deleted()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        using (var empty = await api.GetAsync($"/api/manage/projects/{projectId}/labels"))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Empty((await ManagementApiData.ReadJsonAsync(empty)).EnumerateArray());
        }

        long regressionId;
        using (var created = await api.PostAsync(
                   $"/api/manage/projects/{projectId}/labels",
                   """{"name":"regression"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var label = await ManagementApiData.ReadJsonAsync(created);
            regressionId = label.GetProperty("id").GetInt64();
            Assert.True(regressionId > 0);
            Assert.Equal("regression", label.GetProperty("name").GetString());
            Assert.Equal("#888888", label.GetProperty("color").GetString());
        }

        long crashId;
        using (var created = await api.PostAsync(
                   $"/api/manage/projects/{projectId}/labels",
                   """{"name":"crash","color":"#ff0000"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var label = await ManagementApiData.ReadJsonAsync(created);
            crashId = label.GetProperty("id").GetInt64();
            Assert.Equal("#ff0000", label.GetProperty("color").GetString());
        }

        using (var listed = await api.GetAsync($"/api/manage/projects/{projectId}/labels"))
        {
            var labels = (await ManagementApiData.ReadJsonAsync(listed)).EnumerateArray().ToList();
            Assert.Equal(
                new[] { "crash", "regression" },
                labels.Select(label => label.GetProperty("name").GetString()!));
        }

        using (var duplicate = await api.PostAsync(
                   $"/api/manage/projects/{projectId}/labels",
                   """{"name":"crash"}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var error = await ManagementApiData.ReadJsonAsync(duplicate);
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
        }

        using (var updated = await api.PatchAsync(
                   $"/api/manage/labels/{regressionId}",
                   """{"name":"bug","color":"#00ff00"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var label = await ManagementApiData.ReadJsonAsync(updated);
            Assert.Equal(regressionId, label.GetProperty("id").GetInt64());
            Assert.Equal("bug", label.GetProperty("name").GetString());
            Assert.Equal("#00ff00", label.GetProperty("color").GetString());
        }

        using (var conflict = await api.PatchAsync($"/api/manage/labels/{crashId}", """{"name":"bug"}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        using (var colourOnly = await api.PatchAsync($"/api/manage/labels/{crashId}", """{"color":"#0000ff"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, colourOnly.StatusCode);
            var label = await ManagementApiData.ReadJsonAsync(colourOnly);
            Assert.Equal("crash", label.GetProperty("name").GetString());
            Assert.Equal("#0000ff", label.GetProperty("color").GetString());
        }

        using (var deleted = await api.DeleteAsync($"/api/manage/labels/{crashId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var listed = await api.GetAsync($"/api/manage/projects/{projectId}/labels"))
        {
            var labels = (await ManagementApiData.ReadJsonAsync(listed)).EnumerateArray().ToList();
            Assert.Equal(
                new[] { "bug" },
                labels.Select(label => label.GetProperty("name").GetString()!));
        }

        using (var again = await api.DeleteAsync($"/api/manage/labels/{crashId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        using (var unknown = await api.PatchAsync("/api/manage/labels/9000000000", """{"name":"x"}"""))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    [InlineData("""{"name":"ok","color":""}""")]
    [InlineData("""{not json""")]
    public async Task Invalid_label_bodies_are_rejected(string body)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.PostAsync($"/api/manage/projects/{TestData.NextProjectId()}/labels", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_label_removes_its_issue_assignments()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "label-cascade");
        var issueId = await IssueIdAsync(api, projectId);

        long labelId;
        using (var created = await api.PostAsync(
                   $"/api/manage/projects/{projectId}/labels",
                   """{"name":"assigned"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            labelId = (await ManagementApiData.ReadJsonAsync(created)).GetProperty("id").GetInt64();
        }

        using (var patched = await api.PatchAsync($"/api/manage/issues/{issueId}", """{"labels":["assigned"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        }

        Assert.Equal(
            1,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select count(*) from issue_labels where label_id = {labelId}"));

        using (var deleted = await api.DeleteAsync($"/api/manage/labels/{labelId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Equal(
            0,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select count(*) from issue_labels where label_id = {labelId}"));

        using var detail = await api.GetAsync($"/api/manage/issues/{issueId}");
        var issue = await ManagementApiData.ReadJsonAsync(detail);
        Assert.Empty(issue.GetProperty("labels").EnumerateArray());
    }

    [Fact]
    public async Task Labels_are_unique_per_project()
    {
        using var api = new ManagementApiClient(_fixture);
        var firstProject = TestData.NextProjectId();
        var secondProject = TestData.NextProjectId();

        using (var created = await api.PostAsync(
                   $"/api/manage/projects/{firstProject}/labels",
                   """{"name":"shared"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var otherProject = await api.PostAsync(
                   $"/api/manage/projects/{secondProject}/labels",
                   """{"name":"shared"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, otherProject.StatusCode);
        }

        using var duplicate = await api.PostAsync(
            $"/api/manage/projects/{firstProject}/labels",
            """{"name":"shared","color":"#123456"}""");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    private async Task<long> IssueIdAsync(ManagementApiClient api, long projectId)
    {
        var page = await ManagementApiData.GetIssuesAsync(api, projectId);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        return Assert.Single(items).GetProperty("id").GetInt64();
    }
}
