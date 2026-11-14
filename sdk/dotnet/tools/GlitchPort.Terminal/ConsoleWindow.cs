using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GlitchPort.Terminal
{
    /// <summary>
    /// Windows console helpers: re-attaching a fresh console window, setting the
    /// title and enabling ANSI/VT colour output.
    /// </summary>
    internal static class ConsoleWindow
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;
        private static readonly IntPtr InvalidHandleValue = new(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        /// <summary>
        /// Detaches from the inherited console and allocates a new window. The
        /// standard streams must be opened again afterwards.
        /// </summary>
        public static void Reattach()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            FreeConsole();
            AllocConsole();
        }

        /// <summary>Sets the console title when the process owns a real console.</summary>
        public static void TrySetTitle(string appName)
        {
            if (!OperatingSystem.IsWindows() || GetConsoleWindow() == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Console.Title = string.IsNullOrEmpty(appName)
                    ? "glitch-port terminal"
                    : "glitch-port terminal - " + appName;
            }
            catch (IOException)
            {
                // No console attached; the title is best effort.
            }
            catch (PlatformNotSupportedException)
            {
                // No console attached; the title is best effort.
            }
        }

        /// <summary>
        /// Returns whether ANSI colour should be emitted: only on a real console
        /// whose VT processing mode could be enabled.
        /// </summary>
        public static bool EnableColor()
        {
            if (OperatingSystem.IsWindows())
            {
                return TryEnableVirtualTerminalProcessing();
            }

            return !Console.IsOutputRedirected;
        }

        private static bool TryEnableVirtualTerminalProcessing()
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
    }
}
