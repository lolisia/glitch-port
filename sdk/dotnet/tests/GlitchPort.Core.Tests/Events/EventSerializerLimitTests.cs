using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Tests.Events
{
    /// <summary>
    /// The serializer applies the protocol limits before an event is sent:
    /// over-length strings are truncated, over-count collections keep the
    /// documented subset and oversized free-form data is omitted.
    /// </summary>
    public sealed class EventSerializerLimitTests
    {
        private const string SdkName = "glitchport.dotnet";
        private const string SdkVersion = "0.1.0";

        private static readonly Guid EventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");
        private static readonly DateTimeOffset Timestamp =
            new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero);

        [Fact]
        public void Serialize_TruncatesEventStrings()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Platform = new string('p', 40),
                Logger = new string('l', 200),
                Message = new string('m', 9000),
                Release = new string('r', 300),
                Environment = new string('e', 100),
                ServerName = new string('s', 300),
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(32, JsonAssert.Property(root, "platform").GetString()!.Length);
            Assert.Equal(128, JsonAssert.Property(root, "logger").GetString()!.Length);
            Assert.Equal(8192, JsonAssert.Property(root, "message").GetString()!.Length);
            Assert.Equal(200, JsonAssert.Property(root, "release").GetString()!.Length);
            Assert.Equal(64, JsonAssert.Property(root, "environment").GetString()!.Length);
            Assert.Equal(200, JsonAssert.Property(root, "server_name").GetString()!.Length);
        }

        [Fact]
        public void Serialize_KeepsTheInnermostExceptions()
        {
            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp };

            for (var i = 0; i < 12; i++)
            {
                e.Exceptions.Add(new ExceptionInfo { Type = "Type" + i, Value = "value" + i });
            }

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var exceptions = JsonAssert.Property(document.RootElement, "exception");

            Assert.Equal(10, exceptions.GetArrayLength());
            Assert.Equal("Type0", JsonAssert.Property(exceptions[0], "type").GetString());
            Assert.Equal("Type9", JsonAssert.Property(exceptions[9], "type").GetString());
        }

        [Fact]
        public void Serialize_TruncatesExceptionStrings()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Exceptions =
                {
                    new ExceptionInfo
                    {
                        Type = new string('t', 300),
                        Value = new string('v', 9000),
                        Module = new string('o', 300),
                    },
                },
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var exception = JsonAssert.Property(document.RootElement, "exception")[0];

            Assert.Equal(200, JsonAssert.Property(exception, "type").GetString()!.Length);
            Assert.Equal(8192, JsonAssert.Property(exception, "value").GetString()!.Length);
            Assert.Equal(200, JsonAssert.Property(exception, "module").GetString()!.Length);
        }

        [Fact]
        public void Serialize_KeepsTheFramesClosestToTheCrashSite()
        {
            var exception = new ExceptionInfo { Type = "System.Exception" };
            for (var i = 0; i < 300; i++)
            {
                exception.Frames.Add(new StackFrameInfo { Function = "f" + i, InApp = true });
            }

            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp };
            e.Exceptions.Add(exception);

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var frames = JsonAssert.Property(
                JsonAssert.Property(JsonAssert.Property(document.RootElement, "exception")[0], "stacktrace"),
                "frames");

            Assert.Equal(250, frames.GetArrayLength());
            Assert.Equal("f50", JsonAssert.Property(frames[0], "function").GetString());
            Assert.Equal("f299", JsonAssert.Property(frames[249], "function").GetString());
        }

        [Fact]
        public void Serialize_TruncatesFrameStrings()
        {
            var exception = new ExceptionInfo
            {
                Type = "System.Exception",
                Frames =
                {
                    new StackFrameInfo
                    {
                        Function = new string('f', 2000),
                        Module = new string('m', 2000),
                        FileName = new string('n', 2000),
                        AbsPath = new string('p', 2000),
                        LineNumber = 1,
                        ColumnNumber = 1,
                        InApp = false,
                    },
                },
            };

            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp };
            e.Exceptions.Add(exception);

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var frame = JsonAssert.Property(
                JsonAssert.Property(JsonAssert.Property(document.RootElement, "exception")[0], "stacktrace"),
                "frames")[0];

            Assert.Equal(1024, JsonAssert.Property(frame, "function").GetString()!.Length);
            Assert.Equal(1024, JsonAssert.Property(frame, "module").GetString()!.Length);
            Assert.Equal(1024, JsonAssert.Property(frame, "filename").GetString()!.Length);
            Assert.Equal(1024, JsonAssert.Property(frame, "abs_path").GetString()!.Length);
        }

        [Fact]
        public void Serialize_KeepsTheFirstTags()
        {
            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp, Message = "tags" };
            for (var i = 0; i < 60; i++)
            {
                e.Tags["k" + i] = "v" + i;
            }

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var tags = JsonAssert.Property(document.RootElement, "tags");

            Assert.Equal(50, CountProperties(tags));
            Assert.Equal("v0", JsonAssert.Property(tags, "k0").GetString());
            Assert.Equal("v49", JsonAssert.Property(tags, "k49").GetString());
            JsonAssert.NoProperty(tags, "k50");
        }

        [Fact]
        public void Serialize_TruncatesTagKeysAndValues()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "tags",
            };
            e.Tags[new string('k', 40)] = new string('v', 300);

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var tags = JsonAssert.Property(document.RootElement, "tags");
            var tag = Assert.Single(tags.EnumerateObject());

            Assert.Equal(32, tag.Name.Length);
            Assert.Equal(200, tag.Value.GetString()!.Length);
        }

        [Fact]
        public void Serialize_KeepsTheOldestBreadcrumbs()
        {
            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp, Message = "crumbs" };
            for (var i = 0; i < 120; i++)
            {
                e.Breadcrumbs.Add(new Breadcrumb
                {
                    Timestamp = Timestamp.AddSeconds(i),
                    Message = "b" + i,
                });
            }

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var breadcrumbs = JsonAssert.Property(document.RootElement, "breadcrumbs");

            Assert.Equal(100, breadcrumbs.GetArrayLength());
            Assert.Equal("b0", JsonAssert.Property(breadcrumbs[0], "message").GetString());
            Assert.Equal("b99", JsonAssert.Property(breadcrumbs[99], "message").GetString());
        }

        [Fact]
        public void Serialize_TruncatesFingerprintComponentsAndKeepsTen()
        {
            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp, Message = "fingerprint" };
            for (var i = 0; i < 12; i++)
            {
                e.Fingerprint.Add(new string((char)('a' + i), 300));
            }

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var fingerprint = JsonAssert.Property(document.RootElement, "fingerprint");

            Assert.Equal(10, fingerprint.GetArrayLength());
            foreach (var component in fingerprint.EnumerateArray())
            {
                Assert.Equal(200, component.GetString()!.Length);
            }

            Assert.Equal('a', fingerprint[0].GetString()![0]);
            Assert.Equal('j', fingerprint[9].GetString()![0]);
        }

        [Fact]
        public void Serialize_KeepsAContextNamespaceAtTheSizeLimitAndOmitsTheNextByte()
        {
            // The serialized namespace is {"blob":"<n characters>"}, so n + 11
            // bytes are measured.
            var atLimit = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp, Message = "contexts" };
            atLimit.Contexts["kept"] = new Dictionary<string, object?>
            {
                ["blob"] = new string('x', 65525),
            };

            var overLimit = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp, Message = "contexts" };
            overLimit.Contexts["omitted"] = new Dictionary<string, object?>
            {
                ["blob"] = new string('x', 65526),
            };

            using var keptDocument = JsonDocument.Parse(EventSerializer.Serialize(atLimit, SdkName, SdkVersion));
            using var omittedDocument = JsonDocument.Parse(EventSerializer.Serialize(overLimit, SdkName, SdkVersion));

            Assert.True(JsonAssert.Property(keptDocument.RootElement, "contexts").TryGetProperty("kept", out _));
            JsonAssert.NoProperty(omittedDocument.RootElement, "contexts");
        }

        [Fact]
        public void Serialize_OmitsOversizedContextNamespacesAndKeepsTheSmallOnes()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "contexts",
            };
            e.Contexts["small"] = new Dictionary<string, object?> { ["scene"] = "Stage1" };
            e.Contexts["huge"] = new Dictionary<string, object?> { ["blob"] = new string('x', 70000) };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            JsonAssert.Compact(json);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var contexts = JsonAssert.Property(document.RootElement, "contexts");

            Assert.True(contexts.TryGetProperty("small", out var small));
            Assert.Equal("Stage1", JsonAssert.Property(small, "scene").GetString());
            JsonAssert.NoProperty(contexts, "huge");
        }

        [Fact]
        public void Serialize_MeasuresContextNamespacesInUtf8Bytes()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "contexts",
            };

            // 30 000 emoji are 60 000 UTF-16 characters but 120 000 UTF-8 bytes.
            e.Contexts["emoji"] = new Dictionary<string, object?>
            {
                ["blob"] = string.Concat(System.Linq.Enumerable.Repeat("\ud83c\udfae", 30000)),
            };

            using var document = JsonDocument.Parse(EventSerializer.Serialize(e, SdkName, SdkVersion));
            JsonAssert.NoProperty(document.RootElement, "contexts");
        }

        [Fact]
        public void Serialize_KeepsExtraAtTheSizeLimitAndOmitsTheNextByte()
        {
            var atLimit = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "extra",
                Extra = new Dictionary<string, object?> { ["blob"] = new string('x', 65525) },
            };

            var overLimit = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "extra",
                Extra = new Dictionary<string, object?> { ["blob"] = new string('x', 65526) },
            };

            using var keptDocument = JsonDocument.Parse(EventSerializer.Serialize(atLimit, SdkName, SdkVersion));
            using var omittedDocument = JsonDocument.Parse(EventSerializer.Serialize(overLimit, SdkName, SdkVersion));

            Assert.True(JsonAssert.Property(keptDocument.RootElement, "extra").TryGetProperty("blob", out _));
            JsonAssert.NoProperty(omittedDocument.RootElement, "extra");
        }

        [Fact]
        public void Serialize_OmitsOversizedExtra()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "extra",
                Extra = new Dictionary<string, object?>
                {
                    ["blob"] = string.Concat(System.Linq.Enumerable.Repeat("\ud83c\udfae", 30000)),
                },
            };

            using var document = JsonDocument.Parse(EventSerializer.Serialize(e, SdkName, SdkVersion));
            JsonAssert.NoProperty(document.RootElement, "extra");
        }

        private static int CountProperties(JsonElement element)
        {
            var count = 0;
            foreach (var _ in element.EnumerateObject())
            {
                count++;
            }

            return count;
        }
    }
}
