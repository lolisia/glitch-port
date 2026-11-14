using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlitchPort.Terminal
{
    /// <summary>
    /// Terminates the renderer when its parent (the game or the relay) exits.
    /// </summary>
    internal static class ParentProcessWatcher
    {
        private const int PrSetParentDeathSignal = 1;
        private const int SigTerm = 15;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
        private static extern int Prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

        /// <summary>
        /// Starts watching <paramref name="parentProcessId"/> when it is set. On
        /// Linux the parent-death signal is requested first so the kernel can
        /// kill this process even while it is blocked on stdin.
        /// </summary>
        public static void Start(int? parentProcessId)
        {
            if (parentProcessId is not int pid)
            {
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                SetParentDeathSignal();
            }

            var watcher = new Thread(() => Watch(pid))
            {
                IsBackground = true,
                Name = "glitch-port parent watcher",
            };
            watcher.Start();
        }

        private static void SetParentDeathSignal()
        {
            try
            {
                // Failures are expected on kernels that do not support the
                // option; the polling watcher below is the fallback.
                _ = Prctl(PrSetParentDeathSignal, SigTerm, 0, 0, 0);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private static void Watch(int pid)
        {
            while (true)
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    if (parent.HasExited)
                    {
                        Environment.Exit(0);
                    }
                }
                catch (ArgumentException)
                {
                    Environment.Exit(0);
                }
                catch (InvalidOperationException)
                {
                    Environment.Exit(0);
                }

                Thread.Sleep(PollInterval);
            }
        }
    }
}
