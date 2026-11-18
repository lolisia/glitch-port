using System.Net;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace GlitchPort.Relay.Tests;

/// <summary>
/// The protocol examples under <c>sdk/protocol/examples</c> are the shared
/// contract between the SDKs and the collector: every example must both
/// validate against the schema and be accepted by the Relay.
/// </summary>
public sealed class ProtocolExamplesTests
{
    // JsonSchema registers the schema by its $id, so it may be built only once
    // per process.
    private static readonly Lazy<JsonSchema> ProtocolSchema = new(LoadSchema);

    [Theory]
    [InlineData("message-only.json")]
    [InlineData("exception-unity.json")]
    [InlineData("exception-dotnet-nested.json")]
    [InlineData("custom-fingerprint.json")]
    public async Task ProtocolExample_IsAcceptedAndMatchesTheSchema(string fileName)
    {
        var schema = ProtocolSchema.Value;
        var json = await File.ReadAllTextAsync(ExamplePath(fileName));

        var exampleEvaluation = Evaluate(schema, json);
        Assert.True(
            exampleEvaluation.IsValid,
            $"{fileName} is not valid against event.v1.schema.json: {Describe(exampleEvaluation)}");

        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await RelayTestClient.ReadJsonAsync(response);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(2, envelope.Version);
        Assert.Equal(envelope.EventId, body.GetProperty("id").GetGuid());
        Assert.NotNull(envelope.Payload);

        var payloadEvaluation = Evaluate(schema, envelope.Payload!.Value.GetRawText());
        Assert.True(
            payloadEvaluation.IsValid,
            $"{fileName} normalized payload is not valid against event.v1.schema.json: "
            + Describe(payloadEvaluation));
    }

    [Fact]
    public async Task Schema_RequiresPlatformAndForbidsBlankValues()
    {
        var json = await File.ReadAllTextAsync(SchemaPath());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var required = root.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains("platform", required);

        var platform = root.GetProperty("properties").GetProperty("platform");
        Assert.Equal(1, platform.GetProperty("minLength").GetInt32());
        Assert.Equal(32, platform.GetProperty("maxLength").GetInt32());
        Assert.DoesNotContain(
            "defaults to other",
            platform.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSchema LoadSchema() => JsonSchema.FromFile(SchemaPath());

    private static string SchemaPath() =>
        Path.Combine(
            RelayTestClient.RepositoryRoot,
            "sdk",
            "protocol",
            "event.v1.schema.json");

    private static string ExamplePath(string fileName) =>
        Path.Combine(
            RelayTestClient.RepositoryRoot,
            "sdk",
            "protocol",
            "examples",
            fileName);

    private static EvaluationResults Evaluate(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return schema.Evaluate(
            document.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            });
    }

    private static string Describe(EvaluationResults results)
    {
        var messages = new List<string>();
        CollectErrors(results, messages);
        return messages.Count == 0 ? "no error details" : string.Join("; ", messages);
    }

    private static void CollectErrors(EvaluationResults results, List<string> messages)
    {
        if (results.Errors is not null)
        {
            foreach (var error in results.Errors)
            {
                messages.Add($"{results.InstanceLocation}: {error.Key}: {error.Value}");
            }
        }

        if (results.Details is not null)
        {
            foreach (var detail in results.Details)
            {
                CollectErrors(detail, messages);
            }
        }
    }
}
