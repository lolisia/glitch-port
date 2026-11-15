#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using GlitchPort;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GlitchPort.Unity.Tests.Runtime
{
    /// <summary>
    /// Verifies the runtime API end to end: explicit initialization, implicit
    /// Unity log capture, structured exceptions with contexts, delivery to a
    /// local file relay, scope helpers, PII rules, close, and re-initialization
    /// inside the player loop.
    /// </summary>
    [TestFixture]
    public sealed class GlitchPortUnityPlayModeTests
    {
        private const string LogErrorMessage = "playmode unity error";
        private const string ExceptionMessage = "playmode exception";
        private const string CaptureMessageText = "playmode capture message";
        private const string NdjsonPattern = "glitchport-*.ndjson";
        private const string TestSceneName = "GlitchPort PlayMode Test Scene";

        private string _temporaryDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            // PlayMode entry may have auto-initialized the SDK before the test
            // runner started; every test begins from a clean state.
            GlitchPortUnity.Close();

            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "glitchport-unity-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            GlitchPortUnity.Close();

            try
            {
                if (Directory.Exists(_temporaryDirectory))
                {
                    Directory.Delete(_temporaryDirectory, recursive: true);
                }
            }
            catch (Exception)
            {
                // A leftover temporary directory must not fail the test run.
            }
        }

        [Test]
        public void CaptureAndFlushReturnNullBeforeInitialization()
        {
            Assert.That(GlitchPortUnity.IsInitialized, Is.False);
            Assert.That(GlitchPortUnity.Client, Is.Null);
            Assert.That(GlitchPortUnity.CaptureMessage("before init"), Is.Null);
            Assert.That(GlitchPortUnity.CaptureMessage(null!), Is.Null);
            Assert.That(GlitchPortUnity.CaptureException(new InvalidOperationException("before init")), Is.Null);
            Assert.That(GlitchPortUnity.CaptureException(null!), Is.Null);

            var flushTask = GlitchPortUnity.FlushAsync(TimeSpan.FromSeconds(1));
            Assert.That(flushTask, Is.Not.Null);
            Assert.That(flushTask.GetAwaiter().GetResult(), Is.Null);
        }

        [Test]
        public void InitWithNullOptionsReturnsFalse()
        {
            Assert.That(GlitchPortUnity.Init((GlitchPortOptions)null!), Is.False);
            Assert.That(GlitchPortUnity.IsInitialized, Is.False);
        }

        [Test]
        public void InitDoesNotReplaceAnExistingClient()
        {
            var options = CreateFileOptions();

            Assert.That(GlitchPortUnity.Init(options), Is.True);
            var client = GlitchPortUnity.Client;
            Assert.That(client, Is.Not.Null);

            Assert.That(GlitchPortUnity.Init(CreateFileOptions()), Is.True);
            Assert.That(GlitchPortUnity.Client, Is.SameAs(client));
        }

        [Test]
        public void CloseIsIdempotentAndAllowsReinitialization()
        {
            var options = CreateFileOptions();

            Assert.That(GlitchPortUnity.Init(options), Is.True);
            Assert.DoesNotThrow(() => GlitchPortUnity.Close());
            Assert.DoesNotThrow(() => GlitchPortUnity.Close());
            Assert.That(GlitchPortUnity.IsInitialized, Is.False);
            Assert.That(GlitchPortUnity.Client, Is.Null);

            Assert.That(GlitchPortUnity.Init(options), Is.True);
            Assert.That(GlitchPortUnity.IsInitialized, Is.True);
        }

        [UnityTest]
        public IEnumerator ExplicitInitWritesErrorEventsToFileRelayAndCanReinitialize()
        {
            // This scope is the one UnityLogCheckDelegatingCommand created for
            // this test; setting the flag in [SetUp] would only affect the outer
            // scope and the intentionally logged error below would fail the test.
            LogAssert.ignoreFailingMessages = true;

            Assert.That(GlitchPortUnity.IsInitialized, Is.False);
            Assert.That(GlitchPortUnity.CaptureMessage("before init"), Is.Null);
            Assert.That(GlitchPortUnity.FlushAsync().GetAwaiter().GetResult(), Is.Null);

            var options = CreateFileOptions();
            Assert.That(GlitchPortUnity.Init(options), Is.True);
            Assert.That(GlitchPortUnity.IsInitialized, Is.True);
            Assert.That(GlitchPortUnity.Client, Is.Not.Null);
            var firstClient = GlitchPortUnity.Client;

            // Unity delivers logs through Application.logMessageReceivedThreaded.
            // Wait until the threaded callback has enqueued (and the file relay
            // has written) the error before adding the directly captured events.
            Debug.LogError(LogErrorMessage);
            yield return null;

            var logDeadline = Time.realtimeSinceStartup + 5f;
            while (!ReadNdjson().Contains(LogErrorMessage)
                && Time.realtimeSinceStartup < logDeadline)
            {
                yield return null;
            }

            Assert.That(
                GlitchPortUnity.CaptureException(new InvalidOperationException(ExceptionMessage)),
                Is.Not.Null);
            Assert.That(GlitchPortUnity.CaptureMessage(CaptureMessageText), Is.Not.Null);

            yield return FlushUnity();

            var ndjson = ReadNdjson();
            Assert.That(ndjson, Does.Contain("\"level\":\"error\""));
            Assert.That(ndjson, Does.Contain("\"name\":\"glitchport.unity\""));
            Assert.That(ndjson, Does.Contain("\"version\":\"" + GlitchPortUnity.PackageVersion + "\""));
            Assert.That(ndjson, Does.Contain(LogErrorMessage));
            Assert.That(ndjson, Does.Contain(ExceptionMessage));
            Assert.That(ndjson, Does.Contain(CaptureMessageText));
            Assert.That(ndjson, Does.Contain("\"type\":\"LogError\""));

            GlitchPortUnity.Close();
            Assert.That(GlitchPortUnity.IsInitialized, Is.False);
            Assert.That(GlitchPortUnity.CaptureMessage("after close"), Is.Null);
            Assert.That(GlitchPortUnity.CaptureException(new InvalidOperationException("after close")), Is.Null);
            Assert.That(GlitchPortUnity.FlushAsync().GetAwaiter().GetResult(), Is.Null);

            Assert.That(GlitchPortUnity.Init(options), Is.True);
            Assert.That(GlitchPortUnity.IsInitialized, Is.True);
            Assert.That(GlitchPortUnity.Client, Is.Not.SameAs(firstClient));
        }

        [UnityTest]
        public IEnumerator StructuredExceptionsCarryContextsPlatformReleaseAndSceneBreadcrumbs()
        {
            LogAssert.ignoreFailingMessages = true;

            Assert.That(InitFromSettingsPath(), Is.True);

            var previousScene = SceneManager.GetActiveScene();
            var testScene = SceneManager.CreateScene(TestSceneName);
            Assert.That(testScene.IsValid(), Is.True);
            Assert.That(SceneManager.SetActiveScene(testScene), Is.True);

            // Give the lifecycle tracker a frame so the scene breadcrumb is
            // recorded before the event below.
            yield return null;

            // The trace of an unthrown exception starts at this Debug.LogException
            // call, so the innermost frames end on this test method.
            Debug.LogException(new InvalidOperationException("x", new ArgumentException("inner")));

            yield return null;

            // Restore the previous scene before asserting so a failing assertion
            // cannot leak the extra scene into the following tests.
            if (previousScene.IsValid())
            {
                SceneManager.SetActiveScene(previousScene);
            }

            var unload = SceneManager.UnloadSceneAsync(testScene);
            if (unload is not null && !unload.isDone)
            {
                yield return unload;
            }

            yield return FlushUnity();

            var line = FindEventLine(
                ReadNdjson(),
                "\"logger\":\"unity\"",
                "ArgumentException",
                "InvalidOperationException");

            Assert.That(line, Does.Contain("\"level\":\"error\""));
            Assert.That(line, Does.Contain("\"platform\":\"unity\""));

            var expectedRelease = Application.productName + "@" + Application.version;
            Assert.That(line, Does.Contain("\"release\":\"" + expectedRelease + "\""));
            Assert.That(line, Does.Contain("\"environment\":\"editor\""));

            // The exception array is ordered innermost first.
            var innerIndex = line.IndexOf("\"type\":\"ArgumentException\"", StringComparison.Ordinal);
            var outerIndex = line.IndexOf("InvalidOperationException", StringComparison.Ordinal);
            Assert.That(innerIndex, Is.GreaterThanOrEqualTo(0), "The inner ArgumentException is missing.");
            Assert.That(
                outerIndex,
                Is.GreaterThan(innerIndex),
                "The inner exception must appear before the outer InvalidOperationException.");

            var exceptions = ExceptionArray(line);
            Assert.That(exceptions, Does.Contain("\"type\":\"ArgumentException\""));
            Assert.That(exceptions, Does.Contain("\"value\":\"inner\""));
            Assert.That(
                exceptions.IndexOf("\"type\":\"ArgumentException\"", StringComparison.Ordinal),
                Is.LessThan(exceptions.IndexOf("InvalidOperationException", StringComparison.Ordinal)),
                "The inner exception must be serialized before the outer exception.");
            Assert.That(
                exceptions,
                Does.Contain("\"stacktrace\":{\"frames\":["),
                "The exception chain must carry stack frames.");
            Assert.That(
                LastFrameObject(exceptions),
                Does.Contain("GlitchPortUnityPlayModeTests.cs"),
                "The last frame must be on the test method side.");

            Assert.That(line, Does.Contain("\"contexts\":{"));
            Assert.That(line, Does.Contain("\"os\":{"));
            Assert.That(line, Does.Contain("\"device\":{"));
            Assert.That(line, Does.Contain("\"gpu\":{"));
            Assert.That(line, Does.Contain("\"runtime\":{"));
            Assert.That(line, Does.Contain("\"app\":{"));
            Assert.That(line, Does.Contain("\"game\":"));

            Assert.That(line, Does.Contain("\"unity.platform\":"));
            Assert.That(line, Does.Contain("\"unity.scene\":"));

            // Switching the active scene must have produced a scene breadcrumb
            // on the same event.
            Assert.That(line, Does.Contain("\"category\":\"scene\""));
            Assert.That(line, Does.Contain(TestSceneName));
        }

        [UnityTest]
        public IEnumerator PublicScopeApiDelegatesSafelyBeforeAndAfterInitialization()
        {
            // Before initialization every helper is a no-op.
            Assert.DoesNotThrow(() => GlitchPortUnity.SetTag("before", "value"));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetUser(new UserInfo { Id = "before" }));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetUser(null));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetContext(
                "before",
                new Dictionary<string, object?> { ["value"] = 1 }));
            Assert.DoesNotThrow(() => GlitchPortUnity.AddBreadcrumb(new Breadcrumb { Message = "before" }));
            Assert.That(GlitchPortUnity.CaptureMessage("scope before init"), Is.Null);

            Assert.That(GlitchPortUnity.Init(CreateFileOptions()), Is.True);

            Assert.DoesNotThrow(() => GlitchPortUnity.SetTag("test.tag", "tag-value"));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetUser(
                new UserInfo { Id = "player-1", Username = "tester" }));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetContext(
                "test.context",
                new Dictionary<string, object?> { ["answer"] = 42 }));
            Assert.DoesNotThrow(() => GlitchPortUnity.AddBreadcrumb(new Breadcrumb
            {
                Category = "test",
                Message = "custom breadcrumb",
                Level = Level.Info,
            }));

            // Null arguments and provider failures are swallowed.
            Assert.DoesNotThrow(() => GlitchPortUnity.SetTag(null!, "value"));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetUser(null));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetContext(null!, new Dictionary<string, object?>()));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetContext("test.context", null!));
            Assert.DoesNotThrow(() => GlitchPortUnity.AddBreadcrumb(null!));

            Assert.That(GlitchPortUnity.CaptureMessage("scope api"), Is.Not.Null);

            yield return FlushUnity();

            var line = FindEventLine(ReadNdjson(), "scope api");
            Assert.That(line, Does.Contain("\"test.tag\":\"tag-value\""));
            Assert.That(line, Does.Contain("\"test.context\":{"));
            Assert.That(line, Does.Contain("\"answer\":42"));
            Assert.That(line, Does.Contain("\"player-1\""));
            Assert.That(line, Does.Contain("\"custom breadcrumb\""));

            GlitchPortUnity.Close();

            Assert.DoesNotThrow(() => GlitchPortUnity.SetTag("after", "value"));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetUser(new UserInfo { Id = "after" }));
            Assert.DoesNotThrow(() => GlitchPortUnity.SetContext(
                "after",
                new Dictionary<string, object?>()));
            Assert.DoesNotThrow(() => GlitchPortUnity.AddBreadcrumb(new Breadcrumb { Message = "after" }));
        }

        [UnityTest]
        public IEnumerator ConcurrentScopeWritesAndCapturesStayConsistent()
        {
            Assert.That(GlitchPortUnity.Init(CreateFileOptions()), Is.True);

            var failures = new List<string>();
            var workers = new List<Thread>();
            for (var worker = 0; worker < 3; worker++)
            {
                var workerId = worker;
                var thread = new Thread(() =>
                {
                    for (var iteration = 0; iteration < 40; iteration++)
                    {
                        try
                        {
                            GlitchPortUnity.SetTag("thread." + workerId, iteration.ToString());
                            GlitchPortUnity.SetContext(
                                "thread." + workerId,
                                new Dictionary<string, object?> { ["iteration"] = iteration });
                            GlitchPortUnity.AddBreadcrumb(new Breadcrumb
                            {
                                Category = "test",
                                Message = "thread " + workerId + " " + iteration,
                                Level = Level.Info,
                            });

                            if (GlitchPortUnity.CaptureMessage(
                                    "concurrent " + workerId + " " + iteration) is null)
                            {
                                lock (failures)
                                {
                                    failures.Add("capture " + workerId + " " + iteration);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (failures)
                            {
                                failures.Add(ex.ToString());
                            }
                        }
                    }
                });

                workers.Add(thread);
                thread.Start();
            }

            // Keep capturing on the main thread while the workers mutate the
            // scope, which is the interleaving the lock has to protect.
            while (workers.Exists(thread => !thread.Join(0)))
            {
                GlitchPortUnity.CaptureMessage("main concurrent capture");
                yield return null;
            }

            Assert.That(failures, Is.Empty);
            Assert.That(GlitchPortUnity.IsInitialized, Is.True);

            Assert.That(GlitchPortUnity.CaptureMessage("concurrent writers done"), Is.Not.Null);

            yield return FlushUnity();

            var line = FindEventLine(ReadNdjson(), "concurrent writers done");
            Assert.That(line, Does.Contain("\"thread.0\":\"39\""));
            Assert.That(line, Does.Contain("\"thread.1\":\"39\""));
            Assert.That(line, Does.Contain("\"thread.2\":\"39\""));
            Assert.That(line, Does.Contain("\"thread.0\":{"));
        }

        [UnityTest]
        public IEnumerator PiiUserIsOnlyAttachedWhenSendDefaultPiiIsEnabled()
        {
            Assert.That(GlitchPortUnity.Init(CreateConfiguredOptions(sendDefaultPii: false)), Is.True);
            Assert.That(GlitchPortUnity.CaptureMessage("without pii"), Is.Not.Null);
            yield return FlushUnity();

            var withoutPii = FindEventLine(ReadNdjson(), "without pii");
            Assert.That(withoutPii, Does.Not.Contain("\"user\":"));

            GlitchPortUnity.Close();

            Assert.That(GlitchPortUnity.Init(CreateConfiguredOptions(sendDefaultPii: true)), Is.True);
            Assert.That(GlitchPortUnity.CaptureMessage("with pii"), Is.Not.Null);
            yield return FlushUnity();

            var withPii = FindEventLine(ReadNdjson(), "with pii");
            Assert.That(withPii, Does.Contain("\"user\":{\"id\":\""));
        }

        /// <summary>
        /// Initializes through the settings path (so the production platform
        /// info, release and environment defaults are exercised) while pointing
        /// the environment relay at the test directory.
        /// </summary>
        private bool InitFromSettingsPath()
        {
            const string dsnVariable = "GLITCHPORT_DSN";
            var previous = Environment.GetEnvironmentVariable(dsnVariable);
            try
            {
                var relayConfig = "file:?dir="
                    + Uri.EscapeDataString(_temporaryDirectory)
                    + "&min_level=debug";
                Environment.SetEnvironmentVariable(dsnVariable, relayConfig);
                return GlitchPortUnity.Init();
            }
            finally
            {
                Environment.SetEnvironmentVariable(dsnVariable, previous);
            }
        }

        private GlitchPortOptions CreateFileOptions()
        {
            var options = new GlitchPortOptions();
            options.Relays.Add(Relay.File(relay =>
            {
                relay.Directory = _temporaryDirectory;
                relay.MinLevel = Level.Debug;
            }));

            return options;
        }

        /// <summary>
        /// Builds options through the settings path so the default release and
        /// environment are exercised, with the file relay pointed at the test
        /// directory.
        /// </summary>
        private GlitchPortOptions CreateConfiguredOptions(bool sendDefaultPii)
        {
            var settings = ScriptableObject.CreateInstance<GlitchPortSettings>();
            try
            {
                settings.Dsn = string.Empty;
                settings.FileMode = GlitchPort.Unity.FileMode.On;
                settings.FileDirectory = _temporaryDirectory;
                settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
                settings.SendDefaultPii = sendDefaultPii;

                var options = GlitchPortUnityConfig.BuildOptions(
                    settings,
                    CreatePlatformInfo(),
                    environmentDsn: null);

                Assert.That(options, Is.Not.Null);
                return options!;
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static UnityPlatformInfo CreatePlatformInfo()
        {
            var platform = Application.platform;

            return new UnityPlatformInfo(
                Application.isEditor,
                Debug.isDebugBuild,
                platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer,
                canStartProcess: false,
                platform != RuntimePlatform.WebGLPlayer,
                Application.persistentDataPath,
                Application.productName,
                GlitchPortUnity.PackageVersion,
                terminalExecutablePath: null,
                productVersion: Application.version);
        }

        private IEnumerator FlushUnity()
        {
            var flushTask = GlitchPortUnity.FlushAsync(TimeSpan.FromSeconds(10));
            Assert.That(flushTask, Is.Not.Null);

            var flushDeadline = Time.realtimeSinceStartup + 10f;
            while (!flushTask!.IsCompleted && Time.realtimeSinceStartup < flushDeadline)
            {
                yield return null;
            }

            Assert.That(flushTask!.IsCompleted, Is.True, "FlushAsync did not complete within ten seconds.");
            Assert.That(flushTask.IsFaulted, Is.False, flushTask.Exception?.ToString());

            var report = flushTask.GetAwaiter().GetResult();
            Assert.That(report, Is.Not.Null);
            Assert.That(report!.AllSucceeded, Is.True);
        }

        private string ReadNdjson()
        {
            if (!Directory.Exists(_temporaryDirectory))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var files = Directory.GetFiles(_temporaryDirectory, NdjsonPattern);
            for (var i = 0; i < files.Length; i++)
            {
                try
                {
                    builder.Append(File.ReadAllText(files[i]));
                    builder.Append('\n');
                }
                catch (IOException)
                {
                    // The relay may still be writing; the next poll reads it again.
                }
            }

            return builder.ToString();
        }

        private static string FindEventLine(string ndjson, params string[] fragments)
        {
            var lines = ndjson.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var matches = true;
                for (var j = 0; j < fragments.Length; j++)
                {
                    if (line.IndexOf(fragments[j], StringComparison.Ordinal) < 0)
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return line;
                }
            }

            Assert.Fail("No event line contained: " + string.Join(", ", fragments));
            return string.Empty;
        }

        /// <summary>
        /// Extracts the <c>exception</c> array of an event line, including its
        /// brackets.
        /// </summary>
        private static string ExceptionArray(string eventLine)
        {
            const string prefix = "\"exception\":[";
            var start = eventLine.IndexOf(prefix, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "The event has no exception array.");
            start += prefix.Length - 1;

            var depth = 0;
            var inString = false;
            for (var i = start; i < eventLine.Length; i++)
            {
                var character = eventLine[i];
                if (inString)
                {
                    if (character == '\\')
                    {
                        i++;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return eventLine.Substring(start, i - start + 1);
                    }
                }
            }

            Assert.Fail("The exception array was not terminated.");
            return string.Empty;
        }

        /// <summary>
        /// Extracts the last stack frame object of the last exception that has
        /// frames.
        /// </summary>
        private static string LastFrameObject(string json)
        {
            const string marker = "\"stacktrace\":{\"frames\":[";
            var index = json.LastIndexOf(marker, StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "The exception chain has no stack frames.");

            var start = index + marker.Length;
            string? lastFrame = null;
            var frameStart = -1;
            var depth = 0;
            var inString = false;

            for (var i = start; i < json.Length; i++)
            {
                var character = json[i];
                if (inString)
                {
                    if (character == '\\')
                    {
                        i++;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '{')
                {
                    if (depth == 0)
                    {
                        frameStart = i;
                    }

                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0 && frameStart >= 0)
                    {
                        lastFrame = json.Substring(frameStart, i - frameStart + 1);
                        frameStart = -1;
                    }
                }
                else if (character == ']' && depth == 0)
                {
                    break;
                }
            }

            Assert.That(lastFrame, Is.Not.Null, "The exception chain has no stack frame objects.");
            return lastFrame!;
        }
    }
}
