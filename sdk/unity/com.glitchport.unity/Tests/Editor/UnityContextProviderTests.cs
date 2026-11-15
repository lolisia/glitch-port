#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>
    /// Verifies the context namespaces, keys and value conversions produced
    /// from <see cref="IUnityContextSource"/>.
    /// </summary>
    [TestFixture]
    public sealed class UnityContextProviderTests
    {
        private FakeUnityContextSource _source = null!;
        private UnityContextProvider _provider = null!;

        [SetUp]
        public void SetUp()
        {
            _source = new FakeUnityContextSource();
            _provider = new UnityContextProvider(_source);
        }

        [Test]
        public void CreateContextsReturnsTheDocumentedNamespacesAndValues()
        {
            _provider.Collect();
            _provider.RefreshFrame();

            var contexts = _provider.CreateContexts();

            Assert.That(
                contexts.Keys,
                Is.EquivalentTo(new[] { "os", "device", "gpu", "runtime", "app", "game" }));

            var os = contexts["os"];
            Assert.That(os.Keys, Is.EquivalentTo(new[] { "name", "family" }));
            Assert.That(os["name"], Is.EqualTo(FakeUnityContextSource.DefaultOperatingSystem));
            Assert.That(os["family"], Is.EqualTo(FakeUnityContextSource.DefaultOperatingSystemFamily));

            var device = contexts["device"];
            Assert.That(
                device.Keys,
                Is.EquivalentTo(new[]
                {
                    "model",
                    "type",
                    "memory_size",
                    "processor_type",
                    "processor_count",
                    "battery_level",
                }));
            Assert.That(device["model"], Is.EqualTo(FakeUnityContextSource.DefaultDeviceModel));
            Assert.That(device["type"], Is.EqualTo(FakeUnityContextSource.DefaultDeviceType));
            Assert.That(device["memory_size"], Is.TypeOf<long>());
            Assert.That(
                device["memory_size"],
                Is.EqualTo(FakeUnityContextSource.DefaultSystemMemoryMegabytes * 1024L * 1024L));
            Assert.That(device["processor_type"], Is.EqualTo(FakeUnityContextSource.DefaultProcessorType));
            Assert.That(device["processor_count"], Is.TypeOf<int>());
            Assert.That(device["processor_count"], Is.EqualTo(FakeUnityContextSource.DefaultProcessorCount));
            Assert.That(device["battery_level"], Is.TypeOf<float>());
            Assert.That(device["battery_level"], Is.EqualTo(FakeUnityContextSource.DefaultBatteryLevel));

            var gpu = contexts["gpu"];
            Assert.That(gpu.Keys, Is.EquivalentTo(new[] { "name", "vendor", "version", "api", "memory_size" }));
            Assert.That(gpu["name"], Is.EqualTo(FakeUnityContextSource.DefaultGpuName));
            Assert.That(gpu["vendor"], Is.EqualTo(FakeUnityContextSource.DefaultGpuVendor));
            Assert.That(gpu["version"], Is.EqualTo(FakeUnityContextSource.DefaultGpuVersion));
            Assert.That(gpu["api"], Is.EqualTo(FakeUnityContextSource.DefaultGraphicsApi));
            Assert.That(gpu["memory_size"], Is.TypeOf<int>());
            Assert.That(gpu["memory_size"], Is.EqualTo(FakeUnityContextSource.DefaultGraphicsMemoryMegabytes));

            var runtime = contexts["runtime"];
            Assert.That(runtime.Keys, Is.EquivalentTo(new[] { "name", "version", "scripting_backend" }));
            Assert.That(runtime["name"], Is.EqualTo("Unity"));
            Assert.That(runtime["version"], Is.EqualTo(FakeUnityContextSource.DefaultUnityVersion));
            Assert.That(runtime["scripting_backend"], Is.EqualTo(FakeUnityContextSource.DefaultScriptingBackend));

            var app = contexts["app"];
            Assert.That(
                app.Keys,
                Is.EquivalentTo(new[]
                {
                    "product_name",
                    "version",
                    "identifier",
                    "company",
                    "is_editor",
                    "is_development_build",
                }));
            Assert.That(app["product_name"], Is.EqualTo(FakeUnityContextSource.DefaultProductName));
            Assert.That(app["version"], Is.EqualTo(FakeUnityContextSource.DefaultProductVersion));
            Assert.That(app["identifier"], Is.EqualTo(FakeUnityContextSource.DefaultApplicationIdentifier));
            Assert.That(app["company"], Is.EqualTo(FakeUnityContextSource.DefaultCompanyName));
            Assert.That(app["is_editor"], Is.EqualTo(false));
            Assert.That(app["is_development_build"], Is.EqualTo(true));

            var game = contexts["game"];
            Assert.That(game.Keys, Is.EquivalentTo(new[] { "scene", "frame_count", "realtime_since_startup" }));
            Assert.That(game["scene"], Is.EqualTo(FakeUnityContextSource.DefaultSceneName));
            Assert.That(game["frame_count"], Is.TypeOf<int>());
            Assert.That(game["frame_count"], Is.EqualTo(FakeUnityContextSource.DefaultFrameCount));
            Assert.That(game["realtime_since_startup"], Is.TypeOf<float>());
            Assert.That(
                game["realtime_since_startup"],
                Is.EqualTo(FakeUnityContextSource.DefaultRealtimeSinceStartup));
        }

        [Test]
        public void DeviceMemorySizeIsConvertedFromMegabytesToBytes()
        {
            _source.SystemMemoryMegabytes = 1024L;

            _provider.Collect();
            _provider.RefreshFrame();

            var memorySize = _provider.CreateContexts()["device"]["memory_size"];

            Assert.That(memorySize, Is.TypeOf<long>());
            Assert.That(memorySize, Is.EqualTo(1073741824L));
        }

        [TestCase(null, false)]
        [TestCase(-1f, false)]
        [TestCase(-0.01f, false)]
        [TestCase(0f, true)]
        [TestCase(1f, true)]
        public void BatteryLevelIsOnlyIncludedWhenItIsKnown(float? batteryLevel, bool expected)
        {
            _source.BatteryLevel = batteryLevel;

            _provider.Collect();
            _provider.RefreshFrame();

            var device = _provider.CreateContexts()["device"];
            Assert.That(device.ContainsKey("battery_level"), Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(device["battery_level"], Is.EqualTo(batteryLevel!.Value));
            }
        }

        [Test]
        public void UnavailableOptionalValuesKeepTheirDocumentedKeys()
        {
            _source.DeviceModel = null;
            _source.DeviceType = null;
            _source.ProcessorType = null;
            _source.BatteryLevel = null;
            _source.GpuName = null;
            _source.GpuVendor = null;
            _source.GpuVersion = null;
            _source.GraphicsApi = null;
            _source.GraphicsMemoryMegabytes = null;
            _source.ApplicationIdentifier = null;
            _source.CompanyName = null;
            _source.CurrentSceneName = null;

            _provider.Collect();
            _provider.RefreshFrame();

            var contexts = _provider.CreateContexts();
            Assert.That(contexts["device"].ContainsKey("battery_level"), Is.False);
            Assert.That(contexts["device"]["model"], Is.Null);
            Assert.That(contexts["device"]["type"], Is.Null);
            Assert.That(contexts["device"]["processor_type"], Is.Null);
            Assert.That(contexts["gpu"]["name"], Is.Null);
            Assert.That(contexts["gpu"]["vendor"], Is.Null);
            Assert.That(contexts["gpu"]["version"], Is.Null);
            Assert.That(contexts["gpu"]["api"], Is.Null);
            Assert.That(contexts["gpu"]["memory_size"], Is.Null);
            Assert.That(contexts["app"]["identifier"], Is.Null);
            Assert.That(contexts["app"]["company"], Is.Null);
            Assert.That(contexts["game"]["scene"], Is.Null);
        }

        [Test]
        public void CollectSnapshotsStaticValuesAndRefreshFrameRefreshesGameValues()
        {
            _provider.Collect();
            _provider.RefreshFrame();

            _source.OperatingSystem = "Linux";
            _source.ProductName = "Changed Product";
            _source.CurrentSceneName = "Stage2";
            _source.FrameCount = 5;
            _source.RealtimeSinceStartup = 10f;

            var stale = _provider.CreateContexts();
            Assert.That(stale["os"]["name"], Is.EqualTo(FakeUnityContextSource.DefaultOperatingSystem));
            Assert.That(stale["app"]["product_name"], Is.EqualTo(FakeUnityContextSource.DefaultProductName));
            Assert.That(stale["game"]["scene"], Is.EqualTo(FakeUnityContextSource.DefaultSceneName));
            Assert.That(stale["game"]["frame_count"], Is.EqualTo(FakeUnityContextSource.DefaultFrameCount));
            Assert.That(
                stale["game"]["realtime_since_startup"],
                Is.EqualTo(FakeUnityContextSource.DefaultRealtimeSinceStartup));

            _provider.RefreshFrame();

            var refreshed = _provider.CreateContexts();
            Assert.That(refreshed["os"]["name"], Is.EqualTo(FakeUnityContextSource.DefaultOperatingSystem));
            Assert.That(refreshed["app"]["product_name"], Is.EqualTo(FakeUnityContextSource.DefaultProductName));
            Assert.That(refreshed["game"]["scene"], Is.EqualTo("Stage2"));
            Assert.That(refreshed["game"]["frame_count"], Is.EqualTo(5));
            Assert.That(refreshed["game"]["realtime_since_startup"], Is.EqualTo(10f));

            _provider.Collect();

            Assert.That(_provider.CreateContexts()["os"]["name"], Is.EqualTo("Linux"));
            Assert.That(
                _provider.CreateContexts()["app"]["product_name"],
                Is.EqualTo("Changed Product"));
        }

        [Test]
        public void CreateTagsReturnsPlatformAndScene()
        {
            _provider.Collect();
            _provider.RefreshFrame();

            var tags = _provider.CreateTags();

            Assert.That(tags.Keys, Is.EquivalentTo(new[] { "unity.platform", "unity.scene" }));
            Assert.That(tags["unity.platform"], Is.EqualTo(FakeUnityContextSource.DefaultPlatform));
            Assert.That(tags["unity.scene"], Is.EqualTo(FakeUnityContextSource.DefaultSceneName));
        }

        [TestCase(null)]
        [TestCase("")]
        public void SceneTagIsEmptyWhenNoSceneIsAvailable(string? sceneName)
        {
            _source.CurrentSceneName = sceneName;

            _provider.Collect();
            _provider.RefreshFrame();

            Assert.That(_provider.CreateTags()["unity.scene"], Is.EqualTo(string.Empty));
        }

        [Test]
        public void CreateUserReturnsNullWhenDefaultPiiIsDisabled()
        {
            Assert.That(_provider.CreateUser(false), Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void CreateUserReturnsNullWhenTheDeviceIdentifierIsBlank(string? deviceIdentifier)
        {
            _source.DeviceUniqueIdentifier = deviceIdentifier;

            Assert.That(_provider.CreateUser(true), Is.Null);
        }

        [Test]
        public void CreateUserHashesTheDeviceIdentifierToSixteenLowercaseHexCharacters()
        {
            _source.DeviceUniqueIdentifier = "device-123";

            var user = _provider.CreateUser(true);

            Assert.That(user, Is.Not.Null);
            Assert.That(user!.Id, Is.EqualTo("27bb7491278d5a37"));
            Assert.That(user.Id!.Length, Is.EqualTo(16));
            Assert.That(user.Id, Does.Match("^[0-9a-f]{16}$"));
        }

        [Test]
        public void CreateUserHashDependsOnTheWholeUtf8Identifier()
        {
            _source.DeviceUniqueIdentifier = "player-42";

            var user = _provider.CreateUser(true);

            Assert.That(user, Is.Not.Null);
            Assert.That(user!.Id, Is.EqualTo("c3214538d133c9d9"));
        }

        [Test]
        public void SystemSourceReadsUnityValuesWithoutThrowing()
        {
            var source = new UnitySystemContextSource();

            // Several SystemInfo members are unsupported in batch mode and on
            // some players; reading each one must yield a fallback, not throw.
            Assert.That(source.OperatingSystem, Is.Not.Null);
            Assert.That(source.OperatingSystemFamily, Is.Not.Null);
            Assert.That(source.SystemMemoryMegabytes, Is.GreaterThanOrEqualTo(0L));
            Assert.That(source.ProcessorCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(source.ScriptingBackend, Is.EqualTo("Mono").Or.EqualTo("IL2CPP"));
            Assert.That(source.UnityVersion, Is.EqualTo(Application.unityVersion));
            Assert.That(source.ProductName, Is.EqualTo(Application.productName));
            Assert.That(source.ProductVersion, Is.EqualTo(Application.version));
            Assert.That(source.Platform, Is.EqualTo(Application.platform.ToString()));
            Assert.That(source.IsEditor, Is.True);
            Assert.That(source.FrameCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(source.RealtimeSinceStartup, Is.GreaterThanOrEqualTo(0f));

            // These are platform-dependent and may legitimately be null/empty.
            Assert.That(
                () =>
                {
                    _ = source.DeviceModel;
                    _ = source.DeviceType;
                    _ = source.ProcessorType;
                    _ = source.BatteryLevel;
                    _ = source.GpuName;
                    _ = source.GpuVendor;
                    _ = source.GpuVersion;
                    _ = source.GraphicsApi;
                    _ = source.GraphicsMemoryMegabytes;
                    _ = source.ApplicationIdentifier;
                    _ = source.CompanyName;
                    _ = source.IsDevelopmentBuild;
                    _ = source.CurrentSceneName;
                    _ = source.DeviceUniqueIdentifier;
                },
                Throws.Nothing);

            var provider = new UnityContextProvider(source);
            provider.Collect();
            provider.RefreshFrame();

            Assert.That(provider.CreateContexts().Keys, Is.EquivalentTo(
                new[] { "os", "device", "gpu", "runtime", "app", "game" }));
            Assert.That(provider.CreateTags()["unity.platform"], Is.EqualTo(Application.platform.ToString()));
        }

        private sealed class FakeUnityContextSource : IUnityContextSource
        {
            public const string DefaultOperatingSystem = "Windows 11 (10.0.22631) 64bit";
            public const string DefaultOperatingSystemFamily = "Windows";
            public const string DefaultDeviceModel = "Test Device";
            public const string DefaultDeviceType = "Desktop";
            public const long DefaultSystemMemoryMegabytes = 16384L;
            public const string DefaultProcessorType = "Test CPU";
            public const int DefaultProcessorCount = 12;
            public const float DefaultBatteryLevel = 0.75f;
            public const string DefaultGpuName = "Test GPU";
            public const string DefaultGpuVendor = "Test Vendor";
            public const string DefaultGpuVersion = "1.2.3";
            public const string DefaultGraphicsApi = "Direct3D11";
            public const int DefaultGraphicsMemoryMegabytes = 8192;
            public const string DefaultUnityVersion = "2022.3.42f1";
            public const string DefaultScriptingBackend = "Mono";
            public const string DefaultProductName = "Star Raiders";
            public const string DefaultProductVersion = "1.2.3";
            public const string DefaultApplicationIdentifier = "com.example.starraiders";
            public const string DefaultCompanyName = "Example Studio";
            public const string DefaultPlatform = "WindowsPlayer";
            public const string DefaultSceneName = "Stage1";
            public const int DefaultFrameCount = 12345;
            public const float DefaultRealtimeSinceStartup = 67.5f;
            public const string DefaultDeviceUniqueIdentifier = "device-123";

            public string OperatingSystem { get; set; } = DefaultOperatingSystem;
            public string OperatingSystemFamily { get; set; } = DefaultOperatingSystemFamily;
            public string? DeviceModel { get; set; } = DefaultDeviceModel;
            public string? DeviceType { get; set; } = DefaultDeviceType;
            public long SystemMemoryMegabytes { get; set; } = DefaultSystemMemoryMegabytes;
            public string? ProcessorType { get; set; } = DefaultProcessorType;
            public int ProcessorCount { get; set; } = DefaultProcessorCount;
            public float? BatteryLevel { get; set; } = DefaultBatteryLevel;
            public string? GpuName { get; set; } = DefaultGpuName;
            public string? GpuVendor { get; set; } = DefaultGpuVendor;
            public string? GpuVersion { get; set; } = DefaultGpuVersion;
            public string? GraphicsApi { get; set; } = DefaultGraphicsApi;
            public int? GraphicsMemoryMegabytes { get; set; } = DefaultGraphicsMemoryMegabytes;
            public string UnityVersion { get; set; } = DefaultUnityVersion;
            public string ScriptingBackend { get; set; } = DefaultScriptingBackend;
            public string ProductName { get; set; } = DefaultProductName;
            public string ProductVersion { get; set; } = DefaultProductVersion;
            public string? ApplicationIdentifier { get; set; } = DefaultApplicationIdentifier;
            public string? CompanyName { get; set; } = DefaultCompanyName;
            public bool IsEditor { get; set; }
            public bool IsDevelopmentBuild { get; set; } = true;
            public string Platform { get; set; } = DefaultPlatform;
            public string? CurrentSceneName { get; set; } = DefaultSceneName;
            public int FrameCount { get; set; } = DefaultFrameCount;
            public float RealtimeSinceStartup { get; set; } = DefaultRealtimeSinceStartup;
            public string? DeviceUniqueIdentifier { get; set; } = DefaultDeviceUniqueIdentifier;
        }
    }
}
