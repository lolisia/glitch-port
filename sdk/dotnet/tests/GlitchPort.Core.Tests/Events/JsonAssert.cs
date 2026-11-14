using System;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Tests.Events
{
    internal static class JsonAssert
    {
        /// <summary>
        /// Asserts that a JSON document contains no insignificant whitespace: the
        /// only whitespace allowed is the text inside string literals.
        /// </summary>
        public static void Compact(string json)
        {
            var inString = false;
            var escaped = false;

            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    continue;
                }

                Assert.False(
                    char.IsWhiteSpace(character),
                    $"Unexpected whitespace at index {index}: {json}");
            }

            Assert.False(inString, "The JSON document has an unterminated string literal.");
        }

        /// <summary>Parses <paramref name="json"/> and returns the named property.</summary>
        public static JsonElement Property(JsonElement element, string name)
        {
            Assert.True(
                element.TryGetProperty(name, out var property),
                $"Expected property '{name}' in {element.GetRawText()}");

            return property;
        }

        /// <summary>Asserts that a property is absent from an object.</summary>
        public static void NoProperty(JsonElement element, string name)
        {
            Assert.False(
                element.TryGetProperty(name, out _),
                $"Did not expect property '{name}' in {element.GetRawText()}");
        }
    }
}
