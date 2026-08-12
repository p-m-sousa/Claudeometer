using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeUsage.WinForms
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = RuntimeSelfTest.Run() ? 0 : 1;
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            var explicitPath = ReadStatsFileArgument(args);
            Application.Run(new MainForm(explicitPath));
        }

        private static string ReadStatsFileArgument(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--stats-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[i + 1]));
                }

                const string prefix = "--stats-file=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(Environment.ExpandEnvironmentVariables(arg.Substring(prefix.Length)));
                }
            }

            return null;
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ShowUnexpectedError();
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowUnexpectedError();
        }

        private static void ShowUnexpectedError()
        {
            MessageBox.Show(
                "Claude Usage hit an unexpected error. Your Claude files were not changed. Close and reopen the app, then choose the stats cache again if needed.",
                "Claude Usage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
