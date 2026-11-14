using System;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Tests
{
    /// <summary>
    /// <see cref="JsonPayloadWriter"/> is internal to the SDK, so these tests invoke
    /// the overloads through reflection rather than exposing the writer to consumers.
    /// </summary>
    public sealed class JsonPayloadWriterTests
    {
        private static readonly Guid EventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");
        private static readonly DateTimeOffset Timestamp = new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero);

        [Fact]
        public void SixArgumentOverload_WritesFieldsInDocumentedOrder()
        {
            var payload = CreatePayload(EventId, Timestamp, Level.Warning, "hello", "glitchport.dotnet", "0.1.0");

            Assert.Equal(
                "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\""
                    + ",\"timestamp\":\"2026-11-14T06:02:14.010Z\""
                    + ",\"level\":\"warning\""
                    + ",\"message\":\"hello\""
                    + ",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}",
                payload);
        }

        [Fact]
        public void SixArgumentOverload_PlacesLevelBetweenTimestampAndMessage()
        {
            var payload = CreatePayload(EventId, Timestamp, Level.Warning, "hello", "sdk", "1.0.0");

            var timestampIndex = payload.IndexOf("\"timestamp\":", StringComparison.Ordinal);
            var levelIndex = payload.IndexOf("\"level\":", StringComparison.Ordinal);
            var messageIndex = payload.IndexOf("\"message\":", StringComparison.Ordinal);

            Assert.True(timestampIndex >= 0);
            Assert.True(timestampIndex < levelIndex);
            Assert.True(levelIndex < messageIndex);
        }

        [Theory]
        [InlineData(Level.Debug, "debug")]
        [InlineData(Level.Info, "info")]
        [InlineData(Level.Warning, "warning")]
        [InlineData(Level.Error, "error")]
        [InlineData(Level.Fatal, "fatal")]
        public void SixArgumentOverload_WritesJsonLevelName(Level level, string expectedLevel)
        {
            var payload = CreatePayload(EventId, Timestamp, level, "hello", "sdk", "1.0.0");

            using var document = JsonDocument.Parse(payload);
            Assert.Equal(expectedLevel, document.RootElement.GetProperty("level").GetString());
        }

        [Fact]
        public void FiveArgumentOverload_DefaultsToInfoLevel()
        {
            var payload = CreatePayloadWithoutLevel(EventId, Timestamp, "hello", "sdk", "1.0.0");

            using var document = JsonDocument.Parse(payload);
            Assert.Equal("info", document.RootElement.GetProperty("level").GetString());
        }

        [Fact]
        public void SixArgumentOverload_ConvertsTimestampToUtc()
        {
            var localTimestamp = new DateTimeOffset(2026, 11, 14, 15, 2, 14, 10, TimeSpan.FromHours(9));

            var payload = CreatePayload(EventId, localTimestamp, Level.Info, "hello", "sdk", "1.0.0");

            using var document = JsonDocument.Parse(payload);
            Assert.Equal("2026-11-14T06:02:14.010Z", document.RootElement.GetProperty("timestamp").GetString());
        }

        [Fact]
        public void SixArgumentOverload_EscapesMessageAndKeepsUnicode()
        {
            var payload = CreatePayload(EventId, Timestamp, Level.Error, "quote:\" newline:\n \ud83c\udfae", "sdk", "1.0.0");

            Assert.Contains("\\\"", payload);
            Assert.Contains("\\n", payload);
            Assert.Contains("\ud83c\udfae", payload);

            using var document = JsonDocument.Parse(payload);
            Assert.Equal("quote:\" newline:\n \ud83c\udfae", document.RootElement.GetProperty("message").GetString());
        }

        private static string CreatePayload(
            Guid eventId,
            DateTimeOffset timestamp,
            Level level,
            string message,
            string sdkName,
            string sdkVersion)
        {
            return Invoke(
                new[] { typeof(Guid), typeof(DateTimeOffset), typeof(Level), typeof(string), typeof(string), typeof(string) },
                new object?[] { eventId, timestamp, level, message, sdkName, sdkVersion });
        }

        private static string CreatePayloadWithoutLevel(
            Guid eventId,
            DateTimeOffset timestamp,
            string message,
            string sdkName,
            string sdkVersion)
        {
            return Invoke(
                new[] { typeof(Guid), typeof(DateTimeOffset), typeof(string), typeof(string), typeof(string) },
                new object?[] { eventId, timestamp, message, sdkName, sdkVersion });
        }

        private static string Invoke(Type[] parameterTypes, object?[] arguments)
        {
            var writerType = typeof(GlitchPortClient).Assembly.GetType("GlitchPort.JsonPayloadWriter", throwOnError: true);
            Assert.NotNull(writerType);

            var method = writerType!.GetMethod(
                "CreateMessagePayload",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: parameterTypes,
                modifiers: null);
            Assert.NotNull(method);

            return Assert.IsType<string>(method!.Invoke(null, arguments));
        }
    }
}
