using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    /// <summary>Configures the daily token threshold and the early warning level.</summary>
    internal sealed class AlertsDialog : Form
    {
        private readonly CheckBox _enabled;
        private readonly NumericUpDown _limit;
        private readonly NumericUpDown _warnPercent;
        private readonly RadioButton _processed;
        private readonly RadioButton _inputOutput;
        private readonly Label _preview;
        private readonly TableLayoutPanel _layout;

        internal AlertsDialog(AlertSettings current)
        {
            var settings = (current ?? new AlertSettings()).Clone();
            Result = settings;

            Text = "Daily usage alerts";
            // Sizable rather than fixed: the explanatory text reflows with the display's text scale,
            // so the window has to be able to follow it, and the user has to be able to override.
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = MainForm.Palette.Window;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 380);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 7,
                Padding = new Padding(16, 14, 16, 12),
                BackColor = MainForm.Palette.Window
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (var index = 0; index < layout.RowCount; index++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            _layout = layout;

            // The scrolling host is the safety net: if the content still cannot fit, every control
            // stays reachable instead of being clipped off the bottom edge.
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = MainForm.Palette.Window
            };
            scroll.Controls.Add(layout);
            Controls.Add(scroll);

            _enabled = new CheckBox
            {
                Text = "Warn me when today's usage approaches a daily threshold",
                AutoSize = true,
                Checked = settings.Enabled,
                Margin = new Padding(0, 0, 0, 10)
            };
            _enabled.CheckedChanged += (sender, args) => UpdateEnabledState();
            layout.SetColumnSpan(_enabled, 2);
            layout.Controls.Add(_enabled, 0, 0);

            layout.Controls.Add(FieldLabel("Daily threshold (tokens)"), 0, 1);
            _limit = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100000000000M,
                Increment = 1000000,
                ThousandsSeparator = true,
                Width = 190,
                Value = Clamp(settings.DailyLimitTokens),
                AccessibleName = "Daily token threshold"
            };
            _limit.ValueChanged += (sender, args) => UpdatePreview();
            layout.Controls.Add(_limit, 1, 1);

            layout.Controls.Add(FieldLabel("Warn at"), 0, 2);
            var warnPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            _warnPercent = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 100,
                Increment = 5,
                Width = 70,
                Value = Math.Max(1, Math.Min(100, settings.WarnPercent)),
                AccessibleName = "Warning percentage of the daily threshold"
            };
            _warnPercent.ValueChanged += (sender, args) => UpdatePreview();
            warnPanel.Controls.Add(_warnPercent);
            warnPanel.Controls.Add(new Label
            {
                Text = "% of the threshold",
                AutoSize = true,
                ForeColor = MainForm.Palette.Muted,
                Margin = new Padding(6, 6, 0, 0)
            });
            layout.Controls.Add(warnPanel, 1, 2);

            layout.Controls.Add(FieldLabel("Count"), 0, 3);
            var metricPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            _processed = new RadioButton
            {
                Text = "Processed tokens (input + output + cache read + cache creation)",
                AutoSize = true,
                Checked = settings.Metric == TokenMetric.Processed
            };
            _processed.CheckedChanged += (sender, args) => UpdatePreview();
            _inputOutput = new RadioButton
            {
                Text = "Input + output tokens only",
                AutoSize = true,
                Checked = settings.Metric == TokenMetric.InputOutput
            };
            metricPanel.Controls.Add(_processed);
            metricPanel.Controls.Add(_inputOutput);
            layout.Controls.Add(metricPanel, 1, 3);

            _preview = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                // Reserve room for the longest wording this label takes, so editing a value does
                // not make the dialog's content taller than the dialog.
                MinimumSize = new Size(500, 52),
                ForeColor = MainForm.Palette.Muted,
                BackColor = MainForm.Palette.InfoBackground,
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0, 12, 0, 0)
            };
            layout.SetColumnSpan(_preview, 2);
            layout.Controls.Add(_preview, 0, 4);

            layout.Controls.Add(new Label
            {
                Text = "Cache-read tokens dominate processed totals, so a threshold based on them is much larger " +
                       "than one based on input and output alone. Alerts appear in the notification area, so " +
                       "Claude Usage needs to stay running.",
                AutoSize = true,
                MaximumSize = new Size(500, 0),
                ForeColor = MainForm.Palette.Muted,
                Margin = new Padding(0, 10, 0, 0)
            }, 0, 5);
            layout.SetColumnSpan(layout.GetControlFromPosition(0, 5), 2);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = new Padding(0, 12, 0, 0)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.System
            };
            var ok = new Button { Text = "Save", AutoSize = true, FlatStyle = FlatStyle.System };
            ok.Click += (sender, args) => Commit();
            footer.Controls.Add(cancel);
            footer.Controls.Add(ok);
            layout.SetColumnSpan(footer, 2);
            layout.Controls.Add(footer, 0, 6);
            AcceptButton = ok;
            CancelButton = cancel;

            UpdateEnabledState();
        }

        /// <summary>The edited settings. Only meaningful when the dialog returns OK.</summary>
        internal AlertSettings Result { get; private set; }

        /// <summary>
        /// Sizes the dialog once its content has actually been laid out. The preview and guidance
        /// text are only filled in at the end of construction, and both reflow with the display's
        /// text scale, so any size chosen up front is a guess that clips the buttons when wrong.
        /// </summary>
        protected override void OnLoad(EventArgs args)
        {
            base.OnLoad(args);
            try
            {
                _layout.PerformLayout();
                var preferred = _layout.PreferredSize;
                var workingArea = Screen.FromControl(this).WorkingArea;
                var width = Math.Min(
                    Math.Max(preferred.Width, ClientSize.Width),
                    Math.Max(360, workingArea.Width - 80));
                // A few pixels of slack: landing exactly on the content height risks a scrollbar
                // appearing from a one-pixel rounding difference, which then steals width.
                var height = Math.Min(preferred.Height + 4, Math.Max(240, workingArea.Height - 120));
                if (height < preferred.Height + 4)
                {
                    width = Math.Min(width + SystemInformation.VerticalScrollBarWidth, workingArea.Width - 80);
                }

                ClientSize = new Size(width, height);
                // Never narrower than the content needs, which would force a pointless horizontal
                // scrollbar; shorter is fine because the host scrolls vertically.
                MinimumSize = new Size(
                    Width,
                    Math.Min(ClientSize.Height, 260) + (Height - ClientSize.Height));
                CenterToParent();
            }
            catch
            {
                // Never block the dialog on a measurement problem; the host panel still scrolls.
            }
        }

        private void UpdateEnabledState()
        {
            var on = _enabled.Checked;
            _limit.Enabled = on;
            _warnPercent.Enabled = on;
            _processed.Enabled = on;
            _inputOutput.Enabled = on;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            var limit = (long)_limit.Value;
            if (!_enabled.Checked)
            {
                _preview.Text = "Alerts are off. Today's usage is still shown on the Today tab.";
                return;
            }

            if (limit <= 0)
            {
                _preview.Text = "Set a threshold above zero to turn alerts on.";
                return;
            }

            var warnAt = (long)Math.Ceiling(limit * ((double)_warnPercent.Value / 100D));
            _preview.Text = "You will be warned once at " + warnAt.ToString("N0", CultureInfo.CurrentCulture) +
                            " " + MetricLabel() + ", and again at " + limit.ToString("N0", CultureInfo.CurrentCulture) +
                            ". Each level is announced at most once per day.";
        }

        private string MetricLabel()
        {
            return _inputOutput.Checked ? "input + output tokens" : "processed tokens";
        }

        private void Commit()
        {
            Result = new AlertSettings
            {
                Enabled = _enabled.Checked && _limit.Value > 0,
                DailyLimitTokens = (long)_limit.Value,
                WarnPercent = (int)_warnPercent.Value,
                Metric = _inputOutput.Checked ? TokenMetric.InputOutput : TokenMetric.Processed
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        private static decimal Clamp(long value)
        {
            if (value < 0) return 0;
            return value > 100000000000L ? 100000000000M : value;
        }

        private static Label FieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = MainForm.Palette.Text,
                Margin = new Padding(0, 6, 14, 8)
            };
        }
    }
}
