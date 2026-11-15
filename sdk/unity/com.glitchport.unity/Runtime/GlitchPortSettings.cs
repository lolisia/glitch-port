#nullable enable
using UnityEngine;

namespace GlitchPort.Unity
{
    /// <summary>How the Unity SDK decides whether to attach the local terminal relay.</summary>
    public enum TerminalMode
    {
        /// <summary>Enable the terminal relay in a Windows editor or a Windows development build.</summary>
        Auto = 0,

        /// <summary>Enable the terminal relay whenever the platform can run it.</summary>
        On = 1,

        /// <summary>Never enable the terminal relay.</summary>
        Off = 2,
    }

    /// <summary>How the Unity SDK decides whether to attach the local file relay.</summary>
    public enum FileMode
    {
        /// <summary>Enable the file relay in the editor and in development builds.</summary>
        Auto = 0,

        /// <summary>Always enable the file relay.</summary>
        On = 1,

        /// <summary>Never enable the file relay.</summary>
        Off = 2,
    }

    /// <summary>
    /// Project-wide Unity SDK configuration. The editor creates and edits one
    /// asset at <c>Assets/GlitchPort/Resources/GlitchPortSettings.asset</c>, and
    /// the runtime loads it with <c>Resources.Load&lt;GlitchPortSettings&gt;</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Glitch Port/Settings", fileName = "GlitchPortSettings")]
    public sealed class GlitchPortSettings : ScriptableObject
    {
        /// <summary>Gets or sets a value indicating whether the SDK is enabled at all.</summary>
        [Header("General")]
        public bool Enabled = true;

        /// <summary>
        /// Gets or sets the remote relay DSN. An empty value disables HTTP delivery
        /// while leaving the local relays available.
        /// </summary>
        public string Dsn = string.Empty;

        /// <summary>Gets or sets a value indicating whether the SDK initializes before the first scene loads.</summary>
        public bool AutoInitialize = true;

        /// <summary>Gets or sets the minimum level delivered by the HTTP relay.</summary>
        public Level HttpMinLevel = Level.Error;

        /// <summary>
        /// Gets or sets the release identifier copied onto every event. An empty
        /// value uses the product name and the application version, for example
        /// <c>My Game@1.2.3</c>.
        /// </summary>
        public string Release = string.Empty;

        /// <summary>
        /// Gets or sets the deployment environment copied onto every event. An
        /// empty value uses <c>editor</c>, <c>development</c> or
        /// <c>production</c> depending on where the game runs.
        /// </summary>
        public string Environment = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether personally identifiable
        /// information, such as the user name or IP address, may be collected and
        /// sent.
        /// </summary>
        public bool SendDefaultPii = false;

        /// <summary>Gets or sets the maximum number of breadcrumbs kept for an event.</summary>
        public int MaxBreadcrumbs = 100;

        /// <summary>Gets or sets the fraction of events that is sent, between 0 and 1.</summary>
        [Range(0f, 1f)]
        public float SampleRate = 1f;

        /// <summary>Gets or sets a value indicating whether <see cref="LogType.Exception"/> messages are captured.</summary>
        [Header("Log capture")]
        public bool CaptureExceptions = true;

        /// <summary>Gets or sets a value indicating whether <see cref="LogType.Error"/> and <see cref="LogType.Assert"/> messages are captured.</summary>
        public bool CaptureErrors = true;

        /// <summary>Gets or sets a value indicating whether <see cref="LogType.Warning"/> messages are captured.</summary>
        public bool CaptureWarnings = false;

        /// <summary>Gets or sets a value indicating whether <see cref="LogType.Log"/> messages are captured.</summary>
        public bool CaptureLogs = false;

        /// <summary>Gets or sets a value indicating whether captured log messages are recorded as breadcrumbs.</summary>
        public bool CaptureBreadcrumbsFromLogs = true;

        /// <summary>Gets or sets when the local terminal relay is attached.</summary>
        [Header("Local relays")]
        public TerminalMode TerminalMode = GlitchPort.Unity.TerminalMode.Auto;

        /// <summary>Gets or sets whether the terminal opens a new console window or inherits the current one.</summary>
        public TerminalWindow TerminalWindow = GlitchPort.TerminalWindow.New;

        /// <summary>Gets or sets when the local file relay is attached.</summary>
        public FileMode FileMode = GlitchPort.Unity.FileMode.Auto;

        /// <summary>
        /// Gets or sets the directory the file relay writes to. An empty value uses
        /// <c>Application.persistentDataPath/glitchport/logs</c>.
        /// </summary>
        public string FileDirectory = string.Empty;

        /// <summary>Gets or sets the minimum level written by the file relay.</summary>
        public Level FileMinLevel = Level.Debug;

        /// <summary>Gets or sets a value indicating whether SDK diagnostics are written to the Unity console.</summary>
        [Header("Diagnostics")]
        public bool DebugLogging = false;
    }
}
