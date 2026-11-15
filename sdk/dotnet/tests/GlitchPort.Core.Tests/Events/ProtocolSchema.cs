using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace GlitchPort.Tests.Events
{
    /// <summary>
    /// Validates serialized events against the shared protocol schema that lives
    /// in <c>sdk/protocol/event.v1.schema.json</c>. The schema file is copied next
    /// to the test assembly by the project file.
    /// </summary>
    internal static class ProtocolSchema
    {
        // JsonSchema registers schemas by their $id, so the schema may be built
        // only once per process.
        private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

        /// <summary>Asserts that <paramref name="json"/> matches the protocol schema.</summary>
        public static void AssertValid(string json)
        {
            using var document = JsonDocument.Parse(json);

            var results = Schema.Value.Evaluate(
                document.RootElement,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List,
                    RequireFormatValidation = true,
                });

            Assert.True(
                results.IsValid,
                "The event is not valid against event.v1.schema.json: "
                    + Describe(results)
                    + Environment.NewLine
                    + json);
        }

        private static JsonSchema LoadSchema()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "protocol", "event.v1.schema.json");
            Assert.True(File.Exists(path), $"The protocol schema was not copied to the test output: {path}");

            return JsonSchema.FromFile(path);
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
}
