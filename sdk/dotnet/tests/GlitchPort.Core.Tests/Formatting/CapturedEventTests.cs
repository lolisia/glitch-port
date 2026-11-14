using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class CapturedEventTests
    {
        private static readonly Guid EventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");

        [Fact]
        public void Constructor_StoresTimestampInUtc()
        {
            var timestamp = new DateTimeOffset(2026, 11, 14, 15, 2, 14, 10, TimeSpan.FromHours(9));

            var captured = CreateEvent(timestamp: timestamp);

            Assert.Equal(TimeSpan.Zero, captured.Timestamp.Offset);
            Assert.Equal(new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero), captured.Timestamp);
        }

        [Fact]
        public void Constructor_CopiesAllValues()
        {
            var timestamp = new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero);

            var captured = new CapturedEvent(
                EventId,
                timestamp,
                Level.Warning,
                "hello",
                "glitchport.dotnet",
                "0.1.0",
                "{\"message\":\"hello\"}");

            Assert.Equal(EventId, captured.EventId);
            Assert.Equal(timestamp, captured.Timestamp);
            Assert.Equal(Level.Warning, captured.Level);
            Assert.Equal("hello", captured.Message);
            Assert.Equal("glitchport.dotnet", captured.SdkName);
            Assert.Equal("0.1.0", captured.SdkVersion);
            Assert.Equal("{\"message\":\"hello\"}", captured.Json);
        }

        [Fact]
        public void Constructor_NullString_ThrowsArgumentNullException()
        {
            var timestamp = new DateTimeOffset(2026, 11, 14, 6, 2, 14, TimeSpan.Zero);

            Assert.Throws<ArgumentNullException>(
                () => new CapturedEvent(EventId, timestamp, Level.Info, null!, "sdk", "1.0.0", "{}"));
            Assert.Throws<ArgumentNullException>(
                () => new CapturedEvent(EventId, timestamp, Level.Info, "hello", null!, "1.0.0", "{}"));
            Assert.Throws<ArgumentNullException>(
                () => new CapturedEvent(EventId, timestamp, Level.Info, "hello", "sdk", null!, "{}"));
            Assert.Throws<ArgumentNullException>(
                () => new CapturedEvent(EventId, timestamp, Level.Info, "hello", "sdk", "1.0.0", null!));
        }

        private static CapturedEvent CreateEvent(DateTimeOffset timestamp)
        {
            return new CapturedEvent(EventId, timestamp, Level.Info, "hello", "sdk", "1.0.0", "{}");
        }
    }
}
