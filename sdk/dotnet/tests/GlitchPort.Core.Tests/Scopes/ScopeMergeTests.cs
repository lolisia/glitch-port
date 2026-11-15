using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests.Scopes
{
    public sealed class ScopeMergeTests
    {
        [Fact]
        public async Task CapturedEvent_MergesGlobalAndPushedScopesWithOptions()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options =>
                {
                    options.Release = "star-raiders@1.2.3";
                    options.Environment = "production";
                    options.ServerName = "player-01";
                    options.Platform = "unity";
                });

            client.SetTag("global", "yes");
            client.SetUser(new UserInfo { Id = "player-1" });
            client.SetContext("game", new Dictionary<string, object?> { ["scene"] = "Stage1" });
            client.AddBreadcrumb(new Breadcrumb { Message = "global" });

            using (client.PushScope())
            {
                client.SetTag("pushed", "yes");
                client.SetContext("inner", new Dictionary<string, object?> { ["step"] = 2 });
                client.AddBreadcrumb(new Breadcrumb { Message = "pushed" });
                client.ConfigureScope(scope => scope.Level = Level.Warning);

                await client.CaptureMessageAsync("hello", Level.Error);
            }

            var captured = Assert.Single(relay.Sink.Events);

            Assert.Equal("hello", captured.Message);
            Assert.Equal(Level.Warning, captured.Level);
            Assert.Equal("yes", captured.Tags["global"]);
            Assert.Equal("yes", captured.Tags["pushed"]);
            Assert.Equal("star-raiders@1.2.3", captured.Release);
            Assert.Equal("production", captured.Environment);

            using var document = JsonDocument.Parse(captured.Json);
            var root = document.RootElement;

            Assert.Equal("unity", root.GetProperty("platform").GetString());
            Assert.Equal("player-01", root.GetProperty("server_name").GetString());
            Assert.Equal("player-1", root.GetProperty("user").GetProperty("id").GetString());
            Assert.Equal("Stage1", root.GetProperty("contexts").GetProperty("game").GetProperty("scene").GetString());
            Assert.Equal(2, root.GetProperty("contexts").GetProperty("inner").GetProperty("step").GetInt32());
            Assert.Equal(
                new[] { "global", "pushed" },
                root.GetProperty("breadcrumbs").EnumerateArray()
                    .Select(breadcrumb => breadcrumb.GetProperty("message").GetString()));
        }

        [Fact]
        public async Task CapturedEvent_EventValuesWinOverScopesAndScopeTagsAndContextsAreUnioned()
        {
            using var client = CapturingClient.Create(out var relay);

            client.SetTag("key", "scope");
            client.SetTag("scope-only", "yes");
            client.SetContext(
                "shared",
                new Dictionary<string, object?>
                {
                    ["from_scope"] = "yes",
                    ["same"] = "scope",
                });
            client.ConfigureScope(scope => scope.Fingerprint.Add("scope"));
            using var pushed = client.PushScope();
            client.SetTag("pushed-only", "yes");
            client.SetContext("shared", new Dictionary<string, object?> { ["pushed"] = "yes" });

            var e = new GlitchPortEvent
            {
                Message = "event",
                Level = Level.Fatal,
                Tags =
                {
                    ["key"] = "event",
                    ["event-only"] = "yes",
                },
                Contexts =
                {
                    ["shared"] = new Dictionary<string, object?>
                    {
                        ["same"] = "event",
                        ["from_event"] = "yes",
                    },
                },
                Fingerprint = { "event" },
            };

            await client.CaptureEventAsync(e);

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal(Level.Fatal, captured.Level);
            Assert.Equal("event", captured.Tags["key"]);
            Assert.Equal("yes", captured.Tags["scope-only"]);
            Assert.Equal("yes", captured.Tags["pushed-only"]);
            Assert.Equal("yes", captured.Tags["event-only"]);

            using var document = JsonDocument.Parse(captured.Json);
            var shared = document.RootElement.GetProperty("contexts").GetProperty("shared");
            Assert.Equal("event", shared.GetProperty("same").GetString());
            Assert.Equal("yes", shared.GetProperty("from_scope").GetString());
            Assert.Equal("yes", shared.GetProperty("pushed").GetString());
            Assert.Equal("yes", shared.GetProperty("from_event").GetString());
            Assert.Equal("event", document.RootElement.GetProperty("fingerprint")[0].GetString());
        }

        [Fact]
        public async Task CapturedEvent_ScopeLevelOverridesTheCaptureLevel()
        {
            using var client = CapturingClient.Create(out var relay);
            using var pushed = client.PushScope();
            client.ConfigureScope(scope => scope.Level = Level.Warning);

            await client.CaptureExceptionAsync(new InvalidOperationException("boom"), Level.Fatal);

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal(Level.Warning, captured.Level);
        }

        [Fact]
        public async Task CapturedEvent_EventLevelWinsOverTheScopeLevel()
        {
            // A scope level replaces the capture level, but an event that sets its
            // own level is an event value and therefore wins over the scope.
            using var client = CapturingClient.Create(out var relay);
            using var pushed = client.PushScope();
            client.ConfigureScope(scope => scope.Level = Level.Warning);

            await client.CaptureEventAsync(new GlitchPortEvent
            {
                Message = "event",
                Level = Level.Fatal,
            });

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal(Level.Fatal, captured.Level);
        }

        [Fact]
        public async Task CapturedEvent_BreadcrumbsKeepTheNewestMaxBreadcrumbs()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.MaxBreadcrumbs = 3);

            client.AddBreadcrumb(new Breadcrumb { Message = "global" });

            using (client.PushScope())
            {
                client.AddBreadcrumb(new Breadcrumb { Message = "pushed-1" });
                client.AddBreadcrumb(new Breadcrumb { Message = "pushed-2" });

                var e = new GlitchPortEvent
                {
                    Message = "event",
                    Breadcrumbs =
                    {
                        new Breadcrumb { Message = "event-1" },
                        new Breadcrumb { Message = "event-2" },
                    },
                };

                await client.CaptureEventAsync(e);
            }

            var captured = Assert.Single(relay.Sink.Events);

            using var document = JsonDocument.Parse(captured.Json);
            Assert.Equal(
                new[] { "pushed-2", "event-1", "event-2" },
                document.RootElement.GetProperty("breadcrumbs").EnumerateArray()
                    .Select(breadcrumb => breadcrumb.GetProperty("message").GetString()));
        }

        [Fact]
        public async Task CapturedEvent_DefaultContextsDoNotOverwriteUserValues()
        {
            using var client = CapturingClient.Create(out var relay);
            var e = new GlitchPortEvent
            {
                Message = "event",
                Contexts =
                {
                    ["runtime"] = new Dictionary<string, object?>
                    {
                        ["name"] = "custom-runtime",
                        ["custom"] = "keep",
                    },
                    ["os"] = new Dictionary<string, object?>
                    {
                        ["name"] = "custom-os",
                        ["custom"] = "keep",
                    },
                },
            };

            await client.CaptureEventAsync(e);

            var captured = Assert.Single(relay.Sink.Events);

            using var document = JsonDocument.Parse(captured.Json);
            var runtime = document.RootElement.GetProperty("contexts").GetProperty("runtime");
            var os = document.RootElement.GetProperty("contexts").GetProperty("os");

            Assert.Equal("custom-runtime", runtime.GetProperty("name").GetString());
            Assert.Equal(Environment.Version.ToString(), runtime.GetProperty("version").GetString());
            Assert.Equal(
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                runtime.GetProperty("framework").GetString());
            Assert.Equal("keep", runtime.GetProperty("custom").GetString());

            Assert.Equal("custom-os", os.GetProperty("name").GetString());
            Assert.Equal("keep", os.GetProperty("custom").GetString());
        }

        [Fact]
        public async Task ConfigureScope_UsesTheCurrentPushedScopeAndFallsBackToGlobal()
        {
            using var client = CapturingClient.Create(out var relay);

            client.ConfigureScope(scope => scope.SetTag("where", "global"));

            using (client.PushScope())
            {
                client.ConfigureScope(scope => scope.SetTag("where", "pushed"));
                await client.CaptureMessageAsync("inside");
            }

            await client.CaptureMessageAsync("outside");

            var events = relay.Sink.Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("pushed", events[0].Tags["where"]);
            Assert.Equal("global", events[1].Tags["where"]);
        }

        [Fact]
        public async Task NestedScopes_MergeFromOutermostToInnermost()
        {
            using var client = CapturingClient.Create(out var relay);
            client.SetTag("level", "global");

            using (client.PushScope())
            {
                client.SetTag("level", "outer");
                client.SetTag("outer", "yes");

                using (client.PushScope())
                {
                    client.SetTag("level", "inner");
                    client.SetTag("inner", "yes");
                    await client.CaptureMessageAsync("nested");
                }

                await client.CaptureMessageAsync("outer");
            }

            var events = relay.Sink.Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("inner", events[0].Tags["level"]);
            Assert.Equal("yes", events[0].Tags["outer"]);
            Assert.Equal("yes", events[0].Tags["inner"]);
            Assert.Equal("outer", events[1].Tags["level"]);
            Assert.Equal("yes", events[1].Tags["outer"]);
            Assert.False(events[1].Tags.ContainsKey("inner"));
        }

        [Fact]
        public async Task BeforeSend_CanModifyTheEvent()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.BeforeSend = e =>
                {
                    e.Tags["before_send"] = "yes";
                    e.Message = "changed";
                    return e;
                });

            await client.CaptureMessageAsync("original");

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal("changed", captured.Message);
            Assert.Equal("yes", captured.Tags["before_send"]);
        }

        [Fact]
        public async Task BeforeSend_ReturningNullDropsTheEvent()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.BeforeSend = _ => null);

            await client.CaptureMessageAsync("dropped");

            Assert.Empty(relay.Sink.Events);
        }

        [Fact]
        public async Task BeforeSend_WhenItThrows_KeepsTheOriginalEvent()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.BeforeSend = _ => throw new InvalidOperationException("hook failed"));

            await client.CaptureMessageAsync("still here");

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal("still here", captured.Message);
        }

        [Fact]
        public async Task BeforeSend_RunsAfterScopesAndDefaultsHaveBeenApplied()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options =>
                {
                    options.Release = "release";
                    options.BeforeSend = e =>
                    {
                        Assert.Equal("release", e.Release);
                        Assert.Equal("scope", e.Tags["scope"]);
                        Assert.True(e.Contexts.ContainsKey("runtime"));
                        Assert.True(e.Contexts.ContainsKey("os"));
                        Assert.NotNull(e.User);
                        return e;
                    };
                });

            client.SetTag("scope", "yes");
            client.SetUser(new UserInfo { Id = "player-1" });

            await client.CaptureMessageAsync("hello");

            Assert.Single(relay.Sink.Events);
        }

        [Fact]
        public async Task SampleRateZero_DropsEveryEvent()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.SampleRate = 0d);

            await client.CaptureMessageAsync("dropped");

            Assert.Empty(relay.Sink.Events);
        }

        [Fact]
        public async Task SampleRate_IsAppliedBeforeBeforeSend()
        {
            var beforeSendCalls = 0;

            using var client = CapturingClient.Create(
                out var relay,
                options =>
                {
                    options.SampleRate = 0d;
                    options.BeforeSend = e =>
                    {
                        beforeSendCalls++;
                        return e;
                    };
                });

            await client.CaptureMessageAsync("dropped");

            Assert.Equal(0, beforeSendCalls);
            Assert.Empty(relay.Sink.Events);
        }

        [Fact]
        public async Task SampleRateOne_SendsEveryEvent()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.SampleRate = 1d);

            await client.CaptureMessageAsync("kept");

            Assert.Single(relay.Sink.Events);
        }

        [Fact]
        public async Task SendDefaultPiiFalse_StripsTheIpAddressFromTheUser()
        {
            using var client = CapturingClient.Create(out var relay);
            var user = new UserInfo { Id = "player-1", IpAddress = "203.0.113.5" };
            client.SetUser(user);

            await client.CaptureMessageAsync("hello");

            var captured = Assert.Single(relay.Sink.Events);
            using var document = JsonDocument.Parse(captured.Json);
            var serializedUser = document.RootElement.GetProperty("user");
            Assert.Equal("player-1", serializedUser.GetProperty("id").GetString());
            Assert.False(serializedUser.TryGetProperty("ip_address", out _));

            // The scope keeps its own user object untouched.
            Assert.Equal("203.0.113.5", user.IpAddress);
        }

        [Fact]
        public async Task SendDefaultPiiTrue_KeepsTheIpAddress()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.SendDefaultPii = true);
            client.SetUser(new UserInfo { Id = "player-1", IpAddress = "203.0.113.5" });

            await client.CaptureMessageAsync("hello");

            var captured = Assert.Single(relay.Sink.Events);
            using var document = JsonDocument.Parse(captured.Json);
            Assert.Equal(
                "203.0.113.5",
                document.RootElement.GetProperty("user").GetProperty("ip_address").GetString());
        }

        [Fact]
        public async Task SendDefaultPiiFalse_DoesNotMutateAUserObjectSetOnTheEvent()
        {
            using var client = CapturingClient.Create(out var relay);
            var user = new UserInfo { Id = "player-1", IpAddress = "203.0.113.5" };

            await client.CaptureEventAsync(new GlitchPortEvent
            {
                Message = "hello",
                User = user,
            });

            var captured = Assert.Single(relay.Sink.Events);
            using var document = JsonDocument.Parse(captured.Json);
            Assert.False(
                document.RootElement.GetProperty("user").TryGetProperty("ip_address", out _));

            // Stripping is applied to a copy; the caller keeps its own object.
            Assert.Equal("203.0.113.5", user.IpAddress);
        }

        [Fact]
        public async Task EventUser_WinsOverTheScopeUser()
        {
            using var client = CapturingClient.Create(out var relay);
            client.SetUser(new UserInfo { Id = "scope-user", Username = "scope" });

            await client.CaptureEventAsync(new GlitchPortEvent
            {
                Message = "hello",
                User = new UserInfo { Id = "event-user" },
            });

            var captured = Assert.Single(relay.Sink.Events);
            using var document = JsonDocument.Parse(captured.Json);
            var serializedUser = document.RootElement.GetProperty("user");
            Assert.Equal("event-user", serializedUser.GetProperty("id").GetString());
            Assert.False(serializedUser.TryGetProperty("username", out _));
        }

        [Fact]
        public async Task SendDefaultPiiFalse_StripsAnIpAddressSetByBeforeSend()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options => options.BeforeSend = e =>
                {
                    e.User = new UserInfo { Id = "player-1", IpAddress = "203.0.113.5" };
                    return e;
                });

            await client.CaptureMessageAsync("hello");

            var captured = Assert.Single(relay.Sink.Events);
            using var document = JsonDocument.Parse(captured.Json);
            Assert.False(
                document.RootElement.GetProperty("user").TryGetProperty("ip_address", out _));
        }

        [Fact]
        public async Task Options_ReleaseAndEnvironment_FillAnEventThatDidNotSetThem()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options =>
                {
                    options.Release = "mygame@1.2.3";
                    options.Environment = "staging";
                });

            await client.CaptureMessageAsync("hello");

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal("mygame@1.2.3", captured.Release);
            Assert.Equal("staging", captured.Environment);

            using var document = JsonDocument.Parse(captured.Json);
            Assert.Equal("mygame@1.2.3", document.RootElement.GetProperty("release").GetString());
            Assert.Equal("staging", document.RootElement.GetProperty("environment").GetString());
        }

        [Fact]
        public async Task Options_DoNotOverwriteEventReleaseAndEnvironment()
        {
            using var client = CapturingClient.Create(
                out var relay,
                options =>
                {
                    options.Release = "option-release";
                    options.Environment = "option-environment";
                });

            await client.CaptureEventAsync(new GlitchPortEvent
            {
                Message = "hello",
                Release = "event-release",
                Environment = "event-environment",
            });

            var captured = Assert.Single(relay.Sink.Events);
            Assert.Equal("event-release", captured.Release);
            Assert.Equal("event-environment", captured.Environment);
        }
    }
}
