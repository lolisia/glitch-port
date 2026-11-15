#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>
    /// Verifies the automatic lifecycle breadcrumb categories, levels and the
    /// idempotent tracker lifecycle.
    /// </summary>
    [TestFixture]
    public sealed class UnityBreadcrumbTests
    {
        private const string PauseHostName = "GlitchPort Breadcrumb Tracker";

        private readonly List<Breadcrumb> _breadcrumbs = new();
        private UnityBreadcrumbTracker? _tracker;

        [TearDown]
        public void TearDown()
        {
            _tracker?.Dispose();
            _tracker = null;
        }

        [Test]
        public void SceneLoadedCreatesASceneBreadcrumb()
        {
            var breadcrumb = UnityBreadcrumbFactory.SceneLoaded("Stage1");

            AssertSceneBreadcrumb(breadcrumb);
            Assert.That(breadcrumb.Message, Does.Contain("Stage1"));
            Assert.That(breadcrumb.Data!["scene"], Is.EqualTo("Stage1"));
        }

        [Test]
        public void SceneUnloadedCreatesASceneBreadcrumb()
        {
            var breadcrumb = UnityBreadcrumbFactory.SceneUnloaded("Stage1");

            AssertSceneBreadcrumb(breadcrumb);
            Assert.That(breadcrumb.Message, Does.Contain("Stage1"));
            Assert.That(breadcrumb.Data!["scene"], Is.EqualTo("Stage1"));
        }

        [Test]
        public void ActiveSceneChangedCreatesASceneBreadcrumbWithBothScenes()
        {
            var breadcrumb = UnityBreadcrumbFactory.ActiveSceneChanged("Stage1", "Stage2");

            AssertSceneBreadcrumb(breadcrumb);
            Assert.That(breadcrumb.Message, Does.Contain("Stage1"));
            Assert.That(breadcrumb.Message, Does.Contain("Stage2"));
            Assert.That(breadcrumb.Data!["previous_scene"], Is.EqualTo("Stage1"));
            Assert.That(breadcrumb.Data!["current_scene"], Is.EqualTo("Stage2"));
        }

        [Test]
        public void ApplicationFocusCreatesALifecycleBreadcrumb()
        {
            var focused = UnityBreadcrumbFactory.ApplicationFocus(true);
            var unfocused = UnityBreadcrumbFactory.ApplicationFocus(false);

            AssertLifecycleBreadcrumb(focused);
            AssertLifecycleBreadcrumb(unfocused);
            Assert.That(focused.Data!["focused"], Is.EqualTo(true));
            Assert.That(unfocused.Data!["focused"], Is.EqualTo(false));
            Assert.That(focused.Message, Is.Not.EqualTo(unfocused.Message));
        }

        [Test]
        public void ApplicationPauseCreatesALifecycleBreadcrumb()
        {
            var paused = UnityBreadcrumbFactory.ApplicationPause(true);
            var resumed = UnityBreadcrumbFactory.ApplicationPause(false);

            AssertLifecycleBreadcrumb(paused);
            AssertLifecycleBreadcrumb(resumed);
            Assert.That(paused.Data!["paused"], Is.EqualTo(true));
            Assert.That(resumed.Data!["paused"], Is.EqualTo(false));
            Assert.That(paused.Message, Is.Not.EqualTo(resumed.Message));
        }

        [Test]
        public void LowMemoryCreatesAWarningDeviceBreadcrumb()
        {
            var breadcrumb = UnityBreadcrumbFactory.LowMemory();

            Assert.That(breadcrumb.Category, Is.EqualTo("device"));
            Assert.That(breadcrumb.Level, Is.EqualTo(Level.Warning));
            Assert.That(breadcrumb.Type, Is.EqualTo("default"));
            Assert.That(breadcrumb.Message, Is.Not.Empty);
            Assert.That(breadcrumb.Data, Is.Not.Null);
            Assert.That(breadcrumb.Data!["low_memory"], Is.EqualTo(true));
        }

        [Test]
        public void LogCreatesALogBreadcrumbWithTheRequestedLevel()
        {
            var breadcrumb = UnityBreadcrumbFactory.Log("something happened", Level.Error);

            Assert.That(breadcrumb.Category, Is.EqualTo("log"));
            Assert.That(breadcrumb.Level, Is.EqualTo(Level.Error));
            Assert.That(breadcrumb.Message, Is.EqualTo("something happened"));
            Assert.That(breadcrumb.Type, Is.EqualTo("default"));
        }

        [Test]
        public void TrackerStartAndDisposeAreIdempotentAndManageTheHiddenPauseHost()
        {
            _tracker = new UnityBreadcrumbTracker(breadcrumb => _breadcrumbs.Add(breadcrumb));

            Assert.DoesNotThrow(() => _tracker.Start());
            Assert.DoesNotThrow(() => _tracker.Start());

            var hosts = FindPauseHosts();
            Assert.That(hosts, Has.Length.EqualTo(1));
            Assert.That(hosts[0].hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));

            Assert.DoesNotThrow(() => _tracker.Dispose());
            Assert.DoesNotThrow(() => _tracker.Dispose());
            Assert.That(FindPauseHosts(), Is.Empty);

            _tracker = null;
        }

        [Test]
        public void HiddenPauseHostForwardsPauseBreadcrumbs()
        {
            _tracker = new UnityBreadcrumbTracker(breadcrumb => _breadcrumbs.Add(breadcrumb));
            _tracker.Start();
            _breadcrumbs.Clear();

            InvokeOnApplicationPause(FindSinglePauseHost(), paused: true);

            Assert.That(_breadcrumbs, Has.Count.EqualTo(1));
            Assert.That(_breadcrumbs[0].Category, Is.EqualTo("app.lifecycle"));
            Assert.That(_breadcrumbs[0].Data!["paused"], Is.EqualTo(true));
        }

        [Test]
        public void TrackerSwallowsAConsumerFailure()
        {
            _tracker = new UnityBreadcrumbTracker(
                _ => throw new InvalidOperationException("consumer failed"));
            _tracker.Start();

            Assert.DoesNotThrow(() => InvokeOnApplicationPause(FindSinglePauseHost(), paused: false));
            Assert.DoesNotThrow(() => _tracker.Dispose());

            _tracker = null;
        }

        private static void AssertSceneBreadcrumb(Breadcrumb breadcrumb)
        {
            Assert.That(breadcrumb.Category, Is.EqualTo("scene"));
            Assert.That(breadcrumb.Level, Is.EqualTo(Level.Info));
            Assert.That(breadcrumb.Type, Is.EqualTo("default"));
            Assert.That(breadcrumb.Message, Is.Not.Empty);
            Assert.That(breadcrumb.Data, Is.Not.Null);
            Assert.That(
                breadcrumb.Timestamp,
                Is.GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        private static void AssertLifecycleBreadcrumb(Breadcrumb breadcrumb)
        {
            Assert.That(breadcrumb.Category, Is.EqualTo("app.lifecycle"));
            Assert.That(breadcrumb.Level, Is.EqualTo(Level.Info));
            Assert.That(breadcrumb.Type, Is.EqualTo("default"));
            Assert.That(breadcrumb.Message, Is.Not.Empty);
            Assert.That(breadcrumb.Data, Is.Not.Null);
        }

        private static GameObject[] FindPauseHosts()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(gameObject => gameObject.name == PauseHostName)
                .ToArray();
        }

        private static GameObject FindSinglePauseHost()
        {
            var hosts = FindPauseHosts();
            Assert.That(hosts, Has.Length.EqualTo(1));
            return hosts[0];
        }

        private static void InvokeOnApplicationPause(GameObject host, bool paused)
        {
            var listener = host.GetComponents<MonoBehaviour>().Single();
            var method = listener
                .GetType()
                .GetMethod("OnApplicationPause", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null, "The hidden pause listener must implement OnApplicationPause.");
            method!.Invoke(listener, new object[] { paused });
        }
    }
}
