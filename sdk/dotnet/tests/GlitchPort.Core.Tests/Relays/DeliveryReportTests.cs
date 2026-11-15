using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class DeliveryReportTests
    {
        [Fact]
        public void Constructor_WithoutQueuedCount_DefaultsToZero()
        {
            var result = new RelayDeliveryResult("http", "http", delivered: 3, failed: 0, dropped: 0, lastError: null);

            Assert.Equal("http", result.Name);
            Assert.Equal("http", result.Type);
            Assert.Equal(3, result.Delivered);
            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.Dropped);
            Assert.Equal(0, result.Queued);
            Assert.Null(result.LastError);
            Assert.True(result.Succeeded);
        }

        [Fact]
        public void Constructor_WithQueuedCount_ExposesTheValue()
        {
            var result = new RelayDeliveryResult(
                "http",
                "http",
                delivered: 1,
                failed: 2,
                dropped: 3,
                queued: 4,
                lastError: "HTTP 503 (ServiceUnavailable)");

            Assert.Equal(1, result.Delivered);
            Assert.Equal(2, result.Failed);
            Assert.Equal(3, result.Dropped);
            Assert.Equal(4, result.Queued);
            Assert.Equal("HTTP 503 (ServiceUnavailable)", result.LastError);
            Assert.False(result.Succeeded);
        }

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(1, 0, false)]
        [InlineData(0, 1, false)]
        [InlineData(1, 1, false)]
        public void Succeeded_OnlyDependsOnFailedAndDropped(long failed, long dropped, bool expected)
        {
            var result = new RelayDeliveryResult("http", "http", 10, failed, dropped, queued: 25, lastError: null);

            Assert.Equal(expected, result.Succeeded);
        }

        [Fact]
        public void Report_AllSucceeded_IgnoresTheQueuedCount()
        {
            var report = new DeliveryReport(new[]
            {
                new RelayDeliveryResult("http", "http", 1, 0, 0, 12, null),
                new RelayDeliveryResult("file", "file", 1, 0, 0, null),
            });

            Assert.True(report.AllSucceeded);
            Assert.Equal(12, report.Relays[0].Queued);
            Assert.Equal(0, report.Relays[1].Queued);
        }

        [Fact]
        public void Constructor_WithNullName_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => new RelayDeliveryResult(null!, "http", 0, 0, 0, 0, null));
        }
    }
}
