using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class HttpOfflineQueueOptionsTests
    {
        [Fact]
        public void Defaults_KeepTheQueueDisabled()
        {
            var options = new HttpRelayOptions();

            Assert.NotNull(options.OfflineQueue);
            Assert.Null(options.OfflineQueue.Directory);
            Assert.Equal(500, options.OfflineQueue.MaxEvents);
            Assert.Equal(72, options.OfflineQueue.MaxAgeHours);
            Assert.Equal(30, options.OfflineQueue.RetryIntervalSeconds);
        }

        [Fact]
        public void Validate_AcceptsTheSupportedBoundaries()
        {
            var options = new HttpOfflineQueueOptions
            {
                MaxEvents = 1,
                MaxAgeHours = 0,
                RetryIntervalSeconds = 1,
            };

            options.Validate();
        }

        [Theory]
        [InlineData(0, 72, 30, "MaxEvents")]
        [InlineData(-1, 72, 30, "MaxEvents")]
        [InlineData(500, -1, 30, "MaxAgeHours")]
        [InlineData(500, 72, 0, "RetryIntervalSeconds")]
        [InlineData(500, 72, -1, "RetryIntervalSeconds")]
        public void Validate_RejectsOutOfRangeValues(
            int maxEvents,
            int maxAgeHours,
            int retryIntervalSeconds,
            string expectedParameter)
        {
            var options = new HttpOfflineQueueOptions
            {
                MaxEvents = maxEvents,
                MaxAgeHours = maxAgeHours,
                RetryIntervalSeconds = retryIntervalSeconds,
            };

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
            Assert.Equal(expectedParameter, exception.ParamName);
        }
    }
}
