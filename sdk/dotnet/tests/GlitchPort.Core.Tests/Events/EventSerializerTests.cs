using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Tests.Events
{
    public sealed class EventSerializerTests
    {
        private const string SdkName = "glitchport.dotnet";
        private const string SdkVersion = "0.1.0";

        private static readonly Guid EventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");
        private static readonly DateTimeOffset Timestamp =
            new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero);

        [Fact]
        public void Serialize_MessageOnly_WritesCompactSnakeCaseJson()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Level = Level.Warning,
                Message = "hello",
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            Assert.Equal(
                "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\""
                    + ",\"timestamp\":\"2026-11-14T06:02:14.010Z\""
                    + ",\"level\":\"warning\""
                    + ",\"platform\":\"dotnet\""
                    + ",\"message\":\"hello\""
                    + ",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}",
                json);

            JsonAssert.Compact(json);
            ProtocolSchema.AssertValid(json);
        }

        [Fact]
        public void Serialize_MessageOnly_OmitsNullAndEmptyOptionalFields()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "only a message",
            };

            using var document = JsonDocument.Parse(EventSerializer.Serialize(e, SdkName, SdkVersion));
            var root = document.RootElement;

            JsonAssert.NoProperty(root, "logger");
            JsonAssert.NoProperty(root, "release");
            JsonAssert.NoProperty(root, "environment");
            JsonAssert.NoProperty(root, "server_name");
            JsonAssert.NoProperty(root, "exception");
            JsonAssert.NoProperty(root, "tags");
            JsonAssert.NoProperty(root, "user");
            JsonAssert.NoProperty(root, "contexts");
            JsonAssert.NoProperty(root, "breadcrumbs");
            JsonAssert.NoProperty(root, "fingerprint");
            JsonAssert.NoProperty(root, "extra");

            ProtocolSchema.AssertValid(root.GetRawText());
        }

        [Fact]
        public void Serialize_ExceptionOnly_ValidatesAgainstTheSchema()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Level = Level.Error,
                Exceptions =
                {
                    new ExceptionInfo
                    {
                        Type = "System.InvalidOperationException",
                        Value = "boom",
                        Module = "System",
                        Frames =
                        {
                            new StackFrameInfo
                            {
                                Function = "Game.Update",
                                Module = "Assembly-CSharp",
                                FileName = "Update.cs",
                                AbsPath = "C:/game/Update.cs",
                                LineNumber = 12,
                                ColumnNumber = 5,
                                InApp = true,
                            },
                        },
                    },
                },
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            Assert.DoesNotContain("\"message\":", json, StringComparison.Ordinal);
            JsonAssert.Compact(json);
            ProtocolSchema.AssertValid(json);
        }

        [Fact]
        public void Serialize_FullyPopulatedEvent_WritesEveryField()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Level = Level.Fatal,
                Platform = "unity",
                Logger = "UnityEngine.Debug",
                Message = "NullReferenceException: Object reference not set",
                Release = "star-raiders@1.2.3",
                Environment = "production",
                ServerName = "player-01",
                Exceptions =
                {
                    new ExceptionInfo
                    {
                        Type = "System.NullReferenceException",
                        Value = "Object reference not set to an instance of an object",
                        Module = "System",
                        Frames =
                        {
                            new StackFrameInfo
                            {
                                Function = "PlayerController.Update",
                                Module = "Assembly-CSharp",
                                FileName = "PlayerController.cs",
                                AbsPath = "C:/build/Assets/Scripts/PlayerController.cs",
                                LineNumber = 42,
                                ColumnNumber = 17,
                                InApp = true,
                            },
                            new StackFrameInfo
                            {
                                Function = "Weapon.Fire",
                                Module = "Assembly-CSharp",
                                FileName = "Weapon.cs",
                                AbsPath = "C:/build/Assets/Scripts/Weapon.cs",
                                LineNumber = 88,
                                ColumnNumber = 13,
                                InApp = false,
                            },
                        },
                    },
                },
                Tags =
                {
                    ["scene"] = "Stage1",
                    ["game_mode"] = "coop",
                },
                User = new UserInfo
                {
                    Id = "player-123",
                    Username = "foo",
                    Email = "foo@example.com",
                    IpAddress = "{{auto}}",
                },
                Contexts =
                {
                    ["os"] = new Dictionary<string, object?>
                    {
                        ["name"] = "Windows",
                        ["version"] = "11",
                    },
                    ["game"] = new Dictionary<string, object?>
                    {
                        ["scene"] = "Stage1",
                        ["frame"] = 123456,
                    },
                },
                Breadcrumbs =
                {
                    new Breadcrumb
                    {
                        Timestamp = Timestamp.AddSeconds(-2),
                        Category = "scene",
                        Message = "Loaded Stage1",
                        Level = Level.Info,
                        Type = "default",
                        Data = new Dictionary<string, object?> { ["scene"] = "Stage1" },
                    },
                },
                Fingerprint = { "{{ default }}", "Stage1" },
                Extra = new Dictionary<string, object?>
                {
                    ["connection_id"] = "conn-77",
                },
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            JsonAssert.Compact(json);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(EventId.ToString("D"), JsonAssert.Property(root, "event_id").GetString());
            Assert.Equal("2026-11-14T06:02:14.010Z", JsonAssert.Property(root, "timestamp").GetString());
            Assert.Equal("fatal", JsonAssert.Property(root, "level").GetString());
            Assert.Equal("unity", JsonAssert.Property(root, "platform").GetString());
            Assert.Equal("UnityEngine.Debug", JsonAssert.Property(root, "logger").GetString());
            Assert.Equal(
                "NullReferenceException: Object reference not set",
                JsonAssert.Property(root, "message").GetString());
            Assert.Equal("star-raiders@1.2.3", JsonAssert.Property(root, "release").GetString());
            Assert.Equal("production", JsonAssert.Property(root, "environment").GetString());
            Assert.Equal("player-01", JsonAssert.Property(root, "server_name").GetString());

            var sdk = JsonAssert.Property(root, "sdk");
            Assert.Equal(SdkName, JsonAssert.Property(sdk, "name").GetString());
            Assert.Equal(SdkVersion, JsonAssert.Property(sdk, "version").GetString());

            var exception = JsonAssert.Property(root, "exception")[0];
            Assert.Equal("System.NullReferenceException", JsonAssert.Property(exception, "type").GetString());
            Assert.Equal(
                "Object reference not set to an instance of an object",
                JsonAssert.Property(exception, "value").GetString());
            Assert.Equal("System", JsonAssert.Property(exception, "module").GetString());

            var frames = JsonAssert.Property(JsonAssert.Property(exception, "stacktrace"), "frames");
            Assert.Equal(2, frames.GetArrayLength());
            var firstFrame = frames[0];
            Assert.Equal("PlayerController.Update", JsonAssert.Property(firstFrame, "function").GetString());
            Assert.Equal("Assembly-CSharp", JsonAssert.Property(firstFrame, "module").GetString());
            Assert.Equal("PlayerController.cs", JsonAssert.Property(firstFrame, "filename").GetString());
            Assert.Equal(
                "C:/build/Assets/Scripts/PlayerController.cs",
                JsonAssert.Property(firstFrame, "abs_path").GetString());
            Assert.Equal(42, JsonAssert.Property(firstFrame, "lineno").GetInt32());
            Assert.Equal(17, JsonAssert.Property(firstFrame, "colno").GetInt32());
            Assert.True(JsonAssert.Property(firstFrame, "in_app").GetBoolean());
            Assert.False(JsonAssert.Property(frames[1], "in_app").GetBoolean());

            var tags = JsonAssert.Property(root, "tags");
            Assert.Equal("Stage1", JsonAssert.Property(tags, "scene").GetString());
            Assert.Equal("coop", JsonAssert.Property(tags, "game_mode").GetString());

            var user = JsonAssert.Property(root, "user");
            Assert.Equal("player-123", JsonAssert.Property(user, "id").GetString());
            Assert.Equal("foo", JsonAssert.Property(user, "username").GetString());
            Assert.Equal("foo@example.com", JsonAssert.Property(user, "email").GetString());
            Assert.Equal("{{auto}}", JsonAssert.Property(user, "ip_address").GetString());

            var contexts = JsonAssert.Property(root, "contexts");
            Assert.Equal("Windows", JsonAssert.Property(JsonAssert.Property(contexts, "os"), "name").GetString());
            Assert.Equal(123456, JsonAssert.Property(JsonAssert.Property(contexts, "game"), "frame").GetInt32());

            var breadcrumb = JsonAssert.Property(root, "breadcrumbs")[0];
            Assert.Equal("2026-11-14T06:02:12.010Z", JsonAssert.Property(breadcrumb, "timestamp").GetString());
            Assert.Equal("scene", JsonAssert.Property(breadcrumb, "category").GetString());
            Assert.Equal("Loaded Stage1", JsonAssert.Property(breadcrumb, "message").GetString());
            Assert.Equal("info", JsonAssert.Property(breadcrumb, "level").GetString());
            Assert.Equal("default", JsonAssert.Property(breadcrumb, "type").GetString());
            Assert.Equal(
                "Stage1",
                JsonAssert.Property(JsonAssert.Property(breadcrumb, "data"), "scene").GetString());

            var fingerprint = JsonAssert.Property(root, "fingerprint");
            Assert.Equal(2, fingerprint.GetArrayLength());
            Assert.Equal("{{ default }}", fingerprint[0].GetString());

            Assert.Equal(
                "conn-77",
                JsonAssert.Property(JsonAssert.Property(root, "extra"), "connection_id").GetString());
        }

        [Fact]
        public void Serialize_EscapesStringsAndKeepsUnicode()
        {
            const string message = "quote:\" backslash:\\ newline:\n tab:\t control:\u0001 emoji:\ud83c\udfae";

            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = message,
                Logger = "logger \"quoted\"",
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            Assert.Contains("\\\"", json, StringComparison.Ordinal);
            Assert.Contains("\\\\", json, StringComparison.Ordinal);
            Assert.Contains("\\n", json, StringComparison.Ordinal);
            Assert.Contains("\\t", json, StringComparison.Ordinal);
            Assert.Contains("\\u0001", json, StringComparison.Ordinal);
            Assert.Contains("\ud83c\udfae", json, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(json);
            Assert.Equal(message, JsonAssert.Property(document.RootElement, "message").GetString());
            Assert.Equal("logger \"quoted\"", JsonAssert.Property(document.RootElement, "logger").GetString());
        }

        [Fact]
        public void Serialize_PlatformFallsBackToDotnetWhenBlank()
        {
            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "hello",
                Platform = "   ",
            };

            using var document = JsonDocument.Parse(EventSerializer.Serialize(e, SdkName, SdkVersion));
            Assert.Equal("dotnet", JsonAssert.Property(document.RootElement, "platform").GetString());
        }

        [Fact]
        public void Serialize_SupportsEveryValueKindInContextsExtraAndBreadcrumbData()
        {
            var when = new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero);

            var e = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "values",
                Contexts =
                {
                    ["game"] = new Dictionary<string, object?>
                    {
                        ["text"] = "Stage1",
                        ["count"] = 42,
                        ["ratio"] = 0.5d,
                        ["ratio32"] = 0.25f,
                        ["flag"] = true,
                        ["nothing"] = null,
                        ["nested"] = new Dictionary<string, object?> { ["score"] = 7L },
                        ["list"] = new object?[] { 1, "two", false, null },
                        ["hashtable"] = new Hashtable { ["a"] = 1, ["b"] = "two" },
                        ["sequence"] = new List<int> { 1, 2, 3 },
                        ["when"] = when,
                    },
                },
                Extra = new Dictionary<string, object?>
                {
                    ["note"] = "extra",
                    ["array"] = new[] { "x", "y" },
                },
                Breadcrumbs =
                {
                    new Breadcrumb
                    {
                        Timestamp = when,
                        Message = "breadcrumb",
                        Data = new Dictionary<string, object?>
                        {
                            ["index"] = 3,
                            ["tags"] = new List<string> { "a", "b" },
                        },
                    },
                },
            };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            JsonAssert.Compact(json);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var game = JsonAssert.Property(document.RootElement, "contexts");
            game = JsonAssert.Property(game, "game");

            Assert.Equal("Stage1", JsonAssert.Property(game, "text").GetString());
            Assert.Equal(42, JsonAssert.Property(game, "count").GetInt32());
            Assert.Equal(0.5d, JsonAssert.Property(game, "ratio").GetDouble());
            Assert.Equal(0.25d, JsonAssert.Property(game, "ratio32").GetDouble());
            Assert.True(JsonAssert.Property(game, "flag").GetBoolean());
            Assert.Equal(JsonValueKind.Null, JsonAssert.Property(game, "nothing").ValueKind);
            Assert.Equal(7, JsonAssert.Property(JsonAssert.Property(game, "nested"), "score").GetInt64());

            var list = JsonAssert.Property(game, "list");
            Assert.Equal(JsonValueKind.Array, list.ValueKind);
            Assert.Equal(4, list.GetArrayLength());
            Assert.Equal(1, list[0].GetInt32());
            Assert.Equal("two", list[1].GetString());
            Assert.False(list[2].GetBoolean());
            Assert.Equal(JsonValueKind.Null, list[3].ValueKind);

            var hashtable = JsonAssert.Property(game, "hashtable");
            Assert.Equal(JsonValueKind.Object, hashtable.ValueKind);
            Assert.Equal(1, JsonAssert.Property(hashtable, "a").GetInt32());
            Assert.Equal("two", JsonAssert.Property(hashtable, "b").GetString());

            var sequence = JsonAssert.Property(game, "sequence");
            Assert.Equal(3, sequence.GetArrayLength());
            Assert.Equal(3, sequence[2].GetInt32());

            Assert.Equal("2026-11-14T06:02:14.010Z", JsonAssert.Property(game, "when").GetString());

            var extra = JsonAssert.Property(document.RootElement, "extra");
            Assert.Equal("extra", JsonAssert.Property(extra, "note").GetString());
            Assert.Equal("y", JsonAssert.Property(extra, "array")[1].GetString());

            var breadcrumb = JsonAssert.Property(document.RootElement, "breadcrumbs")[0];
            var data = JsonAssert.Property(breadcrumb, "data");
            Assert.Equal(3, JsonAssert.Property(data, "index").GetInt32());
            Assert.Equal(2, JsonAssert.Property(data, "tags").GetArrayLength());
        }

        [Fact]
        public void Serialize_EmptyCollectionsAreOmitted()
        {
            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp, Message = "empty" };

            using var document = JsonDocument.Parse(EventSerializer.Serialize(e, SdkName, SdkVersion));

            JsonAssert.NoProperty(document.RootElement, "tags");
            JsonAssert.NoProperty(document.RootElement, "contexts");
            JsonAssert.NoProperty(document.RootElement, "breadcrumbs");
            JsonAssert.NoProperty(document.RootElement, "fingerprint");
            JsonAssert.NoProperty(document.RootElement, "extra");
            JsonAssert.NoProperty(document.RootElement, "exception");
        }

        [Fact]
        public void Serialize_NullUserAndEmptyUserAreOmitted()
        {
            var withNullUser = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "no user",
            };

            var withEmptyUser = new GlitchPortEvent
            {
                EventId = EventId,
                Timestamp = Timestamp,
                Message = "empty user",
                User = new UserInfo(),
            };

            using var nullUserDocument = JsonDocument.Parse(
                EventSerializer.Serialize(withNullUser, SdkName, SdkVersion));
            using var emptyUserDocument = JsonDocument.Parse(
                EventSerializer.Serialize(withEmptyUser, SdkName, SdkVersion));

            JsonAssert.NoProperty(nullUserDocument.RootElement, "user");
            JsonAssert.NoProperty(emptyUserDocument.RootElement, "user");
        }

        [Fact]
        public void Serialize_AlwaysWritesRequiredFields()
        {
            var e = new GlitchPortEvent { EventId = EventId, Timestamp = Timestamp };

            using var document = JsonDocument.Parse(EventSerializer.Serialize(e, SdkName, SdkVersion));
            var root = document.RootElement;

            Assert.Equal(EventId.ToString("D"), JsonAssert.Property(root, "event_id").GetString());
            Assert.Equal("2026-11-14T06:02:14.010Z", JsonAssert.Property(root, "timestamp").GetString());
            Assert.Equal("info", JsonAssert.Property(root, "level").GetString());
            Assert.Equal("dotnet", JsonAssert.Property(root, "platform").GetString());
            Assert.Equal(SdkName, JsonAssert.Property(JsonAssert.Property(root, "sdk"), "name").GetString());
        }

        [Fact]
        public void Serialize_DefaultEventIsSchemaValid()
        {
            var e = new GlitchPortEvent { Message = "defaults" };

            var json = EventSerializer.Serialize(e, SdkName, SdkVersion);

            JsonAssert.Compact(json);
            ProtocolSchema.AssertValid(json);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(Guid.Empty.ToString("D"), JsonAssert.Property(root, "event_id").GetString());
            Assert.Equal("0001-01-01T00:00:00.000Z", JsonAssert.Property(root, "timestamp").GetString());
            Assert.Equal("dotnet", JsonAssert.Property(root, "platform").GetString());
        }

        [Fact]
        public void Serialize_NullEventThrows()
        {
            Assert.Throws<ArgumentNullException>(() => EventSerializer.Serialize(null!, SdkName, SdkVersion));
        }
    }
}
