#nullable enable
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>Verifies every Unity log type and capture flag mapping.</summary>
    [TestFixture]
    public sealed class UnityLogBridgeTests
    {
        private GlitchPortSettings _settings = null!;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<GlitchPortSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
            {
                Object.DestroyImmediate(_settings);
            }
        }

        [Test]
        public void ExceptionMapsToErrorAndAppendsStackTrace()
        {
            var captured = TryMap("boom", " at Game.Update()", LogType.Exception, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Error));
            Assert.That(message, Is.EqualTo("boom\n at Game.Update()"));
        }

        [Test]
        public void ExceptionIsIgnoredWhenCaptureExceptionsIsOff()
        {
            _settings.CaptureExceptions = false;

            Assert.That(TryMap("boom", " at Game.Update()", LogType.Exception), Is.False);
        }

        [Test]
        public void ErrorMapsToErrorAndAppendsStackTrace()
        {
            var captured = TryMap("failed", " at Game.Start()", LogType.Error, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Error));
            Assert.That(message, Is.EqualTo("failed\n at Game.Start()"));
        }

        [Test]
        public void ErrorIsIgnoredWhenCaptureErrorsIsOff()
        {
            _settings.CaptureErrors = false;

            Assert.That(TryMap("failed", " at Game.Start()", LogType.Error), Is.False);
        }

        [Test]
        public void AssertMapsToError()
        {
            var captured = TryMap("assertion", " at Game.Check()", LogType.Assert, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Error));
            Assert.That(message, Is.EqualTo("assertion"));
        }

        [Test]
        public void AssertIsIgnoredWhenCaptureErrorsIsOff()
        {
            _settings.CaptureErrors = false;

            Assert.That(TryMap("assertion", " at Game.Check()", LogType.Assert), Is.False);
        }

        [Test]
        public void WarningMapsToWarningWithoutStackTrace()
        {
            _settings.CaptureWarnings = true;

            var captured = TryMap("careful", " at Game.Tick()", LogType.Warning, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Warning));
            Assert.That(message, Is.EqualTo("careful"));
        }

        [Test]
        public void WarningIsIgnoredByDefault()
        {
            Assert.That(TryMap("careful", string.Empty, LogType.Warning), Is.False);
        }

        [Test]
        public void LogMapsToDebugWithoutStackTrace()
        {
            _settings.CaptureLogs = true;

            var captured = TryMap("hello", " at Game.Tick()", LogType.Log, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Debug));
            Assert.That(message, Is.EqualTo("hello"));
        }

        [Test]
        public void LogIsIgnoredByDefault()
        {
            Assert.That(TryMap("hello", string.Empty, LogType.Log), Is.False);
        }

        [Test]
        public void ErrorWithoutStackTraceKeepsOnlyTheCondition()
        {
            var captured = TryMap("failed", string.Empty, LogType.Error, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Error));
            Assert.That(message, Is.EqualTo("failed"));
        }

        [Test]
        public void ExceptionWithoutStackTraceKeepsOnlyTheCondition()
        {
            var captured = TryMap("boom", string.Empty, LogType.Exception, out var message, out var level);

            Assert.That(captured, Is.True);
            Assert.That(level, Is.EqualTo(Level.Error));
            Assert.That(message, Is.EqualTo("boom"));
        }

        [TestCase(LogType.Exception)]
        [TestCase(LogType.Error)]
        [TestCase(LogType.Assert)]
        [TestCase(LogType.Warning)]
        [TestCase(LogType.Log)]
        public void GlitchPortPrefixedMessagesAreIgnoredForEveryLogType(LogType type)
        {
            EnableAllCaptureFlags();

            Assert.That(TryMap("[GlitchPort] SDK diagnostic", " at Sdk()", type), Is.False);
        }

        [TestCase(LogType.Exception)]
        [TestCase(LogType.Error)]
        [TestCase(LogType.Assert)]
        [TestCase(LogType.Warning)]
        [TestCase(LogType.Log)]
        public void DisabledSettingsIgnoreEveryLogType(LogType type)
        {
            EnableAllCaptureFlags();
            _settings.Enabled = false;

            Assert.That(TryMap("message", " at Game()", type), Is.False);
        }

        [Test]
        public void CaptureFlagsAreIndependent()
        {
            _settings.CaptureExceptions = false;
            _settings.CaptureErrors = false;
            _settings.CaptureWarnings = true;
            _settings.CaptureLogs = false;

            Assert.That(TryMap("warning", string.Empty, LogType.Warning), Is.True);
            Assert.That(TryMap("error", string.Empty, LogType.Error), Is.False);
            Assert.That(TryMap("assert", string.Empty, LogType.Assert), Is.False);
            Assert.That(TryMap("log", string.Empty, LogType.Log), Is.False);
            Assert.That(TryMap("exception", string.Empty, LogType.Exception), Is.False);
        }

        [Test]
        public void UnknownLogTypeIsIgnored()
        {
            EnableAllCaptureFlags();

            Assert.That(TryMap("message", string.Empty, (LogType)999), Is.False);
        }

        [Test]
        public void CallsDoNotShareState()
        {
            var first = ScriptableObject.CreateInstance<GlitchPortSettings>();
            var second = ScriptableObject.CreateInstance<GlitchPortSettings>();
            try
            {
                first.CaptureErrors = true;
                second.CaptureErrors = false;

                Assert.That(
                    UnityLogBridge.TryMap("first", string.Empty, LogType.Error, first, out var firstMessage, out _),
                    Is.True);
                Assert.That(
                    UnityLogBridge.TryMap("second", string.Empty, LogType.Error, second, out var secondMessage, out _),
                    Is.False);
                Assert.That(firstMessage, Is.EqualTo("first"));
                Assert.That(secondMessage, Is.EqualTo("second"));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        private bool TryMap(string condition, string stackTrace, LogType type)
        {
            return UnityLogBridge.TryMap(condition, stackTrace, type, _settings, out _, out _);
        }

        private bool TryMap(
            string condition,
            string stackTrace,
            LogType type,
            out string message,
            out Level level)
        {
            return UnityLogBridge.TryMap(condition, stackTrace, type, _settings, out message, out level);
        }

        private void EnableAllCaptureFlags()
        {
            _settings.CaptureExceptions = true;
            _settings.CaptureErrors = true;
            _settings.CaptureWarnings = true;
            _settings.CaptureLogs = true;
        }
    }
}
