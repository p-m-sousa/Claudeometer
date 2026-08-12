using System;
using System.IO;
using System.Text;

namespace ClaudeUsage.WinForms
{
    internal sealed class SettingsStore
    {
        private readonly string _settingsPath;

        public SettingsStore()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeUsage");
            _settingsPath = Path.Combine(directory, "settings.txt");
        }

        public string ReadSourcePath()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return null;
                var value = File.ReadAllText(_settingsPath, Encoding.UTF8).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        public void WriteSourcePath(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_settingsPath, path ?? string.Empty, new UTF8Encoding(false));
            }
            catch
            {
                // Preferences are optional; a locked-down profile should not stop the viewer.
            }
        }

        public int ReadRefreshSeconds()
        {
            try
            {
                var intervalPath = _settingsPath + ".interval";
                int seconds;
                return File.Exists(intervalPath) &&
                       int.TryParse(File.ReadAllText(intervalPath, Encoding.ASCII).Trim(), out seconds)
                    ? seconds
                    : 30;
            }
            catch
            {
                return 30;
            }
        }

        public void WriteRefreshSeconds(int seconds)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_settingsPath + ".interval", seconds.ToString(), Encoding.ASCII);
            }
            catch
            {
                // Preferences are optional.
            }
        }
    }
}
