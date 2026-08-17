using System;
using System.Collections.Generic;
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
            if (args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = RuntimeSelfTest.Run(Console.Out) ? 0 : 1;
                return;
            }

            if (args.Any(argument => string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(argument, "/?", StringComparison.Ordinal)))
            {
                MessageBox.Show(
                    "Claude Usage shows local Claude Code token usage.\r\n\r\n" +
                    "ClaudeUsage.exe [--data-dir <folder>] [--self-test]\r\n\r\n" +
                    "--data-dir   Read Claude Code data from this folder (the one containing \"projects\").\r\n" +
                    "             Repeat the switch to read more than one folder.\r\n" +
                    "--self-test  Verify the scanner, analytics, and PDF writer, then exit.",
                    "Claude Usage",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.Run(new MainForm(ReadDataDirectories(args)));
        }

        /// <summary>
        /// Accepts <c>--data-dir</c> (repeatable) and the 0.1 spelling <c>--stats-file</c>, whose
        /// value pointed at a file inside the data folder.
        /// </summary>
        private static IList<string> ReadDataDirectories(string[] args)
        {
            var directories = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string value = null;
                foreach (var name in new[] { "--data-dir", "--config-dir", "--stats-file" })
                {
                    if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    {
                        value = args[index + 1];
                        index++;
                        break;
                    }

                    var prefix = name + "=";
                    if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        value = argument.Substring(prefix.Length);
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(value)) continue;
                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (System.IO.File.Exists(expanded))
                    {
                        expanded = System.IO.Path.GetDirectoryName(expanded);
                    }

                    if (!string.IsNullOrWhiteSpace(expanded)) directories.Add(expanded);
                }
                catch (ArgumentException)
                {
                    // An unusable path is ignored; discovery still runs.
                }
            }

            return directories;
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
                "Claude Usage hit an unexpected error. Your Claude Code files were not changed. Close and reopen the " +
                "app; if it keeps happening, use Data sources to check which folders are being read.",
                "Claude Usage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
