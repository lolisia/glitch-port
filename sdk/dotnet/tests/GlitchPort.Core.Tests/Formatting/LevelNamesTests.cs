using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class LevelNamesTests
    {
        [Theory]
        [InlineData(Level.Debug, "debug")]
        [InlineData(Level.Info, "info")]
        [InlineData(Level.Warning, "warning")]
        [InlineData(Level.Error, "error")]
        [InlineData(Level.Fatal, "fatal")]
        public void ToJson_ReturnsLowerCaseName(Level level, string expected)
        {
            Assert.Equal(expected, LevelNames.ToJson(level));
        }

        [Theory]
        [InlineData("debug", Level.Debug)]
        [InlineData("info", Level.Info)]
        [InlineData("warning", Level.Warning)]
        [InlineData("error", Level.Error)]
        [InlineData("fatal", Level.Fatal)]
        [InlineData("DEBUG", Level.Debug)]
        [InlineData("Info", Level.Info)]
        [InlineData("Warning", Level.Warning)]
        [InlineData("ERROR", Level.Error)]
        [InlineData("Fatal", Level.Fatal)]
        public void TryParse_ValidName_IsCaseInsensitive(string value, Level expected)
        {
            var parsed = LevelNames.TryParse(value, out var level);

            Assert.True(parsed);
            Assert.Equal(expected, level);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("verbose")]
        [InlineData("warn")]
        [InlineData("error!!!")]
        public void TryParse_InvalidName_ReturnsFalse(string? value)
        {
            var parsed = LevelNames.TryParse(value, out var level);

            Assert.False(parsed);
            Assert.Equal(default, level);
        }

        [Fact]
        public void ToJson_UndefinedValue_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LevelNames.ToJson((Level)42));
        }
    }
}
