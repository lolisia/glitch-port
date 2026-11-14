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
            Assert.That(_settings.CaptureExceptions, Is.True);
            Assert.That(_settings.CaptureErrors, Is.True);
            Assert.That(_settings.CaptureWarnings, Is.False);
            Assert.That(_settings.CaptureLogs, Is.False);
            Assert.That(_settings.TerminalMode, Is.EqualTo(GlitchPort.Unity.TerminalMode.Auto));
            Assert.That(_settings.TerminalWindow, Is.EqualTo(GlitchPort.TerminalWindow.New));
            Assert.That(_settings.FileMode, Is.EqualTo(GlitchPort.Unity.FileMode.Auto));
            Assert.That(_settings.FileDirectory, Is.EqualTo(string.Empty));
            Assert.That(_settings.FileMinLevel, Is.EqualTo(Level.Debug));
            Assert.That(_settings.DebugLogging, Is.False);
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
                "CaptureExceptions",
                "CaptureErrors",
                "CaptureWarnings",
                "CaptureLogs",
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
            _settings.CaptureExceptions = false;
            _settings.CaptureErrors = false;
            _settings.CaptureWarnings = true;
            _settings.CaptureLogs = true;
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
            Assert.That(serialized.FindProperty("TerminalMode")!.enumValueIndex, Is.EqualTo((int)GlitchPort.Unity.TerminalMode.On));
            Assert.That(serialized.FindProperty("FileMode")!.enumValueIndex, Is.EqualTo((int)GlitchPort.Unity.FileMode.On));
        }
    }
}
