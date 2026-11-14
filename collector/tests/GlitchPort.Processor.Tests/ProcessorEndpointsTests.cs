using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ProcessorEndpointsTests
{
    private const string InternalHeader = TestData.InternalHeader;

    private readonly ProcessorFixture _fixture;

    public ProcessorEndpointsTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Healthz_returns_ok()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Internal_envelopes_stores_a_batch_once_per_event_id()
    {
        using var client = _fixture.CreateClient();

        var first = TestData.CreateEnvelope(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
            "first");
        var second = TestData.CreateEnvelope(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 3, 1, 12, 0, 1, TimeSpan.Zero),
            "second");
        Envelope[] batch = [first, second];

        using var initialResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            batch);

        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        Assert.Equal(2, await TestData.ReadStoredAsync(initialResponse));
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, first.EventId));

        using var duplicateResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            batch);

        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.Equal(0, await TestData.ReadStoredAsync(duplicateResponse));
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, first.EventId));
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, second.EventId));
    }

    [Fact]
    public async Task Internal_envelopes_accepts_envelopes_without_optional_fields()
    {
        using var client = _fixture.CreateClient();
        var envelope = new Envelope
        {
            EventId = Guid.NewGuid(),
            ProjectId = 99,
            ReceivedAt = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.FromHours(9)),
            Endpoint = "POST /api/1/messages",
            Message = "minimal envelope",
        };

        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TestData.ReadStoredAsync(response));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    public async Task Internal_envelopes_rejects_a_wrong_secret(string? secret)
    {
        using var client = _fixture.CreateClient();
        var envelope = TestData.CreateEnvelope(Guid.NewGuid(), DateTimeOffset.UtcNow, "must not store");

        using var response = await TestData.PostEnvelopesAsync(client, secret, [envelope]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, envelope.EventId));
    }

    [Fact]
    public async Task Internal_envelopes_rejects_malformed_json()
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/envelopes")
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(InternalHeader, ProcessorApplicationFactory.InternalSharedSecret);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Manage_messages_orders_by_received_at_descending_and_honours_limit_one()
    {
        await DeleteStoredMessagesAsync();

        using var client = _fixture.CreateClient();

        var older = TestData.CreateEnvelope(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "older");
        var newer = TestData.CreateEnvelope(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            "newer");

        using var postResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [older, newer]);
        Assert.Equal(2, await TestData.ReadStoredAsync(postResponse));

        using var request = TestData.CreateAuthorizedGet("/api/manage/messages?limit=1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());

        var item = root[0];
        Assert.Equal(
            ["id", "received_at", "endpoint", "message"],
            item.EnumerateObject().Select(property => property.Name));
        Assert.Equal(newer.EventId, item.GetProperty("id").GetGuid());
        Assert.Equal(newer.Endpoint, item.GetProperty("endpoint").GetString());
        Assert.Equal("newer", item.GetProperty("message").GetString());

        var receivedAt = item.GetProperty("received_at").GetString();
        Assert.NotNull(receivedAt);
        Assert.EndsWith("+00:00", receivedAt);
        Assert.Equal(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.Parse(receivedAt, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Manage_messages_returns_events_unioned_with_legacy_messages()
    {
        await DeleteStoredMessagesAsync();

        using var client = _fixture.CreateClient();

        var projectId = TestData.NextProjectId();
        var legacyId = Guid.NewGuid();
        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into messages (id, project_id, received_at, endpoint, message)
            values ('{legacyId}', {projectId}, '2026-01-01T00:00:00Z', 'POST /api/1/messages', 'legacy row')
            """);

        var eventId = Guid.NewGuid();
        var payload = TestData.CreateExceptionPayload(level: "info");
        var envelope = TestData.CreatePayloadEnvelope(
            eventId,
            projectId,
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            payload);

        using var postResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);
        Assert.Equal(1, await TestData.ReadStoredAsync(postResponse));

        using var request = TestData.CreateAuthorizedGet("/api/manage/messages?limit=10");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement;

        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(eventId, items[0].GetProperty("id").GetGuid());
        Assert.Equal("NullReferenceException: Object reference not set to an instance of an object", items[0].GetProperty("message").GetString());
        Assert.Equal(legacyId, items[1].GetProperty("id").GetGuid());
        Assert.Equal("legacy row", items[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Manage_messages_defaults_to_limit_100()
    {
        await DeleteStoredMessagesAsync();

        using var client = _fixture.CreateClient();

        var envelopes = Enumerable
            .Range(0, 101)
            .Select(index => TestData.CreateEnvelope(
                Guid.NewGuid(),
                new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                $"message-{index}"))
            .ToArray();

        using var postResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            envelopes);
        Assert.Equal(101, await TestData.ReadStoredAsync(postResponse));
        Assert.Equal(101, await TestDatabase.CountEventsAsync(
            _fixture.ConnectionString,
            envelopes[0].ProjectId));

        using var defaultRequest = TestData.CreateAuthorizedGet("/api/manage/messages");
        using var defaultResponse = await client.SendAsync(defaultRequest);

        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);

        using var defaultDocument = JsonDocument.Parse(await defaultResponse.Content.ReadAsStringAsync());
        var defaultItems = defaultDocument.RootElement;

        Assert.Equal(100, defaultItems.GetArrayLength());
        Assert.Equal("message-100", defaultItems[0].GetProperty("message").GetString());
        Assert.Equal("message-1", defaultItems[99].GetProperty("message").GetString());

        using var allRequest = TestData.CreateAuthorizedGet("/api/manage/messages?limit=500");
        using var allResponse = await client.SendAsync(allRequest);

        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);

        using var allDocument = JsonDocument.Parse(await allResponse.Content.ReadAsStringAsync());
        Assert.Equal(101, allDocument.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("501")]
    [InlineData("not-a-number")]
    public async Task Manage_messages_rejects_an_out_of_range_limit(string limit)
    {
        using var client = _fixture.CreateClient();
        using var request = TestData.CreateAuthorizedGet($"/api/manage/messages?limit={limit}");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong-token")]
    [InlineData("dev-admin-token")]
    public async Task Manage_messages_rejects_a_missing_or_wrong_admin_token(string? authorization)
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/manage/messages");

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cors_preflight_allows_the_dashboard_origin()
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/manage/messages");
        request.Headers.TryAddWithoutValidation("Origin", ProcessorApplicationFactory.DashboardOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            ProcessorApplicationFactory.DashboardOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(
            "GET",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")).Split(',').Select(value => value.Trim()));
    }

    private Task DeleteStoredMessagesAsync() => TestDatabase.ExecuteAsync(
        _fixture.ConnectionString,
        "delete from events; delete from messages");
}
