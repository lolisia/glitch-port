using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests.Scopes
{
    public sealed class ScopeIsolationTests
    {
        [Fact]
        public async Task ParallelTasks_PushTheirOwnScopesWhileGlobalValuesStillMerge()
        {
            using var client = CapturingClient.Create(out var relay);
            client.SetTag("global", "yes");
            client.SetContext("global", new Dictionary<string, object?> { ["value"] = "yes" });

            using var ready = new CountdownEvent(2);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task RunAsync(string name)
            {
                using (client.PushScope())
                {
                    client.SetTag("task", name);
                    client.SetContext("task", new Dictionary<string, object?> { ["name"] = name });
                    client.AddBreadcrumb(new Breadcrumb { Message = name });

                    ready.Signal();
                    await release.Task;

                    // The continuation may run on a different thread; the scope
                    // must still be the one pushed by this flow.
                    await client.CaptureMessageAsync(name);
                }
            }

            var first = Task.Run(() => RunAsync("first"));
            var second = Task.Run(() => RunAsync("second"));

            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Both flows did not push their scope.");
            release.TrySetResult(true);

            await Task.WhenAll(first, second);

            var events = relay.Sink.Events;
            Assert.Equal(2, events.Count);

            var firstEvent = Assert.Single(events, e => e.Message == "first");
            var secondEvent = Assert.Single(events, e => e.Message == "second");

            Assert.Equal("yes", firstEvent.Tags["global"]);
            Assert.Equal("yes", secondEvent.Tags["global"]);
            Assert.Equal("first", firstEvent.Tags["task"]);
            Assert.Equal("second", secondEvent.Tags["task"]);
            Assert.Equal("first", ReadContext(firstEvent, "task", "name"));
            Assert.Equal("second", ReadContext(secondEvent, "task", "name"));

            Assert.Equal(new[] { "first" }, ReadBreadcrumbs(firstEvent));
            Assert.Equal(new[] { "second" }, ReadBreadcrumbs(secondEvent));
        }

        [Fact]
        public async Task PushedScope_IsRestoredAfterItIsDisposed()
        {
            using var client = CapturingClient.Create(out var relay);

            using (client.PushScope())
            {
                client.SetTag("pushed", "yes");
                await client.CaptureMessageAsync("inside");
            }

            await client.CaptureMessageAsync("after");

            var events = relay.Sink.Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("yes", events[0].Tags["pushed"]);
            Assert.False(events[1].Tags.ContainsKey("pushed"));
        }

        [Fact]
        public async Task PushedScope_IsRestoredAfterAnAwait()
        {
            using var client = CapturingClient.Create(out var relay);

            using (client.PushScope())
            {
                client.SetTag("pushed", "yes");
                await Task.Yield();
                await client.CaptureMessageAsync("inside");
            }

            await client.CaptureMessageAsync("after");

            var events = relay.Sink.Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("yes", events[0].Tags["pushed"]);
            Assert.False(events[1].Tags.ContainsKey("pushed"));
        }

        private static string? ReadContext(CapturedEvent e, string context, string key)
        {
            using var document = JsonDocument.Parse(e.Json);
            return document.RootElement
                .GetProperty("contexts")
                .GetProperty(context)
                .GetProperty(key)
                .GetString();
        }

        private static string[] ReadBreadcrumbs(CapturedEvent e)
        {
            using var document = JsonDocument.Parse(e.Json);

            if (!document.RootElement.TryGetProperty("breadcrumbs", out var breadcrumbs))
            {
                return Array.Empty<string>();
            }

            return breadcrumbs.EnumerateArray()
                .Select(breadcrumb => breadcrumb.GetProperty("message").GetString() ?? string.Empty)
                .ToArray();
        }
    }
}
