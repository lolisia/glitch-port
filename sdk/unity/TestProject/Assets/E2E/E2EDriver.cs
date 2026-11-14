#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using GlitchPort;
using GlitchPort.Unity;
using UnityEngine;

namespace GlitchPort.TestProject
{
    /// <summary>
    /// Drives the Unity end-to-end check used by <c>scripts/e2e-unity.sh</c>.
    /// The component does nothing unless the player was started with
    /// <c>-glitchport-e2e</c>. In that case it proves that the SDK initialized
    /// itself before the scene loaded, that Unity logs and the manual capture API
    /// are queued, and that a flush delivers them; the player then exits with 0
    /// on success and 1 on failure.
    /// </summary>
    public sealed class E2EDriver : MonoBehaviour
    {
        /// <summary>The command line flag that enables the end-to-end run.</summary>
        public const string CommandLineFlag = "-glitchport-e2e";

        /// <summary>The error message the automatic log capture must report.</summary>
        public const string CapturedErrorMessage = "e2e unity error";

        /// <summary>The warning message the manual capture API must report.</summary>
        public const string CapturedWarningMessage = "e2e unity message";

        private const string DiagnosticPrefix = "[GlitchPort]";

        private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

        private async void Awake()
        {
            if (!HasCommandLineFlag())
            {
                return;
            }

            var exitCode = 1;

            try
            {
                exitCode = await RunAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError(DiagnosticPrefix + " E2E driver failed: " + exception);
            }
            finally
            {
                Application.Quit(exitCode);
            }
        }

        private static async Task<int> RunAsync()
        {
            // The SDK is initialized by its RuntimeInitializeOnLoadMethod hook
            // before the first scene loads; the driver never calls Init().
            if (!GlitchPortUnity.IsInitialized)
            {
                Debug.LogError(
                    DiagnosticPrefix + " E2E driver: the SDK was not initialized automatically. "
                    + "Check GLITCHPORT_DSN.");
                return 1;
            }

            // Captured by the thread-safe Unity log bridge at Level.Error.
            Debug.LogError(CapturedErrorMessage);

            // Captured through the manual API at Level.Warning. Capture only
            // enqueues the event and returns its id immediately.
            var messageId = GlitchPortUnity.CaptureMessage(CapturedWarningMessage, Level.Warning);

            // Drains every relay, including the file relay configured through
            // GLITCHPORT_DSN, before the player exits.
            var report = await GlitchPortUnity.FlushAsync(FlushTimeout);

            if (messageId is null)
            {
                Debug.LogError(DiagnosticPrefix + " E2E driver: CaptureMessage returned no event id.");
                return 1;
            }

            if (report is null)
            {
                Debug.LogError(DiagnosticPrefix + " E2E driver: FlushAsync returned no delivery report.");
                return 1;
            }

            if (!report.AllSucceeded)
            {
                Debug.LogError(DiagnosticPrefix + " E2E driver: " + DescribeFailures(report));
                return 1;
            }

            return 0;
        }

        private static string DescribeFailures(DeliveryReport report)
        {
            var builder = new StringBuilder("the flush did not deliver every event:");

            for (var i = 0; i < report.Relays.Count; i++)
            {
                var relay = report.Relays[i];
                builder.Append(' ');
                builder.Append(relay.Name);
                builder.Append('=');
                builder.Append(relay.Succeeded ? "ok" : (relay.LastError ?? "failed"));
            }

            return builder.ToString();
        }

        private static bool HasCommandLineFlag()
        {
            var arguments = Environment.GetCommandLineArgs();

            for (var i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], CommandLineFlag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
