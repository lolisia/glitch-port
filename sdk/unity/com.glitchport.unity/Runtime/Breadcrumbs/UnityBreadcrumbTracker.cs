#nullable enable
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GlitchPort.Unity
{
    /// <summary>
    /// Subscribes to the Unity lifecycle callbacks that are worth recording as
    /// breadcrumbs and forwards them to the configured callback.
    /// </summary>
    /// <remarks>
    /// The tracker owns a hidden, don't-save <see cref="GameObject"/> for
    /// <see cref="MonoBehaviour.OnApplicationPause"/> because that callback is
    /// only delivered to components, not to a plain C# object. Both
    /// <see cref="Start"/> and <see cref="Dispose"/> are idempotent and never
    /// throw into Unity's callback dispatch.
    /// </remarks>
    public sealed class UnityBreadcrumbTracker : IDisposable
    {
        private const string PauseHostName = "GlitchPort Breadcrumb Tracker";

        private readonly Action<Breadcrumb> _addBreadcrumb;

        private GameObject? _pauseHost;
        private bool _started;

        /// <summary>Initializes a tracker that forwards breadcrumbs to <paramref name="addBreadcrumb"/>.</summary>
        /// <param name="addBreadcrumb">The callback that stores or discards a breadcrumb.</param>
        /// <exception cref="ArgumentNullException"><paramref name="addBreadcrumb"/> is <see langword="null"/>.</exception>
        public UnityBreadcrumbTracker(Action<Breadcrumb> addBreadcrumb)
        {
            _addBreadcrumb = addBreadcrumb ?? throw new ArgumentNullException(nameof(addBreadcrumb));
        }

        /// <summary>
        /// Starts recording lifecycle breadcrumbs. Calling this method more than
        /// once has no additional effect, and calling it when the platform does
        /// not expose one of the callbacks leaves the remaining subscriptions
        /// active.
        /// </summary>
        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;

            try
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                SceneManager.sceneUnloaded += HandleSceneUnloaded;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                SceneManager.activeSceneChanged += HandleActiveSceneChanged;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                Application.focusChanged += HandleFocusChanged;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                Application.lowMemory += HandleLowMemory;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                _pauseHost = new GameObject(PauseHostName)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var pauseListener = _pauseHost.AddComponent<PauseListener>();
                pauseListener.Initialize(HandleApplicationPause);

                // Keep the pause host across scene loads in a player. EditMode
                // does not allow DontDestroyOnLoad.
                if (Application.isPlaying)
                {
                    Object.DontDestroyOnLoad(_pauseHost);
                }
            }
            catch (Exception)
            {
                DestroyPauseHost();
            }
        }

        /// <summary>
        /// Stops recording and destroys the hidden pause host. Calling this
        /// method more than once has no additional effect.
        /// </summary>
        public void Dispose()
        {
            Unsubscribe();
            DestroyPauseHost();
            _started = false;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Add(UnityBreadcrumbFactory.SceneLoaded(scene.name));
        }

        private void HandleSceneUnloaded(Scene scene)
        {
            Add(UnityBreadcrumbFactory.SceneUnloaded(scene.name));
        }

        private void HandleActiveSceneChanged(Scene previous, Scene current)
        {
            Add(UnityBreadcrumbFactory.ActiveSceneChanged(previous.name, current.name));
        }

        private void HandleFocusChanged(bool focused)
        {
            Add(UnityBreadcrumbFactory.ApplicationFocus(focused));
        }

        private void HandleLowMemory()
        {
            Add(UnityBreadcrumbFactory.LowMemory());
        }

        private void HandleApplicationPause(bool paused)
        {
            Add(UnityBreadcrumbFactory.ApplicationPause(paused));
        }

        private void Add(Breadcrumb breadcrumb)
        {
            // Breadcrumb storage belongs to the caller; a throwing consumer must
            // not break Unity's callback dispatch.
            try
            {
                _addBreadcrumb(breadcrumb);
            }
            catch (Exception)
            {
                // The Unity SDK never reports breadcrumb failures.
            }
        }

        private void Unsubscribe()
        {
            try
            {
                SceneManager.sceneLoaded -= HandleSceneLoaded;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                Application.focusChanged -= HandleFocusChanged;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }

            try
            {
                Application.lowMemory -= HandleLowMemory;
            }
            catch (Exception)
            {
                // The callback is unavailable on this platform.
            }
        }

        private void DestroyPauseHost()
        {
            var host = _pauseHost;
            _pauseHost = null;
            if (host == null)
            {
                return;
            }

            try
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(host);
                }
                else
                {
                    Object.DestroyImmediate(host);
                }
            }
            catch (Exception)
            {
                // Destroying a leaked hidden object must not throw out of Dispose.
            }
        }

        /// <summary>
        /// Receives <see cref="MonoBehaviour.OnApplicationPause"/> for the
        /// hidden host. The class is private because nothing else should find or
        /// add it.
        /// </summary>
        private sealed class PauseListener : MonoBehaviour
        {
            private Action<bool>? _callback;

            public void Initialize(Action<bool> callback)
            {
                _callback = callback;
            }

            private void OnApplicationPause(bool paused)
            {
                var callback = _callback;
                if (callback == null)
                {
                    return;
                }

                try
                {
                    callback(paused);
                }
                catch (Exception)
                {
                    // The tracker already guards the callback; this protects the
                    // Unity message pump from a broken consumer too.
                }
            }
        }
    }
}
