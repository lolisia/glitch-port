#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Text;
using GlitchPort;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GlitchPort.Unity.Tests.Runtime
{
    /// <summary>
    /// Verifies the runtime API end to end: explicit initialization, implicit
    /// Unity log capture, delivery to a local file relay, close, and
    /// re-initialization inside the player loop.
    /// </summary>
    [TestFixture]
    public sealed class GlitchPortUnityPlayModeTests
    {
        private const string LogErrorMessage = "playmode unity error";
        private const string ExceptionMessage = "playmode exception";
        private const string CaptureMessageText = "playmode capture message";
        private const string NdjsonPattern = "glitchport-*.ndjson";

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

            var flushTask = GlitchPortUnity.FlushAsync(TimeSpan.FromSeconds(10));
            Assert.That(flushTask, Is.Not.Null);

            var flushDeadline = Time.realtimeSinceStartup + 10f;
            while (!flushTask.IsCompleted && Time.realtimeSinceStartup < flushDeadline)
            {
                yield return null;
            }

            Assert.That(flushTask.IsCompleted, Is.True, "FlushAsync did not complete within ten seconds.");
            Assert.That(flushTask.IsFaulted, Is.False, flushTask.Exception?.ToString());

            var report = flushTask.GetAwaiter().GetResult();
            Assert.That(report, Is.Not.Null);
            Assert.That(report!.AllSucceeded, Is.True);

            var ndjson = ReadNdjson();
            Assert.That(ndjson, Does.Contain("\"level\":\"error\""));
            Assert.That(ndjson, Does.Contain("\"name\":\"glitchport.unity\""));
            Assert.That(ndjson, Does.Contain("\"version\":\"" + GlitchPortUnity.PackageVersion + "\""));
            Assert.That(ndjson, Does.Contain(LogErrorMessage));
            Assert.That(ndjson, Does.Contain(ExceptionMessage));
            Assert.That(ndjson, Does.Contain(CaptureMessageText));

            GlitchPortUnity.Close();
            Assert.That(GlitchPortUnity.IsInitialized, Is.False);
            Assert.That(GlitchPortUnity.CaptureMessage("after close"), Is.Null);
            Assert.That(GlitchPortUnity.CaptureException(new InvalidOperationException("after close")), Is.Null);
            Assert.That(GlitchPortUnity.FlushAsync().GetAwaiter().GetResult(), Is.Null);

            Assert.That(GlitchPortUnity.Init(options), Is.True);
            Assert.That(GlitchPortUnity.IsInitialized, Is.True);
            Assert.That(GlitchPortUnity.Client, Is.Not.SameAs(firstClient));
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
    }
}
