#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GlitchPort.Unity
{
    /// <summary>
    /// Supplies the device, runtime and application values that the Unity SDK
    /// attaches to every event. The interface exists so the provider can be
    /// unit tested without a running player.
    /// </summary>
    public interface IUnityContextSource
    {
        /// <summary>Gets the operating system description, for example <c>Windows 11  (10.0.22000) 64bit</c>.</summary>
        string OperatingSystem { get; }

        /// <summary>Gets the operating system family, for example <c>Windows</c>.</summary>
        string OperatingSystemFamily { get; }

        /// <summary>Gets the device model, or <see langword="null"/> when the platform does not report one.</summary>
        string? DeviceModel { get; }

        /// <summary>Gets the device type, or <see langword="null"/> when the platform does not report one.</summary>
        string? DeviceType { get; }

        /// <summary>Gets the system memory in megabytes.</summary>
        long SystemMemoryMegabytes { get; }

        /// <summary>Gets the processor description, or <see langword="null"/> when unavailable.</summary>
        string? ProcessorType { get; }

        /// <summary>Gets the number of logical processors.</summary>
        int ProcessorCount { get; }

        /// <summary>Gets the battery charge in the range 0 to 1, or a negative value when unknown.</summary>
        float? BatteryLevel { get; }

        /// <summary>Gets the graphics device name, or <see langword="null"/> when unavailable.</summary>
        string? GpuName { get; }

        /// <summary>Gets the graphics device vendor, or <see langword="null"/> when unavailable.</summary>
        string? GpuVendor { get; }

        /// <summary>Gets the graphics driver version, or <see langword="null"/> when unavailable.</summary>
        string? GpuVersion { get; }

        /// <summary>Gets the graphics API name, for example <c>Direct3D11</c>, or <see langword="null"/> when unavailable.</summary>
        string? GraphicsApi { get; }

        /// <summary>Gets the graphics memory in megabytes, or <see langword="null"/> when unavailable.</summary>
        int? GraphicsMemoryMegabytes { get; }

        /// <summary>Gets the Unity version the game was built with.</summary>
        string UnityVersion { get; }

        /// <summary>Gets the scripting backend name, either <c>Mono</c> or <c>IL2CPP</c>.</summary>
        string ScriptingBackend { get; }

        /// <summary>Gets the application product name.</summary>
        string ProductName { get; }

        /// <summary>Gets the application version.</summary>
        string ProductVersion { get; }

        /// <summary>Gets the application identifier, or <see langword="null"/> when unavailable.</summary>
        string? ApplicationIdentifier { get; }

        /// <summary>Gets the company name, or <see langword="null"/> when unavailable.</summary>
        string? CompanyName { get; }

        /// <summary>Gets a value indicating whether the game runs in the Unity editor.</summary>
        bool IsEditor { get; }

        /// <summary>Gets a value indicating whether the game is a development build.</summary>
        bool IsDevelopmentBuild { get; }

        /// <summary>Gets the runtime platform name, for example <c>WindowsPlayer</c>.</summary>
        string Platform { get; }

        /// <summary>Gets the active scene name, or <see langword="null"/> when no scene is loaded.</summary>
        string? CurrentSceneName { get; }

        /// <summary>Gets the number of frames rendered since the game started.</summary>
        int FrameCount { get; }

        /// <summary>Gets the seconds elapsed since the game started, unaffected by time scaling.</summary>
        float RealtimeSinceStartup { get; }

        /// <summary>Gets the platform device identifier, or <see langword="null"/> when unavailable.</summary>
        string? DeviceUniqueIdentifier { get; }
    }

    /// <summary>
    /// Builds the <c>contexts</c>, <c>tags</c> and optional <c>user</c> payload
    /// of an event from a <see cref="IUnityContextSource"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Collect"/> and <see cref="RefreshFrame"/> must be called on the
    /// Unity main thread because the underlying source reads engine state. They
    /// take a snapshot so serializing an event never touches the engine again:
    /// <see cref="Collect"/> captures the values that do not change during a
    /// session and <see cref="RefreshFrame"/> captures the per-frame game values.
    /// </remarks>
    public sealed class UnityContextProvider
    {
        private const string RuntimeName = "Unity";

        private readonly IUnityContextSource _source;

        private string _operatingSystem = string.Empty;
        private string _operatingSystemFamily = string.Empty;
        private string? _deviceModel;
        private string? _deviceType;
        private long _systemMemoryMegabytes;
        private string? _processorType;
        private int _processorCount;
        private float? _batteryLevel;
        private string? _gpuName;
        private string? _gpuVendor;
        private string? _gpuVersion;
        private string? _graphicsApi;
        private int? _graphicsMemoryMegabytes;
        private string _unityVersion = string.Empty;
        private string _scriptingBackend = string.Empty;
        private string _productName = string.Empty;
        private string _productVersion = string.Empty;
        private string? _applicationIdentifier;
        private string? _companyName;
        private bool _isEditor;
        private bool _isDevelopmentBuild;
        private string _platform = string.Empty;
        private string? _currentSceneName;
        private int _frameCount;
        private float _realtimeSinceStartup;

        /// <summary>Initializes a provider that reads from <paramref name="source"/>.</summary>
        /// <param name="source">The source of the Unity context values.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
        public UnityContextProvider(IUnityContextSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>
        /// Takes a snapshot of the device, GPU, runtime, application and platform
        /// values. Call this from the Unity main thread, typically once during
        /// initialization.
        /// </summary>
        public void Collect()
        {
            _operatingSystem = _source.OperatingSystem;
            _operatingSystemFamily = _source.OperatingSystemFamily;
            _deviceModel = _source.DeviceModel;
            _deviceType = _source.DeviceType;
            _systemMemoryMegabytes = _source.SystemMemoryMegabytes;
            _processorType = _source.ProcessorType;
            _processorCount = _source.ProcessorCount;
            _batteryLevel = _source.BatteryLevel;
            _gpuName = _source.GpuName;
            _gpuVendor = _source.GpuVendor;
            _gpuVersion = _source.GpuVersion;
            _graphicsApi = _source.GraphicsApi;
            _graphicsMemoryMegabytes = _source.GraphicsMemoryMegabytes;
            _unityVersion = _source.UnityVersion;
            _scriptingBackend = _source.ScriptingBackend;
            _productName = _source.ProductName;
            _productVersion = _source.ProductVersion;
            _applicationIdentifier = _source.ApplicationIdentifier;
            _companyName = _source.CompanyName;
            _isEditor = _source.IsEditor;
            _isDevelopmentBuild = _source.IsDevelopmentBuild;
            _platform = _source.Platform;
        }

        /// <summary>
        /// Refreshes the per-frame game context: the active scene, the frame
        /// count and the realtime clock. Call this from the Unity main thread
        /// before capturing an event.
        /// </summary>
        public void RefreshFrame()
        {
            _currentSceneName = _source.CurrentSceneName;
            _frameCount = _source.FrameCount;
            _realtimeSinceStartup = _source.RealtimeSinceStartup;
        }

        /// <summary>
        /// Creates the event <c>contexts</c> from the last snapshots.
        /// </summary>
        /// <returns>
        /// A dictionary with the <c>os</c>, <c>device</c>, <c>gpu</c>,
        /// <c>runtime</c>, <c>app</c> and <c>game</c> namespaces.
        /// </returns>
        public IDictionary<string, IDictionary<string, object?>> CreateContexts()
        {
            var device = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = _deviceModel,
                ["type"] = _deviceType,
                ["memory_size"] = _systemMemoryMegabytes * 1024L * 1024L,
                ["processor_type"] = _processorType,
                ["processor_count"] = _processorCount,
            };

            // SystemInfo reports a negative battery level when the platform
            // cannot determine it; omitting the key keeps that distinct from a
            // measured zero charge.
            if (_batteryLevel is { } batteryLevel && batteryLevel >= 0f)
            {
                device["battery_level"] = batteryLevel;
            }

            return new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal)
            {
                ["os"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = _operatingSystem,
                    ["family"] = _operatingSystemFamily,
                },
                ["device"] = device,
                ["gpu"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = _gpuName,
                    ["vendor"] = _gpuVendor,
                    ["version"] = _gpuVersion,
                    ["api"] = _graphicsApi,
                    ["memory_size"] = _graphicsMemoryMegabytes,
                },
                ["runtime"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = RuntimeName,
                    ["version"] = _unityVersion,
                    ["scripting_backend"] = _scriptingBackend,
                },
                ["app"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["product_name"] = _productName,
                    ["version"] = _productVersion,
                    ["identifier"] = _applicationIdentifier,
                    ["company"] = _companyName,
                    ["is_editor"] = _isEditor,
                    ["is_development_build"] = _isDevelopmentBuild,
                },
                ["game"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scene"] = _currentSceneName,
                    ["frame_count"] = _frameCount,
                    ["realtime_since_startup"] = _realtimeSinceStartup,
                },
            };
        }

        /// <summary>
        /// Creates the event tags from the last snapshots.
        /// </summary>
        /// <returns>
        /// A dictionary with <c>unity.platform</c> and <c>unity.scene</c>; the
        /// scene tag is empty when no scene is available.
        /// </returns>
        public IDictionary<string, string> CreateTags()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unity.platform"] = _platform ?? string.Empty,
                ["unity.scene"] = _currentSceneName ?? string.Empty,
            };
        }

        /// <summary>
        /// Creates the optional event user. The device identifier is never sent
        /// in clear text: it is reduced to a stable 16 character hash.
        /// </summary>
        /// <param name="sendDefaultPii">
        /// Whether the SDK may attach default personally identifiable information.
        /// </param>
        /// <returns>
        /// A user whose <see cref="UserInfo.Id"/> is the first 16 lowercase hex
        /// characters of the SHA-256 hash of the UTF-8 device identifier, or
        /// <see langword="null"/> when default PII is disabled or the platform
        /// does not provide a device identifier.
        /// </returns>
        public UserInfo? CreateUser(bool sendDefaultPii)
        {
            if (!sendDefaultPii)
            {
                return null;
            }

            var deviceIdentifier = _source.DeviceUniqueIdentifier;
            if (string.IsNullOrWhiteSpace(deviceIdentifier))
            {
                return null;
            }

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(deviceIdentifier));
                var builder = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return new UserInfo { Id = builder.ToString() };
            }
        }
    }
}
