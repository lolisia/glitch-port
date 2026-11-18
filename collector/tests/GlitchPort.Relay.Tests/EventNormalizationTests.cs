using System.Text.Json;
using System.Net;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class EventNormalizationTests
{
    [Fact]
    public async Task PostEvent_TruncatesEveryOverLengthString()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var json = JsonSerializer.Serialize(new
        {
            message = new string('m', 9000),
            platform = new string('p', 40),
            logger = new string('l', 200),
            release = new string('r', 300),
            environment = new string('e', 100),
            server_name = new string('s', 300),
            exception = new[]
            {
                new
                {
                    type = new string('t', 300),
                    value = new string('v', 9000),
                    module = new string('o', 300),
                    stacktrace = new
                    {
                        frames = new[]
                        {
                            new
                            {
                                function = new string('f', 2000),
                                module = new string('g', 2000),
                                filename = new string('h', 2000),
                                abs_path = new string('i', 2000),
                                context_line = new string('j', 2000),
                            },
                        },
                    },
                },
            },
            tags = new Dictionary<string, string>
            {
                [new string('k', 40)] = new string('y', 300),
            },
            fingerprint = new[] { new string('z', 300) },
        });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        Assert.Equal(new string('m', 8192), payload.GetProperty("message").GetString());
        Assert.Equal(new string('p', 32), payload.GetProperty("platform").GetString());
        Assert.Equal(new string('l', 128), payload.GetProperty("logger").GetString());
        Assert.Equal(new string('r', 200), payload.GetProperty("release").GetString());
        Assert.Equal(new string('e', 64), payload.GetProperty("environment").GetString());
        Assert.Equal(new string('s', 200), payload.GetProperty("server_name").GetString());

        var exception = payload.GetProperty("exception")[0];
        Assert.Equal(new string('t', 200), exception.GetProperty("type").GetString());
        Assert.Equal(new string('v', 8192), exception.GetProperty("value").GetString());
        Assert.Equal(new string('o', 200), exception.GetProperty("module").GetString());

        var frame = exception.GetProperty("stacktrace").GetProperty("frames")[0];
        Assert.Equal(new string('f', 1024), frame.GetProperty("function").GetString());
        Assert.Equal(new string('g', 1024), frame.GetProperty("module").GetString());
        Assert.Equal(new string('h', 1024), frame.GetProperty("filename").GetString());
        Assert.Equal(new string('i', 1024), frame.GetProperty("abs_path").GetString());
        Assert.Equal(new string('j', 1024), frame.GetProperty("context_line").GetString());

        var tags = payload.GetProperty("tags");
        Assert.True(tags.TryGetProperty(new string('k', 32), out var tagValue));
        Assert.Equal(new string('y', 200), tagValue.GetString());

        Assert.Equal(
            new string('z', 200),
            payload.GetProperty("fingerprint")[0].GetString());
    }

    [Fact]
    public async Task PostEvent_TrimsEveryStringButKeepsInternalWhitespace()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        const string Json = """
            {
              "message": "  boom  ",
              "platform": " unity ",
              "logger": " Game.Server ",
              "release": " game@1.0.0 ",
              "environment": " production ",
              "server_name": " server-01 ",
              "exception": [
                {
                  "type": " System.Exception ",
                  "value": "  a  b  ",
                  "module": " Game ",
                  "stacktrace": { "frames": [
                    {
                      "function": " Foo.Bar ",
                      "module": " Game ",
                      "filename": " Foo.cs ",
                      "abs_path": " /src/Foo.cs ",
                      "context_line": "  call();  "
                    }
                  ] }
                }
              ],
              "tags": { " region ": " eu-west-1 " },
              "fingerprint": [ " checkout " ],
              "user": { "id": " player-1 ", "username": " foo ", "email": " foo@example.com " },
              "breadcrumbs": [
                {
                  "timestamp": " 2026-11-14T11:59:58.000Z ",
                  "category": " scene ",
                  "message": " Loaded ",
                  "level": " info ",
                  "type": " default ",
                  "data": { "scene": "Stage1" }
                }
              ]
            }
            """;

        using var response = await RelayTestClient.PostEventAsync(client, Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        Assert.Equal("boom", payload.GetProperty("message").GetString());
        Assert.Equal("unity", payload.GetProperty("platform").GetString());
        Assert.Equal("Game.Server", payload.GetProperty("logger").GetString());
        Assert.Equal("game@1.0.0", payload.GetProperty("release").GetString());
        Assert.Equal("production", payload.GetProperty("environment").GetString());
        Assert.Equal("server-01", payload.GetProperty("server_name").GetString());

        var exception = payload.GetProperty("exception")[0];
        Assert.Equal("System.Exception", exception.GetProperty("type").GetString());
        Assert.Equal("a  b", exception.GetProperty("value").GetString());
        Assert.Equal("Game", exception.GetProperty("module").GetString());

        var frame = exception.GetProperty("stacktrace").GetProperty("frames")[0];
        Assert.Equal("Foo.Bar", frame.GetProperty("function").GetString());
        Assert.Equal("Game", frame.GetProperty("module").GetString());
        Assert.Equal("Foo.cs", frame.GetProperty("filename").GetString());
        Assert.Equal("/src/Foo.cs", frame.GetProperty("abs_path").GetString());
        Assert.Equal("call();", frame.GetProperty("context_line").GetString());

        Assert.Equal("eu-west-1", payload.GetProperty("tags").GetProperty("region").GetString());
        Assert.Equal("checkout", payload.GetProperty("fingerprint")[0].GetString());

        var user = payload.GetProperty("user");
        Assert.Equal("player-1", user.GetProperty("id").GetString());
        Assert.Equal("foo", user.GetProperty("username").GetString());
        Assert.Equal("foo@example.com", user.GetProperty("email").GetString());

        var breadcrumb = payload.GetProperty("breadcrumbs")[0];
        Assert.Equal("2026-11-14T11:59:58.000Z", breadcrumb.GetProperty("timestamp").GetString());
        Assert.Equal("scene", breadcrumb.GetProperty("category").GetString());
        Assert.Equal("Loaded", breadcrumb.GetProperty("message").GetString());
        Assert.Equal("info", breadcrumb.GetProperty("level").GetString());
        Assert.Equal("default", breadcrumb.GetProperty("type").GetString());
        Assert.Equal(
            "Stage1",
            breadcrumb.GetProperty("data").GetProperty("scene").GetString());
    }

    [Fact]
    public async Task PostEvent_KeepsOnlyTheFirstTenExceptionsInOrder()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var exceptions = Enumerable.Range(0, 12)
            .Select(index => new { type = $"Exception{index}" })
            .ToArray();
        var json = JsonSerializer.Serialize(new { platform = "unity", exception = exceptions });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        var normalized = payload.GetProperty("exception");
        Assert.Equal(10, normalized.GetArrayLength());
        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(
                $"Exception{index}",
                normalized[index].GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task PostEvent_KeepsOnlyTheLastTwoHundredFiftyFramesInOrder()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var frames = Enumerable.Range(0, 300)
            .Select(index => new { function = $"Frame{index}" })
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            platform = "unity",
            exception = new[]
            {
                new { type = "System.Exception", stacktrace = new { frames } },
            },
        });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        var normalized = payload
            .GetProperty("exception")[0]
            .GetProperty("stacktrace")
            .GetProperty("frames");
        Assert.Equal(250, normalized.GetArrayLength());
        Assert.Equal("Frame50", normalized[0].GetProperty("function").GetString());
        Assert.Equal("Frame299", normalized[249].GetProperty("function").GetString());

        for (var index = 0; index < normalized.GetArrayLength(); index++)
        {
            Assert.Equal(
                $"Frame{index + 50}",
                normalized[index].GetProperty("function").GetString());
        }
    }

    [Fact]
    public async Task PostEvent_KeepsOnlyTheFirstHundredBreadcrumbsInOrder()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var breadcrumbs = Enumerable.Range(0, 120)
            .Select(index => new { message = $"Breadcrumb{index}", category = "test" })
            .ToArray();
        var json = JsonSerializer.Serialize(
            new { message = "boom", platform = "unity", breadcrumbs });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        var normalized = payload.GetProperty("breadcrumbs");
        Assert.Equal(100, normalized.GetArrayLength());
        Assert.Equal("Breadcrumb0", normalized[0].GetProperty("message").GetString());
        Assert.Equal("Breadcrumb99", normalized[99].GetProperty("message").GetString());
    }

    [Fact]
    public async Task PostEvent_KeepsOnlyTheFirstFiftyTags()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var tags = Enumerable.Range(0, 60)
            .ToDictionary(index => $"tag{index}", index => $"value{index}");
        var json = JsonSerializer.Serialize(new { message = "boom", platform = "unity", tags });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        var normalized = payload.GetProperty("tags");
        Assert.Equal(50, normalized.EnumerateObject().Count());
        Assert.True(normalized.TryGetProperty("tag0", out _));
        Assert.True(normalized.TryGetProperty("tag49", out _));
        Assert.False(normalized.TryGetProperty("tag50", out _));
        Assert.False(normalized.TryGetProperty("tag59", out _));
    }

    [Fact]
    public async Task PostEvent_KeepsOnlyTheFirstTenFingerprintEntries()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var fingerprint = Enumerable.Range(0, 12)
            .Select(index => $"entry{index}")
            .ToArray();
        var json = JsonSerializer.Serialize(
            new { message = "boom", platform = "unity", fingerprint });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        var normalized = payload.GetProperty("fingerprint");
        Assert.Equal(10, normalized.GetArrayLength());
        Assert.Equal("entry0", normalized[0].GetString());
        Assert.Equal("entry9", normalized[9].GetString());
    }

    [Fact]
    public async Task PostEvent_OmitsContextsAndExtraLargerThanSixtyFourKilobytes()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var oversized = new string('x', 70_000);
        var json = JsonSerializer.Serialize(new
        {
            message = "boom",
            platform = "unity",
            contexts = new { big = oversized },
            extra = new { big = oversized },
        });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));
        Assert.False(payload.TryGetProperty("contexts", out _));
        Assert.False(payload.TryGetProperty("extra", out _));
        Assert.Equal("boom", payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PostEvent_KeepsContextsAndExtraWithinSixtyFourKilobytes()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var json = JsonSerializer.Serialize(new
        {
            message = "boom",
            platform = "unity",
            contexts = new { os = new { name = "Windows" } },
            extra = new { score = 42 },
        });

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));
        Assert.Equal(
            "Windows",
            payload.GetProperty("contexts").GetProperty("os").GetProperty("name").GetString());
        Assert.Equal(42, payload.GetProperty("extra").GetProperty("score").GetInt32());
    }

    [Theory]
    [InlineData("""{"message":"boom"}""")]
    [InlineData("""{"message":"boom","platform":null}""")]
    [InlineData("""{"message":"boom","platform":""}""")]
    [InlineData("""{"message":"boom","platform":"   "}""")]
    public async Task PostEvent_WithoutUsablePlatform_ReturnsBadRequest(string json)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("platform is required", await RelayTestClient.ReadErrorAsync(response));
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithUppercasePlatform_StoresItLowercased()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"Unity"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));
        Assert.Equal("unity", payload.GetProperty("platform").GetString());
    }

    [Theory]
    [InlineData("unity/1")]
    [InlineData("unity 1")]
    [InlineData("Unity@2")]
    [InlineData("유니티")]
    public async Task PostEvent_WithInvalidPlatformCharacters_ReturnsBadRequest(string platform)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var json = JsonSerializer.Serialize(new { message = "boom", platform });
        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "platform must match [a-z0-9._-]",
            await RelayTestClient.ReadErrorAsync(response));
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_TruncatesPlatformTo32Characters()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        // The last eight characters are not valid in a platform, but the value
        // is truncated to 32 characters before it is validated.
        var platform = new string('p', 32) + new string('/', 8);
        Assert.Equal(40, platform.Length);

        var json = JsonSerializer.Serialize(new { message = "boom", platform });
        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));
        Assert.Equal(new string('p', 32), payload.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task PostEvent_WithNewlineAtTruncationBoundary_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        // Truncated to 32 characters the value ends with a newline, which a '$'
        // anchor would accept (it also matches before a trailing '\n').
        var platform = new string('p', 31) + "\n" + "x";
        Assert.Equal(33, platform.Length);

        var json = JsonSerializer.Serialize(new { message = "boom", platform });
        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "platform must match [a-z0-9._-]",
            await RelayTestClient.ReadErrorAsync(response));
        Assert.Empty(publisher.Envelopes);
    }
}
