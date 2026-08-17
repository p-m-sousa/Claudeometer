using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace ClaudeUsage.WinForms
{
    /// <summary>
    /// Lets the user see exactly which folders are being read, pin extra ones, and rebuild the
    /// archive. Discovery is automatic, but never a black box.
    /// </summary>
    internal sealed class SourcesDialog : Form
    {
        private readonly AppSettings _settings;
        private readonly List<string> _pinned;
        private readonly ListView _list;
        private readonly CheckBox _autoDetect;
        private readonly CheckBox _minimizeToTray;
        private readonly Label _summary;
        private readonly Button _removeButton;

        internal SourcesDialog(AppSettings settings)
        {
            _settings = settings;
            _pinned = settings.Sources.ToList();

            Text = "Claude data sources";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = MainForm.Palette.Window;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(760, 430);
            MinimumSize = new Size(620, 380);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(16, 14, 16, 12),
                BackColor = MainForm.Palette.Window
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            layout.Controls.Add(new Label
            {
                Text = "Claude Usage reads Claude Code's own session transcripts. A valid folder is one that " +
                       "contains a \"projects\" subfolder - usually %USERPROFILE%\\.claude.",
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                ForeColor = MainForm.Palette.Muted,
                Margin = new Padding(0, 0, 0, 10)
            }, 0, 0);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BackColor = MainForm.Palette.Card,
                AccessibleName = "Claude data folders"
            };
            _list.Columns.Add("Folder", 420);
            _list.Columns.Add("Found via", 150);
            _list.Columns.Add("Transcripts", 90, HorizontalAlignment.Right);
            _list.SelectedIndexChanged += (sender, args) => UpdateRemoveState();
            layout.Controls.Add(_list, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 10, 0, 6)
            };

            var addButton = new Button { Text = "Add folder…", AutoSize = true, FlatStyle = FlatStyle.System };
            addButton.Click += (sender, args) => AddFolder();
            buttons.Controls.Add(addButton);

            _removeButton = new Button
            {
                Text = "Remove",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Enabled = false,
                Margin = new Padding(8, 0, 0, 0)
            };
            _removeButton.Click += (sender, args) => RemoveSelected();
            buttons.Controls.Add(_removeButton);

            var wslButton = new Button
            {
                Text = "Search WSL…",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(8, 0, 0, 0),
                AccessibleDescription = "Look for Claude Code data inside installed WSL distributions"
            };
            wslButton.Click += (sender, args) => SearchWsl();
            buttons.Controls.Add(wslButton);

            var rebuildButton = new Button
            {
                Text = "Rebuild archive…",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(24, 0, 0, 0),
                AccessibleDescription = "Discard stored daily totals and rebuild them from the transcripts on disk"
            };
            rebuildButton.Click += (sender, args) => RequestRebuild();
            buttons.Controls.Add(rebuildButton);
            layout.Controls.Add(buttons, 0, 2);

            var options = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 8)
            };
            _autoDetect = new CheckBox
            {
                Text = "Also look in the usual locations automatically",
                AutoSize = true,
                Checked = settings.AutoDetectSources
            };
            _autoDetect.CheckedChanged += (sender, args) => Populate();
            options.Controls.Add(_autoDetect);

            _minimizeToTray = new CheckBox
            {
                Text = "Keep watching from the notification area when the window is minimised or closed",
                AutoSize = true,
                Checked = settings.MinimizeToTray
            };
            options.Controls.Add(_minimizeToTray);

            _summary = new Label
            {
                Text = string.Empty,
                AutoSize = true,
                ForeColor = MainForm.Palette.Muted,
                Margin = new Padding(0, 6, 0, 0)
            };
            options.Controls.Add(_summary);
            layout.Controls.Add(options, 0, 3);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = new Padding(0, 6, 0, 0)
            };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.System };
            var ok = new Button { Text = "Save", AutoSize = true, FlatStyle = FlatStyle.System };
            ok.Click += (sender, args) => Commit();
            footer.Controls.Add(cancel);
            footer.Controls.Add(ok);
            layout.Controls.Add(footer, 0, 4);
            AcceptButton = ok;
            CancelButton = cancel;

            Populate();
        }

        /// <summary>True when the user asked for stored daily totals to be discarded and rebuilt.</summary>
        internal bool RebuildRequested { get; private set; }

        private void Populate()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var folder in _pinned)
            {
                AddRow(folder, "Chosen by you", true);
            }

            if (_autoDetect.Checked)
            {
                foreach (var location in ClaudeDataLocator.Discover())
                {
                    if (_pinned.Any(value => string.Equals(value, location.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    AddRow(location.Path, location.Source, false);
                }
            }

            _list.EndUpdate();
            UpdateRemoveState();

            var total = _list.Items.Count;
            _summary.Text = total == 0
                ? "No folder is selected yet, so there is nothing to read."
                : total.ToString(CultureInfo.CurrentCulture) + " folder(s) will be read on the next refresh.";
        }

        private void AddRow(string folder, string source, bool pinned)
        {
            var available = ClaudeDataLocator.HasProjects(folder);
            var item = new ListViewItem(folder) { Tag = pinned };
            item.SubItems.Add(available ? source : source + " (unavailable)");
            item.SubItems.Add(available
                ? ClaudeDataLocator.CountTranscripts(folder).ToString("N0", CultureInfo.CurrentCulture)
                : "-");
            if (!available) item.ForeColor = MainForm.Palette.Muted;
            _list.Items.Add(item);
        }

        private void UpdateRemoveState()
        {
            _removeButton.Enabled = _list.SelectedItems.Count == 1 && (bool)_list.SelectedItems[0].Tag;
        }

        private void AddFolder()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Select the Claude Code data folder (the one containing \"projects\")",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var normalized = ClaudeDataLocator.NormalizeChosenFolder(dialog.SelectedPath);
                if (string.IsNullOrEmpty(normalized)) return;

                if (!ClaudeDataLocator.HasProjects(normalized))
                {
                    var proceed = MessageBox.Show(
                        this,
                        normalized + "\r\n\r\nThis folder has no \"projects\" subfolder, so there is nothing to read " +
                        "there yet. Add it anyway?",
                        "Add folder",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (proceed != DialogResult.Yes) return;
                }

                if (!_pinned.Any(value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    _pinned.Add(normalized);
                }

                Populate();
            }
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItems.Count != 1) return;
            var folder = _list.SelectedItems[0].Text;
            _pinned.RemoveAll(value => string.Equals(value, folder, StringComparison.OrdinalIgnoreCase));
            Populate();
        }

        private void SearchWsl()
        {
            var distributions = ClaudeDataLocator.FindWslDistributions();
            if (distributions.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No WSL distributions are registered for this user.",
                    "Search WSL",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            IList<ClaudeDataLocation> found;
            using (var busy = new WaitCursorScope(this))
            {
                busy.Begin();
                found = ClaudeDataLocator.SearchWsl(TimeSpan.FromSeconds(12));
            }

            if (found.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No Claude Code data was found in: " + string.Join(", ", distributions.ToArray()) +
                    ".\r\n\r\nA stopped distribution can take a moment to start. If you know the path, add it with " +
                    "Add folder - for example \\\\wsl.localhost\\Ubuntu\\home\\you\\.claude.",
                    "Search WSL",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            foreach (var location in found)
            {
                if (!_pinned.Any(value => string.Equals(value, location.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pinned.Add(location.Path);
                }
            }

            Populate();
        }

        private void RequestRebuild()
        {
            var confirm = MessageBox.Show(
                this,
                "Rebuilding discards Claude Usage's stored daily totals and recounts everything from the transcripts " +
                "that are still on disk.\r\n\r\nDays whose transcripts Claude Code has already deleted cannot be " +
                "recovered and will disappear from history. Claude's own files are not touched.\r\n\r\nRebuild anyway?",
                "Rebuild archive",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
            RebuildRequested = true;
            Commit();
        }

        private void Commit()
        {
            _settings.ReplaceSources(_pinned);
            _settings.AutoDetectSources = _autoDetect.Checked;
            _settings.MinimizeToTray = _minimizeToTray.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class WaitCursorScope : IDisposable
        {
            private readonly Form _form;
            private Cursor _previous;

            internal WaitCursorScope(Form form)
            {
                _form = form;
            }

            internal void Begin()
            {
                _previous = _form.Cursor;
                _form.Cursor = Cursors.WaitCursor;
                Application.DoEvents();
            }

            public void Dispose()
            {
                if (_previous != null) _form.Cursor = _previous;
            }
        }
    }
}
