using System;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class DsnTests
    {
        [Fact]
        public void Parse_WithPort_ReturnsComponents()
        {
            var dsn = Dsn.Parse("http://public-key@relay.example.com:5080/42");

            Assert.Equal("public-key", dsn.PublicKey);
            Assert.Equal(42L, dsn.ProjectId);
            Assert.Equal(Uri.UriSchemeHttp, dsn.RelayBaseUri.Scheme);
            Assert.Equal("relay.example.com", dsn.RelayBaseUri.Host);
            Assert.Equal(5080, dsn.RelayBaseUri.Port);
            Assert.Equal("http://relay.example.com:5080", Authority(dsn));
        }

        [Fact]
        public void Parse_WithoutPort_ReturnsComponents()
        {
            var dsn = Dsn.Parse("http://public-key@relay.example.com/7");

            Assert.Equal("public-key", dsn.PublicKey);
            Assert.Equal(7L, dsn.ProjectId);
            Assert.Equal(Uri.UriSchemeHttp, dsn.RelayBaseUri.Scheme);
            Assert.Equal("relay.example.com", dsn.RelayBaseUri.Host);
            Assert.Equal("http://relay.example.com", Authority(dsn));
        }

        [Fact]
        public void Parse_HttpsDsn_KeepsScheme()
        {
            var dsn = Dsn.Parse("https://public-key@relay.example.com:443/1");

            Assert.Equal(Uri.UriSchemeHttps, dsn.RelayBaseUri.Scheme);
            Assert.Equal(1L, dsn.ProjectId);
        }

        [Fact]
        public void Parse_WithPathPrefix_UsesLastSegmentAsProjectId()
        {
            var dsn = Dsn.Parse("http://public-key@relay.example.com:5080/games/hello/99");

            Assert.Equal(99L, dsn.ProjectId);
            Assert.Equal("http://relay.example.com:5080", Authority(dsn));
        }

        [Fact]
        public void Parse_ProjectIdLargerThanInt32_IsPreserved()
        {
            var dsn = Dsn.Parse("http://public-key@relay.example.com/4294967296");

            Assert.Equal(4294967296L, dsn.ProjectId);
        }

        [Fact]
        public void Parse_RelayBaseUri_DoesNotEndWithSlash()
        {
            var dsn = Dsn.Parse("http://public-key@relay.example.com:5080/1");

            Assert.False(dsn.RelayBaseUri.OriginalString.EndsWith("/", StringComparison.Ordinal));
            Assert.Equal("http://relay.example.com:5080", dsn.RelayBaseUri.OriginalString);
        }

        [Fact]
        public void Parse_RelayBaseUri_DoesNotContainPublicKey()
        {
            var dsn = Dsn.Parse("http://public-key@relay.example.com:5080/1");

            Assert.DoesNotContain("public-key", dsn.RelayBaseUri.ToString());
        }

        [Fact]
        public void Parse_SurroundingWhitespace_IsIgnored()
        {
            var dsn = Dsn.Parse("  http://public-key@relay.example.com:5080/5  ");

            Assert.Equal("public-key", dsn.PublicKey);
            Assert.Equal(5L, dsn.ProjectId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("relay.example.com:5080/1")]
        [InlineData("http://public-key@/1")]
        [InlineData("http://relay.example.com:5080/1")]
        [InlineData("http://public-key@relay.example.com:5080")]
        [InlineData("http://public-key@relay.example.com:5080/")]
        [InlineData("http://public-key@relay.example.com:5080/not-a-number")]
        [InlineData("http://public-key@relay.example.com:not-a-port/1")]
        [InlineData("http://public-key@relay.example.com:999999/1")]
        [InlineData("ftp://public-key@relay.example.com/1")]
        public void Parse_InvalidDsn_ThrowsFormatException(string? dsn)
        {
            Assert.Throws<FormatException>(() => Dsn.Parse(dsn!));
        }

        [Fact]
        public void TryParse_ValidDsn_ReturnsTrueAndValue()
        {
            var parsed = Dsn.TryParse("http://public-key@relay.example.com:5080/3", out var dsn);

            Assert.True(parsed);
            Assert.NotNull(dsn);
            Assert.Equal("public-key", dsn.PublicKey);
            Assert.Equal(3L, dsn.ProjectId);
            Assert.Equal(5080, dsn.RelayBaseUri.Port);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a dsn")]
        [InlineData("http://relay.example.com:5080/1")]
        [InlineData("http://public-key@relay.example.com:5080/not-a-number")]
        public void TryParse_InvalidDsn_ReturnsFalse(string? dsn)
        {
            var parsed = Dsn.TryParse(dsn, out var value);

            Assert.False(parsed);
            Assert.Null(value);
        }

        private static string Authority(Dsn dsn)
        {
            return dsn.RelayBaseUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }
    }
}
