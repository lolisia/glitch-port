using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlitchPort.Relay.Config;
using GlitchPort.Relay.Filtering;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class InboundFilterTests
{
    private const string FilteredHeader = "X-GlitchPort-Filtered";

    [Fact]
    public async Task Old_config_without_inbound_filters_does_not_filter()
    {
        var publisher = new RecordingEnvelopePublisher();

        // The seeded handler serializes a project config without the
        // inbound_filters field, exactly like an older Processor.
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            EventJson(message: "boom", release: "release-1.0.0", environment: "prod"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(response.Headers.Contains(FilteredHeader));
        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task Release_filter_match_returns_accepted_with_the_header_and_does_not_publish()
    {
        var publisher = new RecordingEnvelopePublisher();
        var eventId = Guid.NewGuid();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(releases: ["release-*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            EventJson(eventId, message: "boom", release: "release-1.2.3", environment: "prod"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("release", FilteredReason(response));
        Assert.Empty(publisher.Envelopes);

        var body = await RelayTestClient.ReadJsonAsync(response);
        Assert.Equal(eventId, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Environment_filter_match_returns_accepted_with_the_header_and_does_not_publish()
    {
        var publisher = new RecordingEnvelopePublisher();
        var eventId = Guid.NewGuid();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(environments: ["prod*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            EventJson(eventId, message: "boom", release: "release-1.0.0", environment: "production"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("environment", FilteredReason(response));
        Assert.Empty(publisher.Envelopes);

        var body = await RelayTestClient.ReadJsonAsync(response);
        Assert.Equal(eventId, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Message_filter_match_returns_accepted_with_the_header_and_does_not_publish()
    {
        var publisher = new RecordingEnvelopePublisher();
        var eventId = Guid.NewGuid();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(messagePatterns: ["*timeout*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            EventJson(eventId, message: "The connection hit a Timeout", release: "release-1.0.0"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("message", FilteredReason(response));
        Assert.Empty(publisher.Envelopes);

        var body = await RelayTestClient.ReadJsonAsync(response);
        Assert.Equal(eventId, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Exception_filter_match_returns_accepted_with_the_header_and_does_not_publish()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(messagePatterns: ["*NullReferenceException*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            EventJson(message: null, exception: ("NullReferenceException", "Object reference not set")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("message", FilteredReason(response));
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task No_match_publishes_normally_without_the_header()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(
                releases: ["2.0.*"],
                environments: ["staging"],
                messagePatterns: ["*outage*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            EventJson(message: "boom", release: "1.0.0", environment: "prod"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(response.Headers.Contains(FilteredHeader));
        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task Matching_is_case_insensitive_for_every_filter_kind()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(
                releases: ["RELEASE-*"],
                environments: ["PROD"],
                messagePatterns: ["*TIMEOUT*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using (var release = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "release-1.0.0")))
        {
            Assert.Equal(HttpStatusCode.Accepted, release.StatusCode);
            Assert.Equal("release", FilteredReason(release));
        }

        using (var environment = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", environment: "prod")))
        {
            Assert.Equal(HttpStatusCode.Accepted, environment.StatusCode);
            Assert.Equal("environment", FilteredReason(environment));
        }

        using (var message = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "A timeout happened")))
        {
            Assert.Equal(HttpStatusCode.Accepted, message.StatusCode);
            Assert.Equal("message", FilteredReason(message));
        }

        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task A_star_matches_any_string_at_the_start_middle_and_end()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(releases: ["*nightly*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using (var start = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "nightly-42")))
        {
            Assert.Equal("release", FilteredReason(start));
        }

        using (var middle = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "build-nightly-42-hotfix")))
        {
            Assert.Equal("release", FilteredReason(middle));
        }

        using (var end = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "build-nightly")))
        {
            Assert.Equal("release", FilteredReason(end));
        }

        using (var noMatch = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "build-42")))
        {
            Assert.Equal(HttpStatusCode.Accepted, noMatch.StatusCode);
            Assert.False(noMatch.Headers.Contains(FilteredHeader));
        }

        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task Null_values_never_match_a_filter()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(releases: ["*"], environments: ["*"]));
        using var client = await factory.CreateLoadedClientAsync();

        // Neither release nor environment is present, so the wildcards must
        // not drop the event.
        using (var withoutValues = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom")))
        {
            Assert.Equal(HttpStatusCode.Accepted, withoutValues.StatusCode);
            Assert.False(withoutValues.Headers.Contains(FilteredHeader));
        }

        using (var withRelease = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "1.0.0")))
        {
            Assert.Equal(HttpStatusCode.Accepted, withRelease.StatusCode);
            Assert.Equal("release", FilteredReason(withRelease));
        }

        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task Release_takes_precedence_over_environment_and_message()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(
                releases: ["1.0.0"],
                environments: ["prod"],
                messagePatterns: ["*boom*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using (var allMatch = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "1.0.0", environment: "prod")))
        {
            Assert.Equal("release", FilteredReason(allMatch));
        }

        using (var environmentAndMessage = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "2.0.0", environment: "prod")))
        {
            Assert.Equal("environment", FilteredReason(environmentAndMessage));
        }

        using (var messageOnly = await RelayTestClient.PostEventAsync(
                   client,
                   EventJson(message: "boom", release: "2.0.0", environment: "staging")))
        {
            Assert.Equal("message", FilteredReason(messageOnly));
        }

        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task Legacy_messages_apply_only_the_message_filter()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            FilteredHandler(
                releases: ["*"],
                environments: ["*"],
                messagePatterns: ["*boom*"]));
        using var client = await factory.CreateLoadedClientAsync();

        using (var matching = await RelayTestClient.PostMessageAsync(
                   client,
                   """{"message":"boom"}"""))
        {
            Assert.Equal(HttpStatusCode.Accepted, matching.StatusCode);
            Assert.Equal("message", FilteredReason(matching));
        }

        using (var notMatching = await RelayTestClient.PostMessageAsync(
                   client,
                   """{"message":"still running"}"""))
        {
            Assert.Equal(HttpStatusCode.Accepted, notMatching.StatusCode);
            Assert.False(notMatching.Headers.Contains(FilteredHeader));
        }

        Assert.Single(publisher.Envelopes);
    }

    [Theory]
    [InlineData("*timeout", "connection timeout", true)]
    [InlineData("conn*timeout", "connection timeout", true)]
    [InlineData("timeout*", "timeout occurred", true)]
    [InlineData("timeout*", "connection timed out", false)]
    [InlineData("*", "anything", true)]
    [InlineData("*", "", true)]
    [InlineData("Fixed", "FIXED", true)]
    [InlineData("a?b", "a?b", true)]
    [InlineData("a?b", "axb", false)]
    [InlineData("a.b", "axb", false)]
    [InlineData("a.b", "a.b", true)]
    public void Glob_matching_is_anchored_case_insensitive_and_literal(string glob, string value, bool expected)
    {
        var filter = new InboundFilter([], [], [glob]);

        Assert.Equal(expected, filter.MatchesMessage(value));
    }

    [Fact]
    public void A_null_value_never_matches()
    {
        var filter = new InboundFilter(["*"], ["*"], ["*"]);

        Assert.False(filter.MatchesRelease(null));
        Assert.False(filter.MatchesEnvironment(null));
        Assert.False(filter.MatchesMessage(null));

        // A non-null value does match the wildcard, so the checks above are
        // about null, not about a disabled filter.
        Assert.True(filter.MatchesMessage("anything"));
        Assert.True(filter.MatchesException("System.Exception", "boom"));
    }

    [Fact]
    public void The_snapshot_compiles_the_filters_once()
    {
        var snapshot = new ProjectConfigSnapshot(
            "v1",
            new Dictionary<long, IReadOnlyDictionary<string, int?>>
            {
                [1] = new Dictionary<string, int?> { ["key"] = null },
            },
            new Dictionary<long, InboundFilterConfig>
            {
                [1] = new InboundFilterConfig(["1.*"], [], []),
            });

        Assert.True(snapshot.TryGetInboundFilters(1, out var filters));
        Assert.True(filters.MatchesRelease("1.2.3"));
        Assert.False(filters.MatchesRelease("2.0.0"));

        // A project without filters (the old-config case) reports none.
        Assert.False(snapshot.TryGetInboundFilters(2, out _));

        var unfiltered = ProjectConfigTestData.Snapshot("v1", (1, "key", null));
        Assert.False(unfiltered.TryGetInboundFilters(1, out _));
    }

    private static FakeProjectConfigHandler FilteredHandler(
        IReadOnlyList<string>? releases = null,
        IReadOnlyList<string>? environments = null,
        IReadOnlyList<string>? messagePatterns = null)
    {
        var filters = new Dictionary<long, InboundFilterConfig>
        {
            [RelayTestClient.ProjectId] = new(
                releases ?? [],
                environments ?? [],
                messagePatterns ?? []),
        };

        return FakeProjectConfigHandler.ForConfig(
            "filtered",
            filters,
            (RelayTestClient.ProjectId, RelayTestClient.ValidKey, null));
    }

    private static string FilteredReason(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(FilteredHeader, out var values));
        return Assert.Single(values!);
    }

    private static string EventJson(
        Guid? eventId = null,
        string? message = "boom",
        string? release = null,
        string? environment = null,
        (string Type, string? Value)? exception = null)
    {
        var body = new JsonObject { ["platform"] = "unity" };

        if (eventId is not null)
        {
            body["event_id"] = eventId.Value.ToString("D");
        }

        if (message is not null)
        {
            body["message"] = message;
        }

        if (release is not null)
        {
            body["release"] = release;
        }

        if (environment is not null)
        {
            body["environment"] = environment;
        }

        if (exception is { } exceptionValue)
        {
            body["exception"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = exceptionValue.Type,
                    ["value"] = exceptionValue.Value,
                });
        }

        return body.ToJsonString();
    }
}
