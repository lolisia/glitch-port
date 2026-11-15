#nullable enable
using System;
using System.Collections.Generic;

namespace GlitchPort.Unity
{
    /// <summary>
    /// Creates the lifecycle breadcrumbs the Unity SDK records automatically.
    /// The factory is a pure function of its arguments so the produced entries
    /// can be unit tested without a running player.
    /// </summary>
    public static class UnityBreadcrumbFactory
    {
        private const string SceneCategory = "scene";
        private const string LifecycleCategory = "app.lifecycle";
        private const string DeviceCategory = "device";
        private const string LogCategory = "log";
        private const string DefaultType = "default";

        /// <summary>Creates the breadcrumb recorded when a scene finishes loading.</summary>
        /// <param name="scene">The loaded scene name.</param>
        /// <returns>A <c>scene</c> breadcrumb at <see cref="Level.Info"/>.</returns>
        public static Breadcrumb SceneLoaded(string scene)
        {
            var name = scene ?? string.Empty;
            return Create(
                SceneCategory,
                "Scene loaded: " + name,
                Level.Info,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scene"] = name,
                });
        }

        /// <summary>Creates the breadcrumb recorded when a scene is unloaded.</summary>
        /// <param name="scene">The unloaded scene name.</param>
        /// <returns>A <c>scene</c> breadcrumb at <see cref="Level.Info"/>.</returns>
        public static Breadcrumb SceneUnloaded(string scene)
        {
            var name = scene ?? string.Empty;
            return Create(
                SceneCategory,
                "Scene unloaded: " + name,
                Level.Info,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scene"] = name,
                });
        }

        /// <summary>Creates the breadcrumb recorded when the active scene changes.</summary>
        /// <param name="previous">The previous active scene name.</param>
        /// <param name="current">The new active scene name.</param>
        /// <returns>A <c>scene</c> breadcrumb at <see cref="Level.Info"/>.</returns>
        public static Breadcrumb ActiveSceneChanged(string previous, string current)
        {
            var previousName = previous ?? string.Empty;
            var currentName = current ?? string.Empty;
            return Create(
                SceneCategory,
                "Active scene changed from '" + previousName + "' to '" + currentName + "'",
                Level.Info,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["previous_scene"] = previousName,
                    ["current_scene"] = currentName,
                });
        }

        /// <summary>Creates the breadcrumb recorded when the application gains or loses focus.</summary>
        /// <param name="focused">Whether the application is now focused.</param>
        /// <returns>An <c>app.lifecycle</c> breadcrumb at <see cref="Level.Info"/>.</returns>
        public static Breadcrumb ApplicationFocus(bool focused)
        {
            return Create(
                LifecycleCategory,
                focused ? "Application gained focus" : "Application lost focus",
                Level.Info,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["focused"] = focused,
                });
        }

        /// <summary>Creates the breadcrumb recorded when the application is paused or resumed.</summary>
        /// <param name="paused">Whether the application is now paused.</param>
        /// <returns>An <c>app.lifecycle</c> breadcrumb at <see cref="Level.Info"/>.</returns>
        public static Breadcrumb ApplicationPause(bool paused)
        {
            return Create(
                LifecycleCategory,
                paused ? "Application paused" : "Application resumed",
                Level.Info,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["paused"] = paused,
                });
        }

        /// <summary>Creates the breadcrumb recorded when the platform reports low memory.</summary>
        /// <returns>A <c>device</c> breadcrumb at <see cref="Level.Warning"/>.</returns>
        public static Breadcrumb LowMemory()
        {
            return Create(
                DeviceCategory,
                "Low memory warning",
                Level.Warning,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["low_memory"] = true,
                });
        }

        /// <summary>Creates a breadcrumb for a captured Unity log message.</summary>
        /// <param name="message">The log message.</param>
        /// <param name="level">The severity of the log message.</param>
        /// <returns>A <c>log</c> breadcrumb at <paramref name="level"/>.</returns>
        public static Breadcrumb Log(string message, Level level)
        {
            return Create(LogCategory, message ?? string.Empty, level, data: null);
        }

        private static Breadcrumb Create(
            string category,
            string message,
            Level level,
            IDictionary<string, object?>? data)
        {
            return new Breadcrumb
            {
                Timestamp = DateTimeOffset.UtcNow,
                Category = category,
                Message = message,
                Level = level,
                Type = DefaultType,
                Data = data,
            };
        }
    }
}
