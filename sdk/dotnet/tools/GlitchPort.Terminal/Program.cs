using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.Terminal
{
    /// <summary>Entry point of the <c>glitchport-terminal</c> renderer.</summary>
    internal static class Program
    {
        private const string Usage =
            "usage: glitchport-terminal [--parent-pid <pid>] [--format <pretty|compact|json>] [--window <new|inherit>] [--app <name>]";

        internal static async Task<int> Main(string[] args)
        {
            if (!TryParseArguments(args, out var options, out var window, out var error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(Usage);
                return 2;
            }

            if (window == TerminalWindowMode.New && OperatingSystem.IsWindows())
            {
                ConsoleWindow.Reattach();
            }

            ConsoleWindow.TrySetTitle(options.AppName);
            options.Color = ConsoleWindow.EnableColor();

            ParentProcessWatcher.Start(options.ParentProcessId);

            // Open the streams after a possible console re-attach so the writer is
            // created against the current standard handles.
            using var input = CreateInput(Console.OpenStandardInput());
            using var output = CreateOutput(Console.OpenStandardOutput());

            return await TerminalHost.RunAsync(input, output, options, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Creates the UTF-8 (no BOM) standard input reader.</summary>
        internal static TextReader CreateInput(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            return new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>Creates the UTF-8 (no BOM) standard output writer.</summary>
        internal static TextWriter CreateOutput(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }

        /// <summary>Parses the command line; on failure <paramref name="error"/> explains why.</summary>
        internal static bool TryParseArguments(
            string[] args,
            out TerminalHostOptions options,
            out TerminalWindowMode window,
            out string error)
        {
            options = new TerminalHostOptions();
            window = TerminalWindowMode.Inherit;
            error = string.Empty;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--parent-pid":
                        if (!TryTakeValue(args, ref i, out var parentPidText)
                            || !int.TryParse(parentPidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid)
                            || parentPid <= 0)
                        {
                            error = "error: --parent-pid requires a positive integer";
                            return false;
                        }

                        options.ParentProcessId = parentPid;
                        break;

                    case "--format":
                        if (!TryTakeValue(args, ref i, out var formatText))
                        {
                            error = "error: --format requires a value";
                            return false;
                        }

                        if (!TryParseFormat(formatText, out var format))
                        {
                            error = "error: --format must be one of pretty, compact or json";
                            return false;
                        }

                        options.Format = format;
                        break;

                    case "--window":
                        if (!TryTakeValue(args, ref i, out var windowText))
                        {
                            error = "error: --window requires a value";
                            return false;
                        }

                        if (!TryParseWindow(windowText, out window))
                        {
                            error = "error: --window must be new or inherit";
                            return false;
                        }

                        break;

                    case "--app":
                        if (!TryTakeValue(args, ref i, out var appName))
                        {
                            error = "error: --app requires a value";
                            return false;
                        }

                        options.AppName = appName;
                        break;

                    default:
                        error = string.Format(
                            CultureInfo.InvariantCulture,
                            "error: unknown argument '{0}'",
                            args[i]);
                        return false;
                }
            }

            return true;
        }

        private static bool TryTakeValue(string[] args, ref int index, out string value)
        {
            if (index + 1 >= args.Length)
            {
                value = string.Empty;
                return false;
            }

            index++;
            value = args[index];
            return true;
        }

        private static bool TryParseFormat(string value, out TerminalHostFormat format)
        {
            if (string.Equals(value, "pretty", StringComparison.OrdinalIgnoreCase))
            {
                format = TerminalHostFormat.Pretty;
                return true;
            }

            if (string.Equals(value, "compact", StringComparison.OrdinalIgnoreCase))
            {
                format = TerminalHostFormat.Compact;
                return true;
            }

            if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
            {
                format = TerminalHostFormat.Json;
                return true;
            }

            format = TerminalHostFormat.Pretty;
            return false;
        }

        private static bool TryParseWindow(string value, out TerminalWindowMode window)
        {
            if (string.Equals(value, "new", StringComparison.OrdinalIgnoreCase))
            {
                window = TerminalWindowMode.New;
                return true;
            }

            if (string.Equals(value, "inherit", StringComparison.OrdinalIgnoreCase))
            {
                window = TerminalWindowMode.Inherit;
                return true;
            }

            window = TerminalWindowMode.Inherit;
            return false;
        }
    }

    /// <summary>How the renderer obtains its console window.</summary>
    internal enum TerminalWindowMode
    {
        /// <summary>Use the console inherited from the parent process.</summary>
        Inherit = 0,

        /// <summary>Detach and allocate a fresh console window (Windows only).</summary>
        New = 1,
    }
}
