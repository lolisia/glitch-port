#nullable enable
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>
    /// Verifies every Unity log type and capture flag mapping, including the
    /// structured exceptions and the log breadcrumbs.
    /// </summary>
    [TestFixture]
    public sealed class UnityLogBridgeTests
    {
        private const string LoggerName = "unity";

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
        public void ExceptionProducesAStructuredErrorEvent()
        {
            const string condition = "ArgumentException: inner failure";
            const string stackTrace =
                "Game.Inventory.Add (System.String item) (at Assets/Scripts/Inventory.cs:52)\n" +
                "Rethrow as System.InvalidOperationException: pickup failed\n" +
                "Game.Player.PickUp (System.String item) (at Assets/Scripts/Player.cs:92)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.GameLoop:Tick() (at Assets/Scripts/GameLoop.cs:44)";

            var mapping = UnityLogBridge.Map(
                condition,
                stackTrace,
                LogType.Exception,
                _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.HasBreadcrumb, Is.False);

            var exceptionEvent = mapping.Event!;
            Assert.That(exceptionEvent.Level, Is.EqualTo(Level.Error));
            Assert.That(exceptionEvent.Logger, Is.EqualTo(LoggerName));
            Assert.That(exceptionEvent.Message, Is.Null.Or.Empty);
            Assert.That(exceptionEvent.Exceptions, Has.Count.EqualTo(2));

            var inner = exceptionEvent.Exceptions[0];
            Assert.That(inner.Type, Is.EqualTo("ArgumentException"));
            Assert.That(inner.Value, Is.EqualTo("inner failure"));
            Assert.That(inner.Frames, Has.Count.EqualTo(1));
            Assert.That(inner.Frames[0].Module, Is.EqualTo("Game.Inventory"));
            Assert.That(inner.Frames[0].Function, Is.EqualTo("Add"));
            Assert.That(inner.Frames[0].FileName, Is.EqualTo("Assets/Scripts/Inventory.cs"));
            Assert.That(inner.Frames[0].LineNumber, Is.EqualTo(52));

            var outer = exceptionEvent.Exceptions[1];
            Assert.That(outer.Type, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(outer.Value, Is.EqualTo("pickup failed"));
            Assert.That(outer.Frames, Has.Count.EqualTo(1));
            Assert.That(outer.Frames[0].Function, Is.EqualTo("PickUp"));
            Assert.That(outer.Frames[0].LineNumber, Is.EqualTo(92));
        }

        [Test]
        public void ExceptionWithoutStackTraceUsesTheFallbackType()
        {
            var mapping = UnityLogBridge.Map(
                "boom",
                string.Empty,
                LogType.Exception,
                _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.Event!.Exceptions, Has.Count.EqualTo(1));
            Assert.That(mapping.Event.Exceptions[0].Type, Is.EqualTo("Exception"));
            Assert.That(mapping.Event.Exceptions[0].Value, Is.EqualTo("boom"));
            Assert.That(mapping.Event.Exceptions[0].Frames, Is.Empty);
        }

        [Test]
        public void ExceptionChainWithoutThrownFramesKeepsTheLogCallSiteFrames()
        {
            // Debug.LogException with an exception that was created but never
            // thrown reports the inner exception in the condition and the log
            // call stack after a "Rethrow as" marker. There are no exception
            // frames of its own, so the call site frames must be preserved.
            const string condition = "ArgumentException: inner";
            const string stackTrace =
                "Rethrow as InvalidOperationException: x\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.GameLoop:Tick() (at Assets/Scripts/GameLoop.cs:44)\n" +
                "UnityEngine.SetupCoroutine:InvokeMoveNext(IEnumerator, IntPtr)";

            var mapping = UnityLogBridge.Map(condition, stackTrace, LogType.Exception, _settings);

            var exceptions = mapping.Event!.Exceptions;
            Assert.That(exceptions, Has.Count.EqualTo(2));
            Assert.That(exceptions[0].Type, Is.EqualTo("ArgumentException"));
            Assert.That(exceptions[0].Frames, Is.Empty);
            Assert.That(exceptions[1].Type, Is.EqualTo("InvalidOperationException"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(2));
            Assert.That(exceptions[1].Frames[0].Function, Is.EqualTo("InvokeMoveNext"));
            Assert.That(exceptions[1].Frames[1].Function, Is.EqualTo("Tick"));
            Assert.That(exceptions[1].Frames[1].LineNumber, Is.EqualTo(44));
        }

        [Test]
        public void ExceptionIsIgnoredWhenCaptureExceptionsIsOff()
        {
            _settings.CaptureExceptions = false;

            Assert.That(
                UnityLogBridge.Map("boom", "Game.Update () (at Assets/Game.cs:1)", LogType.Exception, _settings)
                    .IsEmpty,
                Is.True);
        }

        [Test]
        public void ErrorWithStackTraceProducesASyntheticLogErrorException()
        {
            const string condition = "failed to load: config.json\nsecond line";
            const string stackTrace =
                "Game.Load () (at Assets/Game.cs:30)\n" +
                "Game.Start () (at Assets/Game.cs:10)";

            var mapping = UnityLogBridge.Map(condition, stackTrace, LogType.Error, _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.HasBreadcrumb, Is.False);

            var exceptionEvent = mapping.Event!;
            Assert.That(exceptionEvent.Level, Is.EqualTo(Level.Error));
            Assert.That(exceptionEvent.Logger, Is.EqualTo(LoggerName));
            Assert.That(exceptionEvent.Message, Is.Null.Or.Empty);
            Assert.That(exceptionEvent.Exceptions, Has.Count.EqualTo(1));

            var exception = exceptionEvent.Exceptions[0];
            Assert.That(exception.Type, Is.EqualTo("LogError"));
            Assert.That(exception.Value, Is.EqualTo("failed to load: config.json"));

            // The parser's frames are reversed so the call site is last.
            Assert.That(exception.Frames, Has.Count.EqualTo(2));
            Assert.That(exception.Frames[0].Function, Is.EqualTo("Start"));
            Assert.That(exception.Frames[0].LineNumber, Is.EqualTo(10));
            Assert.That(exception.Frames[1].Function, Is.EqualTo("Load"));
            Assert.That(exception.Frames[1].LineNumber, Is.EqualTo(30));
        }

        [Test]
        public void AssertWithStackTraceProducesASyntheticAssertException()
        {
            const string stackTrace =
                "Game.Check () (at Assets/Game.cs:20)\n" +
                "Game.Update () (at Assets/Game.cs:10)";

            var mapping = UnityLogBridge.Map("assertion failed", stackTrace, LogType.Assert, _settings);

            Assert.That(mapping.HasEvent, Is.True);
            var exceptionEvent = mapping.Event!;
            Assert.That(exceptionEvent.Level, Is.EqualTo(Level.Error));
            Assert.That(exceptionEvent.Exceptions, Has.Count.EqualTo(1));
            Assert.That(exceptionEvent.Exceptions[0].Type, Is.EqualTo("Assert"));
            Assert.That(exceptionEvent.Exceptions[0].Value, Is.EqualTo("assertion failed"));
            Assert.That(exceptionEvent.Exceptions[0].Frames, Has.Count.EqualTo(2));
            Assert.That(exceptionEvent.Exceptions[0].Frames[1].Function, Is.EqualTo("Check"));
        }

        [Test]
        public void ErrorWithoutStackTraceProducesAMessageEvent()
        {
            var mapping = UnityLogBridge.Map("failed", string.Empty, LogType.Error, _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.HasBreadcrumb, Is.False);

            var messageEvent = mapping.Event!;
            Assert.That(messageEvent.Level, Is.EqualTo(Level.Error));
            Assert.That(messageEvent.Logger, Is.EqualTo(LoggerName));
            Assert.That(messageEvent.Message, Is.EqualTo("failed"));
            Assert.That(messageEvent.Exceptions, Is.Empty);
        }

        [Test]
        public void AssertWithoutStackTraceProducesAMessageEvent()
        {
            var mapping = UnityLogBridge.Map("assertion", string.Empty, LogType.Assert, _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.Event!.Level, Is.EqualTo(Level.Error));
            Assert.That(mapping.Event.Message, Is.EqualTo("assertion"));
            Assert.That(mapping.Event.Exceptions, Is.Empty);
        }

        [Test]
        public void ErrorAndAssertAreIgnoredWhenCaptureErrorsIsOff()
        {
            _settings.CaptureErrors = false;

            Assert.That(
                UnityLogBridge.Map("failed", "Game.Start () (at Assets/Game.cs:1)", LogType.Error, _settings)
                    .IsEmpty,
                Is.True);
            Assert.That(UnityLogBridge.Map("failed", string.Empty, LogType.Assert, _settings).IsEmpty, Is.True);
        }

        [Test]
        public void WarningWithCaptureOnProducesAnEventAndALogBreadcrumb()
        {
            _settings.CaptureWarnings = true;

            var mapping = UnityLogBridge.Map("careful", " at Game.Tick()", LogType.Warning, _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.HasBreadcrumb, Is.True);

            Assert.That(mapping.Event!.Level, Is.EqualTo(Level.Warning));
            Assert.That(mapping.Event.Logger, Is.EqualTo(LoggerName));
            Assert.That(mapping.Event.Message, Is.EqualTo("careful"));
            Assert.That(mapping.Event.Exceptions, Is.Empty);

            Assert.That(mapping.Breadcrumb!.Category, Is.EqualTo("log"));
            Assert.That(mapping.Breadcrumb.Level, Is.EqualTo(Level.Warning));
            Assert.That(mapping.Breadcrumb.Message, Is.EqualTo("careful"));
            Assert.That(mapping.Breadcrumb.Type, Is.EqualTo("default"));
        }

        [Test]
        public void WarningWithCaptureOffStillProducesALogBreadcrumb()
        {
            Assert.That(_settings.CaptureWarnings, Is.False);
            Assert.That(_settings.CaptureBreadcrumbsFromLogs, Is.True);

            var mapping = UnityLogBridge.Map("careful", string.Empty, LogType.Warning, _settings);

            Assert.That(mapping.HasEvent, Is.False);
            Assert.That(mapping.HasBreadcrumb, Is.True);
            Assert.That(mapping.Breadcrumb!.Category, Is.EqualTo("log"));
            Assert.That(mapping.Breadcrumb.Level, Is.EqualTo(Level.Warning));
        }

        [Test]
        public void WarningWithCaptureAndBreadcrumbsOffIsIgnored()
        {
            _settings.CaptureBreadcrumbsFromLogs = false;

            Assert.That(
                UnityLogBridge.Map("careful", string.Empty, LogType.Warning, _settings).IsEmpty,
                Is.True);
        }

        [Test]
        public void LogWithCaptureOnProducesADebugEventAndALogBreadcrumb()
        {
            _settings.CaptureLogs = true;

            var mapping = UnityLogBridge.Map("hello", " at Game.Tick()", LogType.Log, _settings);

            Assert.That(mapping.HasEvent, Is.True);
            Assert.That(mapping.HasBreadcrumb, Is.True);

            Assert.That(mapping.Event!.Level, Is.EqualTo(Level.Debug));
            Assert.That(mapping.Event.Logger, Is.EqualTo(LoggerName));
            Assert.That(mapping.Event.Message, Is.EqualTo("hello"));

            Assert.That(mapping.Breadcrumb!.Category, Is.EqualTo("log"));
            Assert.That(mapping.Breadcrumb.Level, Is.EqualTo(Level.Debug));
        }

        [Test]
        public void LogWithCaptureOffStillProducesALogBreadcrumb()
        {
            Assert.That(_settings.CaptureLogs, Is.False);

            var mapping = UnityLogBridge.Map("hello", string.Empty, LogType.Log, _settings);

            Assert.That(mapping.HasEvent, Is.False);
            Assert.That(mapping.HasBreadcrumb, Is.True);
            Assert.That(mapping.Breadcrumb!.Level, Is.EqualTo(Level.Debug));
        }

        [Test]
        public void LogWithCaptureAndBreadcrumbsOffIsIgnored()
        {
            _settings.CaptureBreadcrumbsFromLogs = false;

            Assert.That(UnityLogBridge.Map("hello", string.Empty, LogType.Log, _settings).IsEmpty, Is.True);
        }

        [TestCase(LogType.Exception)]
        [TestCase(LogType.Error)]
        [TestCase(LogType.Assert)]
        [TestCase(LogType.Warning)]
        [TestCase(LogType.Log)]
        public void GlitchPortPrefixedMessagesAreIgnoredForEveryLogType(LogType type)
        {
            EnableAllCaptureFlags();

            var mapping = UnityLogBridge.Map(
                "[GlitchPort] SDK diagnostic",
                " at Sdk()",
                type,
                _settings);

            Assert.That(mapping.IsEmpty, Is.True);
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

            Assert.That(UnityLogBridge.Map("message", " at Game()", type, _settings).IsEmpty, Is.True);
        }

        [Test]
        public void CaptureFlagsAreIndependent()
        {
            _settings.CaptureExceptions = false;
            _settings.CaptureErrors = false;
            _settings.CaptureWarnings = true;
            _settings.CaptureLogs = false;
            _settings.CaptureBreadcrumbsFromLogs = false;

            var warning = UnityLogBridge.Map("warning", string.Empty, LogType.Warning, _settings);
            Assert.That(warning.HasEvent, Is.True);
            Assert.That(warning.HasBreadcrumb, Is.False);

            Assert.That(UnityLogBridge.Map("error", string.Empty, LogType.Error, _settings).IsEmpty, Is.True);
            Assert.That(UnityLogBridge.Map("assert", string.Empty, LogType.Assert, _settings).IsEmpty, Is.True);
            Assert.That(UnityLogBridge.Map("log", string.Empty, LogType.Log, _settings).IsEmpty, Is.True);
            Assert.That(
                UnityLogBridge.Map("exception", string.Empty, LogType.Exception, _settings).IsEmpty,
                Is.True);
        }

        [Test]
        public void UnknownLogTypeIsIgnored()
        {
            EnableAllCaptureFlags();

            Assert.That(
                UnityLogBridge.Map("message", string.Empty, (LogType)999, _settings).IsEmpty,
                Is.True);
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

                var firstMapping = UnityLogBridge.Map("first", string.Empty, LogType.Error, first);
                var secondMapping = UnityLogBridge.Map("second", string.Empty, LogType.Error, second);

                Assert.That(firstMapping.HasEvent, Is.True);
                Assert.That(firstMapping.Event!.Message, Is.EqualTo("first"));
                Assert.That(secondMapping.IsEmpty, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        private void EnableAllCaptureFlags()
        {
            _settings.CaptureExceptions = true;
            _settings.CaptureErrors = true;
            _settings.CaptureWarnings = true;
            _settings.CaptureLogs = true;
            _settings.CaptureBreadcrumbsFromLogs = true;
        }
    }
}
