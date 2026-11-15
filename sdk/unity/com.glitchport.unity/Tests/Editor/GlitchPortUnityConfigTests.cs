#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>Verifies every rule used to turn settings into Core options.</summary>
    [TestFixture]
    public sealed class GlitchPortUnityConfigTests
    {
        private const string DefaultPersistentDataPath = @"C:\GameData";
        private const string DefaultProductName = "Test Game";
        private const string DefaultPackageVersion = "1.2.3";
        private const string DefaultProductVersion = "2.0.0";
        private const string DefaultTerminalPath = @"C:\Tools\glitchport-terminal.exe";
        private const string BuildScriptName = "scripts/build-unity-plugins.sh";

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

            UnityPlatformInfo.TerminalExecutablePathOverride = null;
        }

        [Test]
        public void DsnAddsHttpRelayWithConfiguredMinimumLevel()
        {
            _settings.Dsn = "http://public@localhost:5080/1";
            _settings.HttpMinLevel = Level.Warning;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("http"));
            Assert.That(relay.MinLevel, Is.EqualTo(Level.Warning));
            Assert.That(relay.Options, Is.InstanceOf<HttpRelayOptions>());
            Assert.That(options!.SdkName, Is.EqualTo("glitchport.unity"));
            Assert.That(options.SdkVersion, Is.EqualTo(DefaultPackageVersion));
        }

        [Test]
        public void HttpRelayQueuesUndeliverableEventsByDefault()
        {
            ConfigureHttpOnly();

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("http"));
            var httpOptions = (HttpRelayOptions)relay.Options;
            Assert.That(
                httpOptions.OfflineQueue.Directory,
                Is.EqualTo(Path.Combine(DefaultPersistentDataPath, "glitchport", "queue")));
            Assert.That(httpOptions.OfflineQueue.MaxEvents, Is.EqualTo(500));
            Assert.That(httpOptions.OfflineQueue.MaxAgeHours, Is.EqualTo(72));
            Assert.That(httpOptions.OfflineQueue.RetryIntervalSeconds, Is.EqualTo(30));
        }

        [Test]
        public void HttpRelayHasNoOfflineQueueWhenItIsDisabled()
        {
            ConfigureHttpOnly();
            _settings.OfflineQueue = false;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            var httpOptions = (HttpRelayOptions)SingleRelay(options).Options;
            Assert.That(httpOptions.OfflineQueue.Directory, Is.Null);
        }

        [TestCase(25, 25)]
        [TestCase(0, 1)]
        [TestCase(-3, 1)]
        public void OfflineQueueMaxEventsIsCopiedAndClampedToAtLeastOne(int maxEvents, int expected)
        {
            ConfigureHttpOnly();
            _settings.OfflineQueueMaxEvents = maxEvents;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            var httpOptions = (HttpRelayOptions)SingleRelay(options).Options;
            Assert.That(httpOptions.OfflineQueue.MaxEvents, Is.EqualTo(expected));
        }

        [Test]
        public void BlankDsnHasNoOfflineQueue()
        {
            _settings.Dsn = string.Empty;
            _settings.OfflineQueue = true;
            _settings.FileMode = GlitchPort.Unity.FileMode.On;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Relays.Any(relay => relay.Type == "http"), Is.False);
        }

        [Test]
        public void HttpFileAndTerminalRelaysAreAddedTogetherAccordingToTheirRules()
        {
            _settings.Dsn = "http://public@localhost:5080/1";
            _settings.HttpMinLevel = Level.Warning;
            _settings.FileMode = GlitchPort.Unity.FileMode.Auto;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Auto;
            _settings.TerminalWindow = GlitchPort.TerminalWindow.Inherit;

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: true, isWindows: true),
                null);

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Relays.Count, Is.EqualTo(3));
            Assert.That(options.Relays[0].Type, Is.EqualTo("http"));
            Assert.That(options.Relays[0].MinLevel, Is.EqualTo(Level.Warning));
            Assert.That(options.Relays[1].Type, Is.EqualTo("file"));
            Assert.That(options.Relays[2].Type, Is.EqualTo("terminal"));
            Assert.That(
                ((TerminalRelayOptions)options.Relays[2].Options).Window,
                Is.EqualTo(GlitchPort.TerminalWindow.Inherit));
        }

        [Test]
        public void BlankDsnDoesNotAddHttpRelay()
        {
            _settings.Dsn = string.Empty;
            _settings.FileMode = GlitchPort.Unity.FileMode.On;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Relays.Any(relay => relay.Type == "http"), Is.False);
        }

        [Test]
        public void WhitespaceDsnDoesNotAddHttpRelay()
        {
            _settings.Dsn = "   ";
            _settings.FileMode = GlitchPort.Unity.FileMode.On;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Relays.Any(relay => relay.Type == "http"), Is.False);
        }

        [Test]
        public void DsnIsIgnoredWhenPlatformDoesNotSupportHttp()
        {
            _settings.Dsn = "http://public@localhost:5080/1";
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(supportsHttp: false),
                null,
                diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(diagnostics.Count, Is.GreaterThan(0));
        }

        [Test]
        public void InvalidDsnThrowsFormatException()
        {
            _settings.Dsn = "not-a-valid-dsn";
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            Assert.Throws<FormatException>(() =>
                GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null));
        }

        [Test]
        public void FileModeAutoAddsFileRelayInEditor()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: false),
                null);

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("file"));
            Assert.That(relay.MinLevel, Is.EqualTo(Level.Debug));
        }

        [Test]
        public void FileModeAutoAddsFileRelayInDevelopmentBuild()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: true),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("file"));
        }

        [Test]
        public void FileModeAutoAddsFileRelayOnNonWindowsEditor()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: false, isWindows: false),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("file"));
        }

        [Test]
        public void FileModeAutoAddsFileRelayOnNonWindowsDevelopmentBuild()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: true, isWindows: false),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("file"));
        }

        [Test]
        public void FileModeAutoDoesNotAddFileRelayInReleaseBuild()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.Auto);
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false),
                null,
                diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(diagnostics.Count, Is.GreaterThan(0));
        }

        [Test]
        public void FileModeOnAddsFileRelayInReleaseBuild()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("file"));
        }

        [Test]
        public void FileModeOnAddsFileRelayOnNonWindowsReleaseBuild()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false, isWindows: false),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("file"));
        }

        [Test]
        public void FileModeOffDoesNotAddFileRelayInEditor()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.Off);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: true),
                null);

            Assert.That(options, Is.Null);
        }

        [Test]
        public void FileRelayUsesConfiguredDirectory()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);
            _settings.FileDirectory = @"D:\CustomLogs";

            var relay = SingleRelay(GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null));

            Assert.That(((FileRelayOptions)relay.Options).Directory, Is.EqualTo(@"D:\CustomLogs"));
        }

        [Test]
        public void FileRelayUsesDefaultDirectoryUnderPersistentDataPath()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);
            _settings.FileDirectory = string.Empty;

            var relay = SingleRelay(GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null));

            Assert.That(
                ((FileRelayOptions)relay.Options).Directory,
                Is.EqualTo(Path.Combine(DefaultPersistentDataPath, "glitchport", "logs")));
        }

        [Test]
        public void FileRelayWhitespaceDirectoryUsesDefault()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);
            _settings.FileDirectory = "   ";

            var relay = SingleRelay(GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null));

            Assert.That(
                ((FileRelayOptions)relay.Options).Directory,
                Is.EqualTo(Path.Combine(DefaultPersistentDataPath, "glitchport", "logs")));
        }

        [Test]
        public void FileRelayUsesConfiguredMinimumLevel()
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);
            _settings.FileMinLevel = Level.Warning;

            var relay = SingleRelay(GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null));

            Assert.That(relay.MinLevel, Is.EqualTo(Level.Warning));
        }

        [Test]
        public void TerminalModeAutoAddsTerminalRelayInWindowsEditor()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: false, isWindows: true),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("terminal"));
        }

        [Test]
        public void TerminalModeAutoAddsTerminalRelayInWindowsDevelopmentBuild()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: true, isWindows: true),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("terminal"));
        }

        [Test]
        public void TerminalModeAutoDoesNotAddTerminalRelayInWindowsReleaseBuild()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false, isWindows: true),
                null);

            Assert.That(options, Is.Null);
        }

        [Test]
        public void TerminalModeAutoDoesNotAddTerminalRelayOnNonWindowsEditor()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: false, isWindows: false),
                null);

            Assert.That(options, Is.Null);
        }

        [Test]
        public void TerminalModeAutoDoesNotAddTerminalRelayOnNonWindowsDevelopmentBuild()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.Auto);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: true, isWindows: false),
                null);

            Assert.That(options, Is.Null);
        }

        [Test]
        public void TerminalModeOnAddsTerminalRelayInWindowsReleaseBuild()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.On);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false, isWindows: true),
                null);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("terminal"));
        }

        [Test]
        public void TerminalModeOnDoesNotAddTerminalRelayOnNonWindows()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.On);
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: false, isWindows: false),
                null,
                diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(HasDiagnostic(diagnostics, BuildScriptName), Is.True);
        }

        [Test]
        public void TerminalModeOnDoesNotAddTerminalRelayWithoutProcessSupport()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.On);
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isWindows: true, canStartProcess: false),
                null,
                diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(HasDiagnostic(diagnostics, BuildScriptName), Is.True);
        }

        [Test]
        public void TerminalModeOffDoesNotAddTerminalRelayInWindowsEditor()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.Off);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isDevelopmentBuild: true, isWindows: true),
                null);

            Assert.That(options, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void TerminalModeOnWithoutExecutableAddsDiagnostic(string? terminalExecutablePath)
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.On);
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(terminalExecutablePath: terminalExecutablePath),
                null,
                diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(HasDiagnostic(diagnostics, BuildScriptName), Is.True);
        }

        [Test]
        public void TerminalRelayUsesConfiguredWindowProductNameAndPath()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.On);
            _settings.TerminalWindow = GlitchPort.TerminalWindow.Inherit;

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(productName: "My Game", terminalExecutablePath: @"C:\Tools\term.exe"),
                null);

            var relay = SingleRelay(options);
            var terminalOptions = (TerminalRelayOptions)relay.Options;
            Assert.That(terminalOptions.Window, Is.EqualTo(GlitchPort.TerminalWindow.Inherit));
            Assert.That(terminalOptions.AppName, Is.EqualTo("My Game"));
            Assert.That(terminalOptions.ExecutablePath, Is.EqualTo(@"C:\Tools\term.exe"));
            Assert.That(relay.MinLevel, Is.EqualTo(Level.Debug));
        }

        [Test]
        public void TerminalRelayDefaultsToNewWindow()
        {
            ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode.On);

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isWindows: true),
                null);

            var terminalOptions = (TerminalRelayOptions)SingleRelay(options).Options;
            Assert.That(terminalOptions.Window, Is.EqualTo(GlitchPort.TerminalWindow.New));
            Assert.That(terminalOptions.AppName, Is.EqualTo(DefaultProductName));
        }

        [Test]
        public void EnvironmentDsnOverridesAssetRelaySettings()
        {
            _settings.Dsn = "http://asset@localhost:5080/1";
            _settings.FileMode = GlitchPort.Unity.FileMode.On;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.On;
            _settings.FileDirectory = "asset-logs";

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: true, isWindows: true),
                "file:?dir=env-logs&min_level=warning");

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("file"));
            Assert.That(relay.MinLevel, Is.EqualTo(Level.Warning));
            Assert.That(((FileRelayOptions)relay.Options).Directory, Is.EqualTo("env-logs"));
        }

        [Test]
        public void EnvironmentDsnIgnoresAnInvalidAssetDsn()
        {
            _settings.Dsn = "not-a-valid-dsn";

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(),
                "file:?dir=env-logs");

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("file"));
            Assert.That(((FileRelayOptions)relay.Options).Directory, Is.EqualTo("env-logs"));
        }

        [Test]
        public void EnvironmentDsnFileOnlyAddsFileRelay()
        {
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false, isWindows: false),
                "file",
                diagnostics);

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("file"));
            Assert.That(((FileRelayOptions)relay.Options).Directory, Is.EqualTo("logs/glitchport"));
            Assert.That(diagnostics.Count, Is.EqualTo(0));
        }

        [Test]
        public void EnvironmentDsnWithHttpRelayUsesParsedMinimumLevel()
        {
            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(),
                "http://public@localhost:5080/7?min_level=error");

            var relay = SingleRelay(options);
            Assert.That(relay.Type, Is.EqualTo("http"));
            Assert.That(relay.MinLevel, Is.EqualTo(Level.Error));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void EnvironmentDsnHttpRelayUsesItsOwnOfflineQueue(bool assetQueueEnabled)
        {
            _settings.Dsn = "http://asset@localhost:5080/1";
            _settings.OfflineQueue = assetQueueEnabled;
            _settings.OfflineQueueMaxEvents = 13;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(),
                "http://public@localhost:5080/7?offline_dir=env-queue&offline_max_events=7");

            var httpOptions = (HttpRelayOptions)SingleRelay(options).Options;
            Assert.That(httpOptions.OfflineQueue.Directory, Is.EqualTo("env-queue"));
            Assert.That(httpOptions.OfflineQueue.MaxEvents, Is.EqualTo(7));
        }

        [Test]
        public void EnvironmentDsnWithoutOfflineDirHasNoOfflineQueue()
        {
            _settings.OfflineQueue = true;
            _settings.OfflineQueueMaxEvents = 13;

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(),
                "http://public@localhost:5080/7");

            var httpOptions = (HttpRelayOptions)SingleRelay(options).Options;
            Assert.That(httpOptions.OfflineQueue.Directory, Is.Null);
        }

        [Test]
        public void EnvironmentDsnAddsEveryParsedRelay()
        {
            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isWindows: false, canStartProcess: false, terminalExecutablePath: null),
                "http://public@localhost:5080/7;terminal:?window=new");

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Relays.Count, Is.EqualTo(2));
            Assert.That(options.Relays[0].Type, Is.EqualTo("http"));
            Assert.That(options.Relays[1].Type, Is.EqualTo("terminal"));
            Assert.That(
                ((TerminalRelayOptions)options.Relays[1].Options).Window,
                Is.EqualTo(GlitchPort.TerminalWindow.New));
        }

        [Test]
        public void EnvironmentDsnTerminalIgnoresPlatformAvailability()
        {
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isWindows: false, canStartProcess: false, terminalExecutablePath: null),
                "terminal",
                diagnostics);

            Assert.That(SingleRelay(options).Type, Is.EqualTo("terminal"));
            Assert.That(diagnostics.Count, Is.EqualTo(0));
        }

        [Test]
        public void WhitespaceEnvironmentDsnUsesAssetSettings()
        {
            _settings.Dsn = "http://public@localhost:5080/1";
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), "   ");

            Assert.That(SingleRelay(options).Type, Is.EqualTo("http"));
        }

        [Test]
        public void WhitespaceEnvironmentDsnWithNoAssetRelaysReturnsNull()
        {
            _settings.Dsn = string.Empty;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), "   ", diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(diagnostics.Count, Is.GreaterThan(0));
        }

        [Test]
        public void EnvironmentDsnWithOnlySeparatorsReturnsNullAndDiagnostic()
        {
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), " ; ; ", diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(diagnostics.Count, Is.GreaterThan(0));
        }

        [Test]
        public void InvalidEnvironmentDsnThrowsFormatException()
        {
            Assert.Throws<FormatException>(() =>
                GlitchPortUnityConfig.BuildOptions(_settings, Platform(), "not-a-relay"));
        }

        [Test]
        public void BuildOptionsDoesNotReadTheProcessEnvironment()
        {
            var previous = Environment.GetEnvironmentVariable("GLITCHPORT_DSN");
            try
            {
                Environment.SetEnvironmentVariable("GLITCHPORT_DSN", "file:?dir=process-env");
                _settings.Dsn = "http://public@localhost:5080/1";
                _settings.FileMode = GlitchPort.Unity.FileMode.Off;
                _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

                var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null);

                Assert.That(SingleRelay(options).Type, Is.EqualTo("http"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GLITCHPORT_DSN", previous);
            }
        }

        [Test]
        public void NoRelayReturnsNullAndAddsDiagnostic()
        {
            _settings.Dsn = string.Empty;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
            var diagnostics = new List<string>();

            var options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null, diagnostics);

            Assert.That(options, Is.Null);
            Assert.That(diagnostics.Count, Is.GreaterThan(0));
            Assert.That(HasDiagnostic(diagnostics, "DSN"), Is.True);
        }

        [Test]
        public void NoRelayWithNullDiagnosticsDoesNotThrow()
        {
            _settings.Dsn = string.Empty;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            GlitchPortOptions? options = null;
            Assert.DoesNotThrow(() =>
                options = GlitchPortUnityConfig.BuildOptions(_settings, Platform(), null, null));
            Assert.That(options, Is.Null);
        }

        [Test]
        public void OptionsReportTheConfiguredPackageVersion()
        {
            _settings.FileMode = GlitchPort.Unity.FileMode.On;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(packageVersion: "9.8.7"),
                null);

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.SdkName, Is.EqualTo("glitchport.unity"));
            Assert.That(options.SdkVersion, Is.EqualTo("9.8.7"));
        }

        [Test]
        public void OptionsReportTheUnityPlatform()
        {
            var options = BuildFileOptions(Platform());

            Assert.That(options.Platform, Is.EqualTo("unity"));
        }

        [Test]
        public void BlankReleaseUsesProductNameAndProductVersion()
        {
            _settings.Release = string.Empty;

            var options = BuildFileOptions(
                Platform(productName: "My Game", productVersion: "3.4.5"));

            Assert.That(options.Release, Is.EqualTo("My Game@3.4.5"));
        }

        [Test]
        public void WhitespaceReleaseUsesProductNameAndProductVersion()
        {
            _settings.Release = "   ";

            var options = BuildFileOptions(Platform());

            Assert.That(
                options.Release,
                Is.EqualTo(DefaultProductName + "@" + DefaultProductVersion));
        }

        [Test]
        public void ExplicitReleaseWins()
        {
            _settings.Release = "mygame@9.9.9";

            var options = BuildFileOptions(Platform());

            Assert.That(options.Release, Is.EqualTo("mygame@9.9.9"));
        }

        [Test]
        public void DefaultReleaseUsesZeroesWithoutAProductVersion()
        {
            var options = BuildFileOptions(Platform(productVersion: null));

            Assert.That(options.Release, Is.EqualTo(DefaultProductName + "@0.0.0"));
        }

        [TestCase(true, false, "editor")]
        [TestCase(true, true, "editor")]
        [TestCase(false, true, "development")]
        [TestCase(false, false, "production")]
        public void DefaultEnvironmentFollowsThePlatform(
            bool isEditor,
            bool isDevelopmentBuild,
            string expected)
        {
            _settings.Environment = string.Empty;

            var options = BuildFileOptions(
                Platform(isEditor: isEditor, isDevelopmentBuild: isDevelopmentBuild));

            Assert.That(options.Environment, Is.EqualTo(expected));
        }

        [Test]
        public void WhitespaceEnvironmentUsesTheDefault()
        {
            _settings.Environment = "   ";

            var options = BuildFileOptions(Platform(isEditor: true));

            Assert.That(options.Environment, Is.EqualTo("editor"));
        }

        [Test]
        public void ExplicitEnvironmentWins()
        {
            _settings.Environment = "staging";

            var options = BuildFileOptions(Platform(isEditor: true, isDevelopmentBuild: true));

            Assert.That(options.Environment, Is.EqualTo("staging"));
        }

        [Test]
        public void SendDefaultPiiDefaultsToFalse()
        {
            var options = BuildFileOptions(Platform());

            Assert.That(options.SendDefaultPii, Is.False);
        }

        [Test]
        public void SendDefaultPiiIsCopied()
        {
            _settings.SendDefaultPii = true;

            var options = BuildFileOptions(Platform());

            Assert.That(options.SendDefaultPii, Is.True);
        }

        [Test]
        public void MaxBreadcrumbsIsCopied()
        {
            _settings.MaxBreadcrumbs = 7;

            var options = BuildFileOptions(Platform());

            Assert.That(options.MaxBreadcrumbs, Is.EqualTo(7));
        }

        [TestCase(0d)]
        [TestCase(0.25d)]
        [TestCase(1d)]
        public void SampleRateIsCopied(double sampleRate)
        {
            _settings.SampleRate = (float)sampleRate;

            var options = BuildFileOptions(Platform());

            Assert.That(options.SampleRate, Is.EqualTo(sampleRate));
        }

        [TestCase(-1d, 0d)]
        [TestCase(0.5d, 0.5d)]
        [TestCase(2d, 1d)]
        public void SampleRateIsClampedToTheUnitRange(double sampleRate, double expected)
        {
            _settings.SampleRate = (float)sampleRate;

            var options = BuildFileOptions(Platform());

            Assert.That(options.SampleRate, Is.EqualTo(expected));
        }

        [Test]
        public void EnvironmentDsnStillAppliesReleaseAndEnvironment()
        {
            _settings.Release = string.Empty;
            _settings.Environment = string.Empty;

            var options = GlitchPortUnityConfig.BuildOptions(
                _settings,
                Platform(isEditor: false, isDevelopmentBuild: false),
                "file");

            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Platform, Is.EqualTo("unity"));
            Assert.That(options.Release, Is.EqualTo(DefaultProductName + "@" + DefaultProductVersion));
            Assert.That(options.Environment, Is.EqualTo("production"));
        }

        [Test]
        public void ProductVersionDefaultsToZeroesForOldCallers()
        {
            var platform = new UnityPlatformInfo(
                isEditor: false,
                isDevelopmentBuild: false,
                isWindows: true,
                canStartProcess: true,
                supportsHttp: true,
                persistentDataPath: DefaultPersistentDataPath,
                productName: DefaultProductName,
                packageVersion: DefaultPackageVersion,
                terminalExecutablePath: DefaultTerminalPath);

            Assert.That(platform.ProductVersion, Is.EqualTo("0.0.0"));
            Assert.That(platform.ProductName, Is.EqualTo(DefaultProductName));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void BlankProductVersionDefaultsToZeroes(string? productVersion)
        {
            Assert.That(Platform(productVersion: productVersion).ProductVersion, Is.EqualTo("0.0.0"));
        }

        [Test]
        public void ExplicitProductVersionIsExposed()
        {
            Assert.That(Platform(productVersion: "4.5.6").ProductVersion, Is.EqualTo("4.5.6"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void IsSupportedReturnsSupportsHttp(bool supportsHttp)
        {
            Assert.That(
                GlitchPortUnityConfig.IsSupported(Platform(supportsHttp: supportsHttp)),
                Is.EqualTo(supportsHttp));
        }

        [Test]
        public void TerminalExecutablePathOverrideRoundTrips()
        {
            UnityPlatformInfo.TerminalExecutablePathOverride = @"C:\Tools\glitchport-terminal.exe";

            Assert.That(
                UnityPlatformInfo.TerminalExecutablePathOverride,
                Is.EqualTo(@"C:\Tools\glitchport-terminal.exe"));
        }

        private static UnityPlatformInfo Platform(
            bool isEditor = false,
            bool isDevelopmentBuild = false,
            bool isWindows = true,
            bool canStartProcess = true,
            bool supportsHttp = true,
            string persistentDataPath = DefaultPersistentDataPath,
            string productName = DefaultProductName,
            string packageVersion = DefaultPackageVersion,
            string? terminalExecutablePath = DefaultTerminalPath,
            string? productVersion = DefaultProductVersion)
        {
            return new UnityPlatformInfo(
                isEditor,
                isDevelopmentBuild,
                isWindows,
                canStartProcess,
                supportsHttp,
                persistentDataPath,
                productName,
                packageVersion,
                terminalExecutablePath,
                productVersion);
        }

        private void ConfigureHttpOnly()
        {
            _settings.Dsn = "http://public@localhost:5080/1";
            _settings.HttpMinLevel = Level.Debug;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
        }

        private void ConfigureFileOnly(GlitchPort.Unity.FileMode fileMode)
        {
            _settings.Dsn = string.Empty;
            _settings.FileMode = fileMode;
            _settings.TerminalMode = GlitchPort.Unity.TerminalMode.Off;
            _settings.FileDirectory = string.Empty;
            _settings.FileMinLevel = Level.Debug;
        }

        private void ConfigureTerminalOnly(GlitchPort.Unity.TerminalMode terminalMode)
        {
            _settings.Dsn = string.Empty;
            _settings.FileMode = GlitchPort.Unity.FileMode.Off;
            _settings.TerminalMode = terminalMode;
            _settings.TerminalWindow = GlitchPort.TerminalWindow.New;
        }

        /// <summary>
        /// Builds options that always contain exactly one relay, so tests about
        /// options fields never depend on the platform's relay rules.
        /// </summary>
        private GlitchPortOptions BuildFileOptions(UnityPlatformInfo platform)
        {
            ConfigureFileOnly(GlitchPort.Unity.FileMode.On);

            var options = GlitchPortUnityConfig.BuildOptions(_settings, platform, null);
            Assert.That(options, Is.Not.Null);
            return options!;
        }

        private static RelayDefinition SingleRelay(GlitchPortOptions? options)
        {
            Assert.That(options, Is.Not.Null);
            Assert.That(options!.Relays.Count, Is.EqualTo(1));
            return options.Relays[0];
        }

        private static bool HasDiagnostic(IEnumerable<string> diagnostics, string text)
        {
            return diagnostics.Any(diagnostic => diagnostic.Contains(text));
        }
    }
}
