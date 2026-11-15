#nullable enable
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>Verifies the serialized settings fields and their documented defaults.</summary>
    [TestFixture]
    public sealed class GlitchPortSettingsTests
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
        public void SettingsHaveTheDocumentedDefaults()
        {
            Assert.That(_settings.Enabled, Is.True);
            Assert.That(_settings.Dsn, Is.EqualTo(string.Empty));
            Assert.That(_settings.AutoInitialize, Is.True);
            Assert.That(_settings.HttpMinLevel, Is.EqualTo(Level.Error));
            Assert.That(_settings.Release, Is.EqualTo(string.Empty));
            Assert.That(_settings.Environment, Is.EqualTo(string.Empty));
            Assert.That(_settings.SendDefaultPii, Is.False);
            Assert.That(_settings.MaxBreadcrumbs, Is.EqualTo(100));
            Assert.That(_settings.SampleRate, Is.EqualTo(1f));
            Assert.That(_settings.OfflineQueue, Is.True);
            Assert.That(_settings.OfflineQueueMaxEvents, Is.EqualTo(500));
            Assert.That(_settings.CaptureExceptions, Is.True);
            Assert.That(_settings.CaptureErrors, Is.True);
            Assert.That(_settings.CaptureWarnings, Is.False);
            Assert.That(_settings.CaptureLogs, Is.False);
            Assert.That(_settings.CaptureBreadcrumbsFromLogs, Is.True);
            Assert.That(_settings.TerminalMode, Is.EqualTo(GlitchPort.Unity.TerminalMode.Auto));
            Assert.That(_settings.TerminalWindow, Is.EqualTo(GlitchPort.TerminalWindow.New));
            Assert.That(_settings.FileMode, Is.EqualTo(GlitchPort.Unity.FileMode.Auto));
            Assert.That(_settings.FileDirectory, Is.EqualTo(string.Empty));
            Assert.That(_settings.FileMinLevel, Is.EqualTo(Level.Debug));
            Assert.That(_settings.DebugLogging, Is.False);
        }

        [Test]
        public void SampleRateIsLimitedToTheUnitRange()
        {
            var attribute = typeof(GlitchPortSettings)
                .GetField(nameof(GlitchPortSettings.SampleRate))!
                .GetCustomAttributes(typeof(UnityEngine.RangeAttribute), false)
                .OfType<UnityEngine.RangeAttribute>()
                .SingleOrDefault();

            Assert.That(attribute, Is.Not.Null, "SampleRate must be drawn as a 0..1 slider.");
            Assert.That(attribute!.min, Is.EqualTo(0f));
            Assert.That(attribute.max, Is.EqualTo(1f));
        }

        [Test]
        public void TerminalModeEnumIsAutoOnOff()
        {
            Assert.That((int)GlitchPort.Unity.TerminalMode.Auto, Is.EqualTo(0));
            Assert.That((int)GlitchPort.Unity.TerminalMode.On, Is.EqualTo(1));
            Assert.That((int)GlitchPort.Unity.TerminalMode.Off, Is.EqualTo(2));
        }

        [Test]
        public void FileModeEnumIsAutoOnOff()
        {
            Assert.That((int)GlitchPort.Unity.FileMode.Auto, Is.EqualTo(0));
            Assert.That((int)GlitchPort.Unity.FileMode.On, Is.EqualTo(1));
            Assert.That((int)GlitchPort.Unity.FileMode.Off, Is.EqualTo(2));
        }

        [Test]
        public void EverySettingIsSerializedForTheInspector()
        {
            var serialized = new SerializedObject(_settings);
            var fields = new[]
            {
                "Enabled",
                "Dsn",
                "AutoInitialize",
                "HttpMinLevel",
                "Release",
                "Environment",
                "SendDefaultPii",
                "MaxBreadcrumbs",
                "SampleRate",
                "OfflineQueue",
                "OfflineQueueMaxEvents",
                "CaptureExceptions",
                "CaptureErrors",
                "CaptureWarnings",
                "CaptureLogs",
                "CaptureBreadcrumbsFromLogs",
                "TerminalMode",
                "TerminalWindow",
                "FileMode",
                "FileDirectory",
                "FileMinLevel",
                "DebugLogging",
            };

            foreach (var field in fields)
            {
                Assert.That(
                    serialized.FindProperty(field),
                    Is.Not.Null,
                    "GlitchPortSettings." + field + " is not a serialized field.");
            }
        }

        [Test]
        public void SettingsAssetCanBeCreatedFromTheMenu()
        {
            var attribute = typeof(GlitchPortSettings)
                .GetCustomAttributes(typeof(CreateAssetMenuAttribute), false)
                .OfType<CreateAssetMenuAttribute>()
                .SingleOrDefault();

            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute!.fileName, Is.EqualTo("GlitchPortSettings"));
        }

        [Test]
        public void SettingsCanBeEditedAndReadBack()
        {
            _settings.Enabled = false;
            _settings.Dsn = "http://public@localhost:5080/1";
            _settings.AutoInitialize = false;
            _settings.HttpMinLevel = Level.Warning;
            _settings.Release = "mygame@2.0.0";
            _settings.Environment = "staging";
            _settings.SendDefaultPii = true;
            _settings.MaxBreadcrumbs = 25;
            _settings.SampleRate = 0.5f;
            _settings.OfflineQueue = false;
            _settings.OfflineQueueMaxEvents = 7;
            _settings.CaptureExceptions = false;
            _settings.CaptureErrors = false;
            _settings.CaptureWarnings = true;
            _settings.CaptureLogs = true;
            _settings.CaptureBreadcrumbsFromLogs = false;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.On;
            _settings.TerminalWindow = GlitchPort.TerminalWindow.Inherit;
            _settings.FileMode = GlitchPort.Unity.FileMode.On;
            _settings.FileDirectory = "custom/logs";
            _settings.FileMinLevel = Level.Info;
            _settings.DebugLogging = true;

            var serialized = new SerializedObject(_settings);
            serialized.Update();

            Assert.That(serialized.FindProperty("Enabled")!.boolValue, Is.False);
            Assert.That(serialized.FindProperty("Dsn")!.stringValue, Is.EqualTo("http://public@localhost:5080/1"));
            Assert.That(serialized.FindProperty("Release")!.stringValue, Is.EqualTo("mygame@2.0.0"));
            Assert.That(serialized.FindProperty("Environment")!.stringValue, Is.EqualTo("staging"));
            Assert.That(serialized.FindProperty("SendDefaultPii")!.boolValue, Is.True);
            Assert.That(serialized.FindProperty("MaxBreadcrumbs")!.intValue, Is.EqualTo(25));
            Assert.That(serialized.FindProperty("SampleRate")!.floatValue, Is.EqualTo(0.5f));
            Assert.That(serialized.FindProperty("OfflineQueue")!.boolValue, Is.False);
            Assert.That(serialized.FindProperty("OfflineQueueMaxEvents")!.intValue, Is.EqualTo(7));
            Assert.That(serialized.FindProperty("CaptureBreadcrumbsFromLogs")!.boolValue, Is.False);
            Assert.That(serialized.FindProperty("TerminalMode")!.enumValueIndex, Is.EqualTo((int)GlitchPort.Unity.TerminalMode.On));
            Assert.That(serialized.FindProperty("FileMode")!.enumValueIndex, Is.EqualTo((int)GlitchPort.Unity.FileMode.On));
        }
    }
}
