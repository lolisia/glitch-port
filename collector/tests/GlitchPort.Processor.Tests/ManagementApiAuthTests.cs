using System.Net;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementApiAuthTests
{
    private const string EventId = "9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e";

    private readonly ProcessorFixture _fixture;

    public ManagementApiAuthTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/manage/projects/1/issues" },
        { "GET", "/api/manage/issues/1" },
        { "PATCH", "/api/manage/issues/1" },
        { "DELETE", "/api/manage/issues/1" },
        { "POST", "/api/manage/issues/bulk" },
        { "GET", "/api/manage/issues/1/events" },
        { "GET", $"/api/manage/events/{EventId}" },
        { "DELETE", $"/api/manage/events/{EventId}" },
        { "GET", "/api/manage/projects/1/labels" },
        { "POST", "/api/manage/projects/1/labels" },
        { "PATCH", "/api/manage/labels/1" },
        { "DELETE", "/api/manage/labels/1" },
        { "GET", "/api/manage/projects/1/stats" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_endpoint_requires_a_bearer_token(string method, string path)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.SendAsync(new HttpMethod(method), path, BodyFor(method), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_endpoint_rejects_a_wrong_bearer_token(string method, string path)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.SendAsync(new HttpMethod(method), path, BodyFor(method), "wrong-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer")]
    [InlineData("Basic dGVzdDp0ZXN0")]
    [InlineData("dev-admin-token")]
    public async Task A_malformed_authorization_header_is_rejected(string authorization)
    {
        using var api = new ManagementApiClient(_fixture);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/manage/projects/1/issues");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await api.Raw.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_is_accepted()
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.GetAsync("/api/manage/projects/1/issues");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string? BodyFor(string method) => method is "POST" or "PATCH" ? "{}" : null;
}
