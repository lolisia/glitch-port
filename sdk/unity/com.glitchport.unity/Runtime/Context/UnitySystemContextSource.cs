#nullable enable
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GlitchPort.Unity
{
    /// <summary>
    /// Reads the Unity context from the engine. Every platform-specific call is
    /// guarded because several <see cref="SystemInfo"/> and
    /// <see cref="Application"/> members are unsupported on some targets and
    /// throw or return placeholder values there.
    /// </summary>
    public sealed class UnitySystemContextSource : IUnityContextSource
    {
#if ENABLE_IL2CPP
        private const string ScriptingBackendName = "IL2CPP";
#else
        private const string ScriptingBackendName = "Mono";
#endif

        /// <inheritdoc />
        public string OperatingSystem => GetString(() => SystemInfo.operatingSystem);

        /// <inheritdoc />
        public string OperatingSystemFamily => GetString(() => SystemInfo.operatingSystemFamily.ToString());

        /// <inheritdoc />
        public string? DeviceModel => GetNullableString(() => SystemInfo.deviceModel);

        /// <inheritdoc />
        public string? DeviceType => GetNullableString(() => SystemInfo.deviceType.ToString());

        /// <inheritdoc />
        public long SystemMemoryMegabytes => GetLong(() => SystemInfo.systemMemorySize);

        /// <inheritdoc />
        public string? ProcessorType => GetNullableString(() => SystemInfo.processorType);

        /// <inheritdoc />
        public int ProcessorCount => GetInt(() => SystemInfo.processorCount);

        /// <inheritdoc />
        public float? BatteryLevel => GetNullableFloat(() => SystemInfo.batteryLevel);

        /// <inheritdoc />
        public string? GpuName => GetNullableString(() => SystemInfo.graphicsDeviceName);

        /// <inheritdoc />
        public string? GpuVendor => GetNullableString(() => SystemInfo.graphicsDeviceVendor);

        /// <inheritdoc />
        public string? GpuVersion => GetNullableString(() => SystemInfo.graphicsDeviceVersion);

        /// <inheritdoc />
        public string? GraphicsApi => GetNullableString(() => SystemInfo.graphicsDeviceType.ToString());

        /// <inheritdoc />
        public int? GraphicsMemoryMegabytes => GetNullableInt(() => SystemInfo.graphicsMemorySize);

        /// <inheritdoc />
        public string UnityVersion => GetString(() => Application.unityVersion);

        /// <inheritdoc />
        public string ScriptingBackend => ScriptingBackendName;

        /// <inheritdoc />
        public string ProductName => GetString(() => Application.productName);

        /// <inheritdoc />
        public string ProductVersion => GetString(() => Application.version);

        /// <inheritdoc />
        public string? ApplicationIdentifier => GetNullableString(() => Application.identifier);

        /// <inheritdoc />
        public string? CompanyName => GetNullableString(() => Application.companyName);

        /// <inheritdoc />
        public bool IsEditor => GetBool(() => Application.isEditor);

        /// <inheritdoc />
        public bool IsDevelopmentBuild => GetBool(() => Debug.isDebugBuild);

        /// <inheritdoc />
        public string Platform => GetString(() => Application.platform.ToString());

        /// <inheritdoc />
        public string? CurrentSceneName
        {
            get
            {
                try
                {
                    var scene = SceneManager.GetActiveScene();
                    return scene.IsValid() && !string.IsNullOrEmpty(scene.name) ? scene.name : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <inheritdoc />
        public int FrameCount => GetInt(() => Time.frameCount);

        /// <inheritdoc />
        public float RealtimeSinceStartup => GetFloat(() => Time.realtimeSinceStartup);

        /// <inheritdoc />
        public string? DeviceUniqueIdentifier => GetNullableString(() => SystemInfo.deviceUniqueIdentifier);

        private static string GetString(Func<string?> getter)
        {
            try
            {
                return getter() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string? GetNullableString(Func<string?> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int GetInt(Func<int> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int? GetNullableInt(Func<int> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long GetLong(Func<long> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        private static float GetFloat(Func<float> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static float? GetNullableFloat(Func<float> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool GetBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
