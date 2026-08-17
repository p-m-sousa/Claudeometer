using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    internal sealed class MainForm : Form
    {
        private const string AllModelsLabel = "All models";

        private readonly AppSettings _settings;
        private readonly UsageStore _store;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Dictionary<string, Label> _todayCards = new Dictionary<string, Label>(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> _rangeCards = new Dictionary<string, Label>(StringComparer.Ordinal);
        private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _watchDebounce = new System.Windows.Forms.Timer();
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

        private UsageHistory _history = UsageHistory.Empty;
        private ScanReport _report;
        private UsageAnalytics _rangeAnalytics;
        private IList<string> _roots = new List<string>();
        private int _archivedOnlyDays;
        private bool _refreshing;
        private bool _refreshQueued;
        private bool _updatingFilters;
        private bool _exiting;
        private Icon _appIcon;
        private NotifyIcon _tray;

        private Label _sourceLabel;
        private Label _stateBadge;
        private Label _todayHeading;
        private Label _todayNotice;
        private Label _todaySecondary;
        private Label _thresholdLabel;
        private ProgressBar _thresholdBar;
        private Button _thresholdButton;
        private Label _rangeNotice;
        private Label _modelsNotice;
        private Button _refreshButton;
        private ComboBox _refreshInterval;
        private DataGridView _todayModelsGrid;
        private DataGridView _dailyGrid;
        private DataGridView _modelsGrid;
        private ComboBox _rangePreset;
        private ComboBox _modelFilter;
        private DateTimePicker _fromDate;
        private DateTimePicker _toDate;
        private Chart _dailyChart;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripStatusLabel _freshnessLabel;

        internal MainForm(IEnumerable<string> commandLineSources)
        {
            _settings = new AppSettings();
            if (commandLineSources != null)
            {
                var provided = commandLineSources
                    .Select(ClaudeDataLocator.NormalizeChosenFolder)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                if (provided.Count > 0)
                {
                    _settings.ReplaceSources(provided);
                    _settings.AutoDetectSources = false;
                }
            }

            _store = new UsageStore(AppSettings.StorePath);
            InitializeWindow();
            BuildInterface();
            ConfigureTimers();
            ConfigureTray();

            Shown += async (sender, args) =>
            {
                await Task.Run(() => _store.Load(TimeZoneInfo.Local));
                SetupWatchers();
                await RefreshAsync(false);
            };
            Resize += OnResize;
            FormClosing += OnFormClosing;
        }

        private void InitializeWindow()
        {
            Text = "Claude Usage";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 400);
            var workArea = Screen.PrimaryScreen == null
                ? new Rectangle(0, 0, 1200, 760)
                : Screen.PrimaryScreen.WorkingArea;
            Size = new Size(
                Math.Min(1200, Math.Max(MinimumSize.Width, workArea.Width - 32)),
                Math.Min(780, Math.Max(MinimumSize.Height, workArea.Height - 32)));
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Palette.Window;
            ForeColor = Palette.Text;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;
            _appIcon = AppIcon.Create();
            if (_appIcon != null) Icon = _appIcon;
        }

        private void BuildInterface()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Palette.Window,
                Padding = new Padding(18, 14, 18, 0)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);

            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(18, 7),
                Margin = new Padding(0, 12, 0, 8),
                AccessibleName = "Usage views"
            };
            tabs.TabPages.Add(BuildTodayTab());
            tabs.TabPages.Add(BuildHistoryTab());
            tabs.TabPages.Add(BuildModelsTab());
            root.Controls.Add(tabs, 0, 1);

            var status = new StatusStrip
            {
                Dock = DockStyle.Fill,
                SizingGrip = false,
                BackColor = Palette.Window,
                Padding = new Padding(0, 4, 0, 4)
            };
            _statusLabel = new ToolStripStatusLabel("Starting…")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _freshnessLabel = new ToolStripStatusLabel("Local only - read-only")
            {
                TextAlign = ContentAlignment.MiddleRight
            };
            status.Items.Add(_statusLabel);
            status.Items.Add(_freshnessLabel);
            root.Controls.Add(status, 0, 2);
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 6,
                RowCount = 2,
                BackColor = Palette.Window,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (var index = 0; index < 4; index++) header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            header.Controls.Add(new Label
            {
                Text = "Claude Usage",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Palette.Heading,
                Margin = new Padding(0, 0, 16, 2)
            }, 0, 0);

            _stateBadge = new Label
            {
                Text = "STARTING",
                AutoSize = true,
                BackColor = Palette.NeutralBadge,
                ForeColor = Palette.Muted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8, 5, 8, 5),
                Margin = new Padding(0, 8, 0, 0)
            };
            header.Controls.Add(_stateBadge, 1, 0);

            _refreshButton = HeaderButton("Refresh now", "Rescan local Claude Code transcripts");
            _refreshButton.Click += async (sender, args) => await RefreshAsync(true);
            header.Controls.Add(_refreshButton, 2, 0);

            var exportButton = HeaderButton("Export PDF…", "Save the selected range as a PDF report");
            exportButton.Click += (sender, args) => ExportPdf();
            header.Controls.Add(exportButton, 3, 0);

            var sourcesButton = HeaderButton("Data sources…", "Choose where Claude Code data is read from");
            sourcesButton.Click += (sender, args) => EditSources();
            header.Controls.Add(sourcesButton, 4, 0);

            var alertsButton = HeaderButton("Alerts…", "Set a daily token threshold and warning level");
            alertsButton.Click += (sender, args) => EditAlerts();
            header.Controls.Add(alertsButton, 5, 0);

            _sourceLabel = new Label
            {
                Text = "Looking for Claude Code data…",
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 24,
                ForeColor = Palette.Muted,
                Margin = new Padding(1, 2, 14, 0),
                AccessibleName = "Claude data locations"
            };
            header.SetColumnSpan(_sourceLabel, 3);
            header.Controls.Add(_sourceLabel, 0, 1);

            var intervalPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(8, 0, 0, 0)
            };
            intervalPanel.Controls.Add(new Label
            {
                Text = "Auto-refresh",
                AutoSize = true,
                ForeColor = Palette.Muted,
                Margin = new Padding(0, 6, 6, 0)
            });
            _refreshInterval = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 105,
                AccessibleName = "Automatic refresh interval"
            };
            _refreshInterval.Items.AddRange(new object[]
            {
                new RefreshChoice("10 seconds", 10),
                new RefreshChoice("30 seconds", 30),
                new RefreshChoice("1 minute", 60),
                new RefreshChoice("5 minutes", 300),
                new RefreshChoice("Off", 0)
            });
            intervalPanel.Controls.Add(_refreshInterval);
            header.SetColumnSpan(intervalPanel, 3);
            header.Controls.Add(intervalPanel, 3, 1);
            return header;
        }

        private TabPage BuildTodayTab()
        {
            var tab = NewTab("Today");
            var content = NewScrollableStack();
            tab.Controls.Add(content);

            _todayHeading = SectionTitle("Today");
            content.Controls.Add(_todayHeading, 0, 0);
            _todayNotice = NoticeLabel(
                "Live from Claude Code's own session transcripts. Every figure is a locally recorded token count, not a bill and not your remaining plan allowance.");
            content.Controls.Add(_todayNotice, 0, 1);

            content.Controls.Add(BuildCardGrid(_todayCards), 0, 2);

            _todaySecondary = new Label
            {
                Text = "Responses 0   -   Messages 0   -   Tool calls 0   -   Sessions 0",
                AutoSize = true,
                ForeColor = Palette.Muted,
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(2, 2, 0, 10)
            };
            content.Controls.Add(_todaySecondary, 0, 3);
            content.Controls.Add(BuildThresholdPanel(), 0, 4);

            content.Controls.Add(SubsectionTitle("Today by model"), 0, 5);
            _todayModelsGrid = CreateGrid("Today's token categories by model");
            AddTextColumn(_todayModelsGrid, "Model", 240, DataGridViewAutoSizeColumnMode.Fill);
            AddNumberColumn(_todayModelsGrid, "Input");
            AddNumberColumn(_todayModelsGrid, "Output");
            AddNumberColumn(_todayModelsGrid, "Cache read");
            AddNumberColumn(_todayModelsGrid, "Cache creation");
            AddNumberColumn(_todayModelsGrid, "Processed");
            AddNumberColumn(_todayModelsGrid, "Responses");
            _todayModelsGrid.Height = 210;
            content.Controls.Add(_todayModelsGrid, 0, 6);
            return tab;
        }

        private TabPage BuildHistoryTab()
        {
            var tab = NewTab("History");
            var content = NewScrollableStack();
            tab.Controls.Add(content);

            content.Controls.Add(SectionTitle("Date range"), 0, 0);
            _rangeNotice = NoticeLabel(
                "Pick any range. Token, response, and tool-call figures follow the model filter; message and session counts are whole-day totals.");
            content.Controls.Add(_rangeNotice, 0, 1);
            content.Controls.Add(BuildFilters(), 0, 2);
            content.Controls.Add(BuildCardGrid(_rangeCards), 0, 3);

            _dailyChart = BuildChart();
            content.Controls.Add(_dailyChart, 0, 4);

            content.Controls.Add(SubsectionTitle("Daily values"), 0, 5);
            _dailyGrid = CreateGrid("Exact values by date");
            AddTextColumn(_dailyGrid, "Date", 105, DataGridViewAutoSizeColumnMode.None);
            AddNumberColumn(_dailyGrid, "Input");
            AddNumberColumn(_dailyGrid, "Output");
            AddNumberColumn(_dailyGrid, "Cache read");
            AddNumberColumn(_dailyGrid, "Cache creation");
            AddNumberColumn(_dailyGrid, "Processed");
            AddNumberColumn(_dailyGrid, "Responses");
            AddNumberColumn(_dailyGrid, "Tool calls");
            AddNumberColumn(_dailyGrid, "Sessions");
            _dailyGrid.Height = 280;
            content.Controls.Add(_dailyGrid, 0, 6);
            return tab;
        }

        private TabPage BuildModelsTab()
        {
            var tab = NewTab("Models");
            var content = NewScrollableStack();
            tab.Controls.Add(content);
            content.Controls.Add(SectionTitle("Totals by model"), 0, 0);
            _modelsNotice = NoticeLabel(
                "These totals use the date range and model filter from the History tab.");
            content.Controls.Add(_modelsNotice, 0, 1);

            _modelsGrid = CreateGrid("Token categories by model for the selected range");
            AddTextColumn(_modelsGrid, "Model", 240, DataGridViewAutoSizeColumnMode.Fill);
            AddNumberColumn(_modelsGrid, "Input");
            AddNumberColumn(_modelsGrid, "Output");
            AddNumberColumn(_modelsGrid, "Cache read");
            AddNumberColumn(_modelsGrid, "Cache creation");
            AddNumberColumn(_modelsGrid, "Processed");
            AddNumberColumn(_modelsGrid, "Responses");
            AddNumberColumn(_modelsGrid, "Tool calls");
            AddNumberColumn(_modelsGrid, "Web searches");
            _modelsGrid.Height = 430;
            _modelsGrid.Margin = new Padding(0, 14, 0, 0);
            content.Controls.Add(_modelsGrid, 0, 2);
            return tab;
        }

        private Control BuildCardGrid(IDictionary<string, Label> target)
        {
            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 2,
                Margin = new Padding(0, 14, 0, 8)
            };
            for (var index = 0; index < 3; index++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.Controls.Add(CreateCard("Processed tokens", "processed", target, Palette.Accent), 0, 0);
            cards.Controls.Add(CreateCard("Input", "input", target, Palette.Blue), 1, 0);
            cards.Controls.Add(CreateCard("Output", "output", target, Palette.Green), 2, 0);
            cards.Controls.Add(CreateCard("Cache read", "cacheRead", target, Palette.Purple), 0, 1);
            cards.Controls.Add(CreateCard("Cache creation", "cacheCreate", target, Palette.Orange), 1, 1);
            cards.Controls.Add(CreateCard("Input + output", "io", target, Palette.Teal), 2, 1);
            return cards;
        }

        private Control BuildThresholdPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Palette.Card,
                Padding = new Padding(12, 10, 12, 12),
                Margin = new Padding(0, 0, 0, 6)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _thresholdLabel = new Label
            {
                Text = "No daily threshold is set.",
                AutoSize = true,
                ForeColor = Palette.Text,
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(2, 2, 0, 6)
            };
            panel.Controls.Add(_thresholdLabel, 0, 0);

            _thresholdButton = new Button
            {
                Text = "Set threshold…",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(10, 0, 0, 0)
            };
            _thresholdButton.Click += (sender, args) => EditAlerts();
            panel.Controls.Add(_thresholdButton, 1, 0);

            _thresholdBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Height = 14,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Visible = false,
                AccessibleName = "Share of the daily threshold used today"
            };
            panel.SetColumnSpan(_thresholdBar, 2);
            panel.Controls.Add(_thresholdBar, 0, 1);
            return panel;
        }

        private Control BuildFilters()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Palette.Card,
                Padding = new Padding(12, 10, 12, 10),
                Margin = new Padding(0, 12, 0, 0)
            };

            panel.Controls.Add(FilterLabel("Range"));
            _rangePreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 130,
                AccessibleName = "Date range preset"
            };
            _rangePreset.Items.AddRange(new object[]
            {
                "Today", "Yesterday", "Last 7 days", "Last 30 days", "Last 90 days",
                "This month", "Last month", "All time", "Custom"
            });
            _rangePreset.SelectedIndex = 3;
            _rangePreset.SelectedIndexChanged += (sender, args) =>
            {
                if (_updatingFilters) return;
                ApplyPresetDates();
                RenderRange();
            };
            panel.Controls.Add(_rangePreset);

            panel.Controls.Add(FilterLabel("From"));
            _fromDate = DatePicker("Range start date");
            _fromDate.ValueChanged += (sender, args) => OnCustomDateChanged();
            panel.Controls.Add(_fromDate);
            panel.Controls.Add(FilterLabel("To"));
            _toDate = DatePicker("Range end date");
            _toDate.ValueChanged += (sender, args) => OnCustomDateChanged();
            panel.Controls.Add(_toDate);

            panel.Controls.Add(FilterLabel("Model"));
            _modelFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 250,
                AccessibleName = "Model filter"
            };
            _modelFilter.Items.Add(AllModelsLabel);
            _modelFilter.SelectedIndex = 0;
            _modelFilter.SelectedIndexChanged += (sender, args) =>
            {
                if (!_updatingFilters) RenderRange();
            };
            panel.Controls.Add(_modelFilter);

            var reset = new Button { Text = "Reset", AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
            reset.Click += (sender, args) =>
            {
                _updatingFilters = true;
                _rangePreset.SelectedIndex = 3;
                _modelFilter.SelectedIndex = 0;
                _updatingFilters = false;
                ApplyPresetDates();
                RenderRange();
            };
            panel.Controls.Add(reset);
            return panel;
        }

        private void ConfigureTimers()
        {
            _refreshTimer.Tick += async (sender, args) => await RefreshAsync(false);
            _watchDebounce.Interval = 1200;
            _watchDebounce.Tick += async (sender, args) =>
            {
                _watchDebounce.Stop();
                await RefreshAsync(false);
            };

            for (var index = 0; index < _refreshInterval.Items.Count; index++)
            {
                if (((RefreshChoice)_refreshInterval.Items[index]).Seconds == _settings.RefreshSeconds)
                {
                    _refreshInterval.SelectedIndex = index;
                    break;
                }
            }

            if (_refreshInterval.SelectedIndex < 0) _refreshInterval.SelectedIndex = 1;
            _refreshInterval.SelectedIndexChanged += (sender, args) => ApplyRefreshInterval(true);
            ApplyRefreshInterval(false);
        }

        private void ConfigureTray()
        {
            _tray = new NotifyIcon
            {
                Text = "Claude Usage",
                Visible = false
            };
            if (_appIcon != null) _tray.Icon = _appIcon;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show Claude Usage", null, (sender, args) => RestoreFromTray());
            menu.Items.Add("Refresh now", null, async (sender, args) => await RefreshAsync(true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (sender, args) =>
            {
                _exiting = true;
                Close();
            });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (sender, args) => RestoreFromTray();
            _tray.BalloonTipClicked += (sender, args) => RestoreFromTray();
        }

        private void ApplyRefreshInterval(bool persist)
        {
            var choice = _refreshInterval.SelectedItem as RefreshChoice;
            var seconds = choice == null ? 30 : choice.Seconds;
            _refreshTimer.Stop();
            if (seconds > 0)
            {
                _refreshTimer.Interval = seconds * 1000;
                _refreshTimer.Start();
            }

            if (!persist) return;
            _settings.RefreshSeconds = seconds;
            _settings.Save();
        }

        private IList<string> ResolveRoots()
        {
            var roots = new List<string>();
            foreach (var pinned in _settings.Sources)
            {
                if (!roots.Any(value => string.Equals(value, pinned, StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(pinned);
                }
            }

            if (_settings.AutoDetectSources)
            {
                foreach (var discovered in ClaudeDataLocator.Discover())
                {
                    if (!roots.Any(value => string.Equals(value, discovered.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        roots.Add(discovered.Path);
                    }
                }
            }

            return roots;
        }

        private async Task RefreshAsync(bool userInitiated)
        {
            if (_refreshing)
            {
                _refreshQueued = true;
                return;
            }

            _refreshing = true;
            _refreshButton.Enabled = false;
            SetBadge("SCANNING", false);
            _statusLabel.Text = "Reading local Claude Code transcripts…";

            try
            {
                _roots = ResolveRoots();
                UpdateSourceLabel();
                if (_roots.Count == 0)
                {
                    ShowNoDataFound();
                    return;
                }

                var roots = _roots.ToList();
                var progress = new Progress<TranscriptScanProgress>(OnScanProgress);
                var result = await Task.Run(() => ScanWithRetry(roots, progress));

                _history = result.History;
                _report = result.Report;
                _archivedOnlyDays = result.ArchivedOnlyDays;

                UpdateModelFilter();
                RenderToday();
                ApplyPresetDates();
                RenderRange();
                UpdateStatus(result);
                EvaluateAlerts();
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
            catch (Exception error)
            {
                SetBadge("NEEDS ATTENTION", true);
                _statusLabel.Text = error.Message;
                _todayNotice.Text = "Claude Code data could not be read. " + error.Message +
                                    " No Claude files were changed. Try Refresh now, or pick a folder under Data sources.";
                if (userInitiated)
                {
                    MessageBox.Show(
                        this,
                        error.Message + "\r\n\r\nNo Claude files were changed.",
                        "Could not read Claude Code data",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _refreshing = false;
                _refreshButton.Enabled = true;
                if (_refreshQueued && !IsDisposed)
                {
                    _refreshQueued = false;
                    BeginInvoke(new Action(async () => await RefreshAsync(false)));
                }
            }
        }

        /// <summary>
        /// A transcript being written mid-scan produces a transient warning. Retrying briefly
        /// usually catches a settled file; the archive keeps the higher figure either way.
        /// </summary>
        private UsageRefreshResult ScanWithRetry(IList<string> roots, IProgress<TranscriptScanProgress> progress)
        {
            UsageRefreshResult result = null;
            var delays = new[] { 0, 150, 400 };
            foreach (var delay in delays)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (delay > 0) Thread.Sleep(delay);
                result = _store.Refresh(roots, TimeZoneInfo.Local, _lifetime.Token, progress);
                if (result.Report.IsComplete) break;
            }

            _store.Save();
            return result;
        }

        private void OnScanProgress(TranscriptScanProgress progress)
        {
            if (IsDisposed || progress.TotalFiles == 0) return;
            if (progress.FilesCompleted >= progress.TotalFiles) return;
            _statusLabel.Text = "Reading transcripts… " +
                progress.FilesCompleted.ToString("N0", CultureInfo.CurrentCulture) + " of " +
                progress.TotalFiles.ToString("N0", CultureInfo.CurrentCulture);
        }

        private void ShowNoDataFound()
        {
            SetBadge("NO DATA FOUND", true);
            var distributions = ClaudeDataLocator.FindWslDistributions();
            var hint = distributions.Count > 0
                ? " WSL was detected (" + string.Join(", ", distributions.ToArray()) +
                  "); if you run Claude Code there, add it from Data sources."
                : string.Empty;
            _statusLabel.Text = "No Claude Code data folder was found.";
            _sourceLabel.Text = "No Claude Code data folder found";
            _todayNotice.Text = "No Claude Code data folder was found automatically. " +
                "Claude Usage looks in %USERPROFILE%\\.claude, %USERPROFILE%\\.config\\claude, and any folder named by " +
                "CLAUDE_CONFIG_DIR, HOME, or HOMEDRIVE/HOMEPATH. Use Data sources to pick the folder that contains a " +
                "\"projects\" subfolder." + hint;
            _rangeNotice.Text = _todayNotice.Text;
        }

        private void UpdateSourceLabel()
        {
            if (_roots.Count == 0)
            {
                _sourceLabel.Text = "No Claude Code data folder found";
                return;
            }

            var text = _roots[0];
            if (_roots.Count > 1) text += "   (+" + (_roots.Count - 1) + " more)";
            var missing = _roots.Where(root => !ClaudeDataLocator.HasProjects(root)).ToList();
            if (missing.Count > 0) text += "   -   " + missing.Count + " unavailable";
            _sourceLabel.Text = text;
            _toolTip.SetToolTip(_sourceLabel, string.Join("\r\n", _roots.ToArray()));
            _sourceLabel.AccessibleDescription = string.Join("; ", _roots.ToArray());
        }

        private void UpdateStatus(UsageRefreshResult result)
        {
            var warnings = result.Report.Warnings.Count;
            var important = result.Report.Warnings.FirstOrDefault(value => value.Severity != WarningSeverity.Information);
            SetBadge(important == null ? "LIVE" : "LIVE - WARNING", important != null);

            var parts = new List<string>
            {
                result.Report.FilesSeen.ToString("N0", CultureInfo.CurrentCulture) + " transcript(s)",
                result.Report.FilesParsed.ToString("N0", CultureInfo.CurrentCulture) + " read this pass",
                _history.Days.Count.ToString("N0", CultureInfo.CurrentCulture) + " day(s) recorded"
            };
            if (_archivedOnlyDays > 0)
            {
                parts.Add(_archivedOnlyDays.ToString("N0", CultureInfo.CurrentCulture) + " day(s) from archive");
            }

            if (warnings > 0) parts.Add(warnings.ToString("N0", CultureInfo.CurrentCulture) + " warning(s)");
            if (important != null) parts.Add(important.Message);
            _statusLabel.Text = string.Join(" - ", parts.ToArray());

            var span = _history.FirstDate == null
                ? "no history yet"
                : _history.FirstDate + " to " + _history.LastDate;
            _freshnessLabel.Text = "Refreshed " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture) +
                                   " - history " + span;

            if (_store.TimeZoneChanged)
            {
                _rangeNotice.Text = "This computer's time zone changed since the archive was created. Days recorded " +
                                    "earlier keep their original local-day boundaries; current transcripts were re-read " +
                                    "using the new one.";
            }
        }

        private void RenderToday()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = _history.FindDay(today);
            var tokens = day == null ? TokenTotals.Zero : day.Tokens;

            _todayHeading.Text = "Today - " + DateTime.Now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
            SetCard(_todayCards, "processed", tokens.ProcessedTokens);
            SetCard(_todayCards, "input", tokens.InputTokens);
            SetCard(_todayCards, "output", tokens.OutputTokens);
            SetCard(_todayCards, "cacheRead", tokens.CacheReadTokens);
            SetCard(_todayCards, "cacheCreate", tokens.CacheCreationTokens);
            SetCard(_todayCards, "io", tokens.InputOutputTokens);

            _todaySecondary.Text = "Responses " + Exact(day == null ? 0 : day.ResponseCount) +
                                   "   -   Messages " + Exact(day == null ? 0 : day.MessageCount) +
                                   "   -   Tool calls " + Exact(day == null ? 0 : day.ToolCallCount) +
                                   "   -   Sessions " + Exact(day == null ? 0 : day.SessionCount);

            _todayNotice.Text = day == null
                ? "No Claude Code activity has been recorded today. This view refreshes on its own and never leaves this computer."
                : "Live from Claude Code's session transcripts. Processed = input + output + cache read + cache creation. " +
                  "Days are local calendar days. These are locally recorded token counts, not a bill or your remaining plan allowance.";

            _todayModelsGrid.Rows.Clear();
            if (day != null)
            {
                foreach (var model in day.Models.Values
                    .OrderByDescending(value => value.Tokens.ProcessedTokens)
                    .ThenBy(value => value.ModelId, StringComparer.Ordinal))
                {
                    _todayModelsGrid.Rows.Add(
                        model.ModelId,
                        model.Tokens.InputTokens,
                        model.Tokens.OutputTokens,
                        model.Tokens.CacheReadTokens,
                        model.Tokens.CacheCreationTokens,
                        model.Tokens.ProcessedTokens,
                        model.ResponseCount);
                }
            }

            RenderThreshold(tokens);
        }

        private void RenderThreshold(TokenTotals todayTokens)
        {
            var alerts = _settings.Alerts;
            if (!alerts.IsActive)
            {
                _thresholdLabel.Text = "No daily threshold is set. Set one to be warned before a heavy day gets away from you.";
                _thresholdLabel.ForeColor = Palette.Muted;
                _thresholdBar.Visible = false;
                _thresholdButton.Text = "Set threshold…";
                return;
            }

            var evaluation = UsageAlertEvaluator.Evaluate(
                alerts,
                DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                todayTokens.Select(alerts.Metric),
                _settings.NotifiedAlert);

            _thresholdButton.Text = "Change threshold…";
            _thresholdBar.Visible = true;
            _thresholdBar.Value = Math.Max(0, Math.Min(100, evaluation.Percent));
            _thresholdLabel.Text = evaluation.Message +
                                   "   Warning at " + alerts.EffectiveWarnPercent.ToString(CultureInfo.CurrentCulture) + "%.";
            _thresholdLabel.ForeColor = evaluation.Level == AlertLevel.Limit
                ? Palette.DangerText
                : (evaluation.Level == AlertLevel.Warning ? Palette.WarningText : Palette.Text);
        }

        private void EvaluateAlerts()
        {
            var alerts = _settings.Alerts;
            if (!alerts.IsActive) return;

            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = _history.FindDay(today);
            var tokens = day == null ? 0 : day.Tokens.Select(alerts.Metric);
            var evaluation = UsageAlertEvaluator.Evaluate(alerts, today, tokens, _settings.NotifiedAlert);
            if (!evaluation.ShouldNotify) return;

            _settings.RecordNotifiedAlert(today, evaluation.Level);
            _settings.Save();

            if (_tray != null)
            {
                var wasVisible = _tray.Visible;
                _tray.Visible = true;
                _tray.ShowBalloonTip(
                    15000,
                    evaluation.Title,
                    evaluation.Message,
                    evaluation.Level == AlertLevel.Limit ? ToolTipIcon.Warning : ToolTipIcon.Info);
                if (!wasVisible && WindowState != FormWindowState.Minimized)
                {
                    // Keep the icon around briefly so the balloon is not orphaned.
                    var hide = new System.Windows.Forms.Timer { Interval = 20000 };
                    hide.Tick += (sender, args) =>
                    {
                        hide.Stop();
                        hide.Dispose();
                        if (_tray != null && WindowState != FormWindowState.Minimized) _tray.Visible = false;
                    };
                    hide.Start();
                }
            }

            _statusLabel.Text = evaluation.Title + " - " + evaluation.Message;
        }

        private void UpdateModelFilter()
        {
            var previous = _modelFilter.SelectedItem as string;
            _updatingFilters = true;
            _modelFilter.Items.Clear();
            _modelFilter.Items.Add(AllModelsLabel);
            foreach (var model in _history.ModelIds) _modelFilter.Items.Add(model);
            var index = previous == null ? -1 : _modelFilter.Items.IndexOf(previous);
            _modelFilter.SelectedIndex = index >= 0 ? index : 0;
            _updatingFilters = false;
        }

        private void ApplyPresetDates()
        {
            if (_rangePreset == null || _fromDate == null || _toDate == null) return;
            var preset = _rangePreset.SelectedItem as string ?? "Last 30 days";
            var today = DateTime.Now.Date;
            _updatingFilters = true;
            var custom = preset == "Custom";
            _fromDate.Enabled = custom;
            _toDate.Enabled = custom;
            if (!custom)
            {
                var from = today;
                var to = today;
                switch (preset)
                {
                    case "Today":
                        break;
                    case "Yesterday":
                        from = today.AddDays(-1);
                        to = from;
                        break;
                    case "Last 7 days":
                        from = today.AddDays(-6);
                        break;
                    case "Last 30 days":
                        from = today.AddDays(-29);
                        break;
                    case "Last 90 days":
                        from = today.AddDays(-89);
                        break;
                    case "This month":
                        from = new DateTime(today.Year, today.Month, 1);
                        break;
                    case "Last month":
                        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
                        from = firstOfThisMonth.AddMonths(-1);
                        to = firstOfThisMonth.AddDays(-1);
                        break;
                    default:
                        from = EarliestRecordedDate() ?? today;
                        break;
                }

                _fromDate.Value = ClampToPickerRange(from);
                _toDate.Value = ClampToPickerRange(to);
            }

            _updatingFilters = false;
        }

        private static DateTime ClampToPickerRange(DateTime value)
        {
            if (value < DateTimePicker.MinimumDateTime) return DateTimePicker.MinimumDateTime;
            if (value > DateTimePicker.MaximumDateTime) return DateTimePicker.MaximumDateTime;
            return value;
        }

        private DateTime? EarliestRecordedDate()
        {
            DateTime parsed;
            return _history.FirstDate != null && DateTime.TryParseExact(
                _history.FirstDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed)
                ? parsed
                : (DateTime?)null;
        }

        private void OnCustomDateChanged()
        {
            if (_updatingFilters) return;
            if ((_rangePreset.SelectedItem as string) != "Custom")
            {
                _updatingFilters = true;
                _rangePreset.SelectedItem = "Custom";
                _fromDate.Enabled = true;
                _toDate.Enabled = true;
                _updatingFilters = false;
            }

            RenderRange();
        }

        private void RenderRange()
        {
            if (_rangePreset == null) return;
            try
            {
                var filter = CurrentFilter();
                _rangeAnalytics = UsageAnalyticsCalculator.Calculate(_history, filter);
                var analytics = _rangeAnalytics;

                SetCard(_rangeCards, "processed", analytics.Tokens.ProcessedTokens);
                SetCard(_rangeCards, "input", analytics.Tokens.InputTokens);
                SetCard(_rangeCards, "output", analytics.Tokens.OutputTokens);
                SetCard(_rangeCards, "cacheRead", analytics.Tokens.CacheReadTokens);
                SetCard(_rangeCards, "cacheCreate", analytics.Tokens.CacheCreationTokens);
                SetCard(_rangeCards, "io", analytics.Tokens.InputOutputTokens);

                RenderChart(analytics);

                _dailyGrid.Rows.Clear();
                foreach (var day in analytics.Days.OrderByDescending(value => value.Date, StringComparer.Ordinal))
                {
                    _dailyGrid.Rows.Add(
                        day.Date,
                        day.Tokens.InputTokens,
                        day.Tokens.OutputTokens,
                        day.Tokens.CacheReadTokens,
                        day.Tokens.CacheCreationTokens,
                        day.Tokens.ProcessedTokens,
                        day.ResponseCount,
                        day.ToolCallCount,
                        day.SessionCount);
                }

                _modelsGrid.Rows.Clear();
                foreach (var model in analytics.Models)
                {
                    _modelsGrid.Rows.Add(
                        model.ModelId,
                        model.Tokens.InputTokens,
                        model.Tokens.OutputTokens,
                        model.Tokens.CacheReadTokens,
                        model.Tokens.CacheCreationTokens,
                        model.Tokens.ProcessedTokens,
                        model.ResponseCount,
                        model.ToolCallCount,
                        model.WebSearchRequests);
                }

                _rangeNotice.Text = RangeNoticeText(analytics);
                _modelsNotice.Text = "Range " + RangeLabel() + " - " + ModelLabel() +
                                     ". Message and session counts are not shown here because a session can span models.";
            }
            catch (ArgumentException error)
            {
                _rangeNotice.Text = error.Message;
            }
        }

        private string RangeNoticeText(UsageAnalytics analytics)
        {
            if (_history.Days.Count == 0)
            {
                return "No usage has been recorded yet. Claude Usage builds history from Claude Code's session " +
                       "transcripts as you work, and keeps its own daily archive so totals survive Claude Code's " +
                       "transcript cleanup.";
            }

            if (analytics.IsEmpty)
            {
                return "Nothing matches this date and model selection. Recorded history covers " +
                       _history.FirstDate + " to " + _history.LastDate + ".";
            }

            var text = "Processed tokens include input, output, cache read, and cache creation. ";
            if (analytics.ActivityIsWholeDay)
            {
                text += "Tokens, responses, and tool calls are filtered to " + ModelLabel() +
                        "; message and session counts remain whole-day totals. ";
            }

            if (_archivedOnlyDays > 0)
            {
                text += _archivedOnlyDays.ToString("N0", CultureInfo.CurrentCulture) +
                        " earlier day(s) come from Claude Usage's own archive, because Claude Code has since deleted " +
                        "those transcripts. ";
            }

            return text + "None of these figures is a bill or your remaining plan allowance.";
        }

        private UsageFilter CurrentFilter()
        {
            string from = null;
            string to = null;
            if ((_rangePreset.SelectedItem as string) != "All time")
            {
                from = _fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                to = _toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(from, to) > 0)
                {
                    throw new ArgumentException("The From date must be on or before the To date.");
                }
            }

            var selected = _modelFilter.SelectedItem as string;
            var models = selected == null || selected == AllModelsLabel ? null : new[] { selected };
            return new UsageFilter(from, to, models);
        }

        private string RangeLabel()
        {
            var preset = _rangePreset.SelectedItem as string ?? "Custom";
            if (preset == "All time")
            {
                return _history.FirstDate == null
                    ? "All recorded dates"
                    : "All recorded dates (" + _history.FirstDate + " to " + _history.LastDate + ")";
            }

            var from = _fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = _toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return from == to ? from + " (" + preset + ")" : from + " to " + to + " (" + preset + ")";
        }

        private string ModelLabel()
        {
            var selected = _modelFilter.SelectedItem as string;
            return string.IsNullOrEmpty(selected) ? AllModelsLabel : selected;
        }

        private void RenderChart(UsageAnalytics analytics)
        {
            var series = _dailyChart.Series[0];
            series.Points.Clear();
            var byDate = analytics.Days.ToDictionary(value => value.Date, StringComparer.Ordinal);
            var min = analytics.FromDate ?? (analytics.Days.Count == 0 ? null : analytics.Days[0].Date);
            var max = analytics.ToDate ??
                      (analytics.Days.Count == 0 ? null : analytics.Days[analytics.Days.Count - 1].Date);
            if (min == null || max == null) return;

            DateTime cursor;
            DateTime last;
            if (!DateTime.TryParseExact(min, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out cursor) ||
                !DateTime.TryParseExact(max, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last))
            {
                return;
            }

            var remaining = 3660;
            while (cursor <= last && remaining-- > 0)
            {
                var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DailyUsage day;
                byDate.TryGetValue(key, out day);
                var value = day == null ? 0L : day.Tokens.ProcessedTokens;
                var point = series.Points.Add(value);
                point.AxisLabel = key;
                point.ToolTip = key + ": " + Exact(value) + " processed tokens";
                if (_settings.Alerts.IsActive &&
                    _settings.Alerts.Metric == TokenMetric.Processed &&
                    value >= _settings.Alerts.DailyLimitTokens)
                {
                    point.Color = Palette.Orange;
                }

                cursor = cursor.AddDays(1);
            }

            var area = _dailyChart.ChartAreas[0];
            area.AxisX.Interval = Math.Max(1, Math.Ceiling(series.Points.Count / 12D));
            area.RecalculateAxesScale();
            _dailyChart.AccessibleDescription =
                "Processed tokens per day. Exact values are in the daily values table below.";
        }

        private void ExportPdf()
        {
            if (_rangeAnalytics == null)
            {
                MessageBox.Show(
                    this,
                    "There is nothing to export yet. Wait for the first scan to finish.",
                    "Export PDF",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var suggested = "claude-usage-" +
                (_rangeAnalytics.FromDate ?? _history.FirstDate ?? "all") + "-to-" +
                (_rangeAnalytics.ToDate ?? _history.LastDate ?? "now") + ".pdf";
            foreach (var invalid in Path.GetInvalidFileNameChars()) suggested = suggested.Replace(invalid, '-');

            using (var dialog = new SaveFileDialog
            {
                Title = "Save usage report",
                Filter = "PDF report (*.pdf)|*.pdf",
                FileName = suggested,
                DefaultExt = "pdf",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = Directory.Exists(_settings.ExportFolder)
                    ? _settings.ExportFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var options = new UsageReportOptions
                    {
                        Title = "Claude Code Usage Report",
                        RangeLabel = RangeLabel(),
                        ModelLabel = ModelLabel(),
                        TimeZoneLabel = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now)
                            ? TimeZoneInfo.Local.DaylightName
                            : TimeZoneInfo.Local.StandardName,
                        DataLocations = _roots.ToList(),
                        Metric = _settings.Alerts.Metric,
                        DailyThresholdTokens = _settings.Alerts.IsActive ? _settings.Alerts.DailyLimitTokens : 0,
                        ArchivedOnlyDays = _archivedOnlyDays
                    };
                    UsageReportWriter.Write(dialog.FileName, _rangeAnalytics, options);
                    _settings.ExportFolder = Path.GetDirectoryName(dialog.FileName);
                    _settings.Save();

                    var open = MessageBox.Show(
                        this,
                        "Saved " + Path.GetFileName(dialog.FileName) + ".\r\n\r\nOpen it now?",
                        "Export PDF",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);
                    if (open == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName)
                        {
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception error)
                {
                    MessageBox.Show(
                        this,
                        "The report could not be saved. " + error.Message,
                        "Export PDF",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void EditSources()
        {
            using (var dialog = new SourcesDialog(_settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _settings.Save();
                if (dialog.RebuildRequested)
                {
                    _store.Clear();
                    _history = UsageHistory.Empty;
                }

                SetupWatchers();
                BeginInvoke(new Action(async () => await RefreshAsync(true)));
            }
        }

        private void EditAlerts()
        {
            using (var dialog = new AlertsDialog(_settings.Alerts))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _settings.ReplaceAlerts(dialog.Result);
                _settings.RecordNotifiedAlert(null, AlertLevel.None);
                _settings.Save();
                RenderToday();
                RenderRange();
                EvaluateAlerts();
            }
        }

        private void SetupWatchers()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // Already gone.
                }
            }

            _watchers.Clear();
            foreach (var root in ResolveRoots())
            {
                try
                {
                    var projects = Path.Combine(root, "projects");
                    if (!Directory.Exists(projects)) continue;
                    var watcher = new FileSystemWatcher(projects, "*.jsonl")
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 32768,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += OnTranscriptChanged;
                    watcher.Created += OnTranscriptChanged;
                    watcher.Deleted += OnTranscriptChanged;
                    watcher.Renamed += OnTranscriptChanged;
                    watcher.Error += OnWatcherError;
                    _watchers.Add(watcher);
                }
                catch
                {
                    // Network and WSL paths may not support watching; the interval timer covers them.
                }
            }
        }

        private void OnTranscriptChanged(object sender, FileSystemEventArgs args)
        {
            ScheduleWatchRefresh(false);
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            ScheduleWatchRefresh(true);
        }

        private void ScheduleWatchRefresh(bool recreateWatchers)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (recreateWatchers) SetupWatchers();
                    _watchDebounce.Stop();
                    _watchDebounce.Start();
                }));
            }
            catch
            {
                // The window is closing.
            }
        }

        private void OnResize(object sender, EventArgs args)
        {
            // Only take over the taskbar button when there is actually a threshold to watch;
            // otherwise minimising should behave like any other window.
            if (!_settings.MinimizeToTray || !_settings.Alerts.IsActive || _tray == null) return;
            if (WindowState == FormWindowState.Minimized)
            {
                _tray.Visible = true;
                Hide();
            }
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (_tray != null) _tray.Visible = false;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            if (!_exiting && args.CloseReason == CloseReason.UserClosing && _settings.Alerts.IsActive &&
                _settings.MinimizeToTray && _tray != null && !_tray.Visible)
            {
                // A threshold can only be watched while the app runs, so closing the window keeps
                // it in the notification area instead of stopping the watch silently.
                args.Cancel = true;
                _tray.Visible = true;
                WindowState = FormWindowState.Minimized;
                Hide();
                _tray.ShowBalloonTip(
                    4000,
                    "Claude Usage is still watching",
                    "Your daily threshold is still being monitored. Right-click this icon to exit.",
                    ToolTipIcon.Info);
                return;
            }

            _refreshTimer.Stop();
            _watchDebounce.Stop();
            _lifetime.Cancel();
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // Already gone.
                }
            }

            _watchers.Clear();
            if (!_refreshing) _store.Save();
            _settings.Save();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            if (_appIcon != null)
            {
                AppIcon.Release(_appIcon);
                _appIcon = null;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5)
            {
                BeginInvoke(new Action(async () => await RefreshAsync(true)));
                return true;
            }

            if (keyData == (Keys.Control | Keys.E))
            {
                ExportPdf();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void SetBadge(string text, bool warning)
        {
            _stateBadge.Text = text;
            _stateBadge.BackColor = warning ? Palette.WarningBadge : Palette.SuccessBadge;
            _stateBadge.ForeColor = warning ? Palette.WarningText : Palette.SuccessText;
        }

        private Button HeaderButton(string text, string description)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(8, 3, 0, 0),
                AccessibleDescription = description
            };
        }

        private static TabPage NewTab(string text)
        {
            return new TabPage(text) { BackColor = Palette.Window, Padding = new Padding(14) };
        }

        private static TableLayoutPanel NewScrollableStack()
        {
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 1,
                RowCount = 10,
                BackColor = Palette.Window,
                Padding = new Padding(2)
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (var index = 0; index < stack.RowCount; index++)
            {
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            return stack;
        }

        private static Label SectionTitle(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Palette.Heading,
                Margin = new Padding(0, 3, 0, 5)
            };
        }

        private static Label SubsectionTitle(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Palette.Heading,
                Margin = new Padding(0, 9, 0, 7)
            };
        }

        private static Label NoticeLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(1080, 0),
                ForeColor = Palette.Muted,
                BackColor = Palette.InfoBackground,
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0, 3, 0, 0)
            };
        }

        private static Control CreateCard(string title, string key, IDictionary<string, Label> values, Color accent)
        {
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = Palette.Card,
                Padding = new Padding(13, 10, 13, 10),
                Margin = new Padding(0, 0, 9, 9),
                AccessibleName = title
            };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(
                new Panel { Height = 3, Dock = DockStyle.Fill, BackColor = accent, Margin = new Padding(0) },
                0,
                0);
            card.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Palette.Muted,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 8, 0, 1)
            }, 0, 1);
            var value = new Label
            {
                Text = "0",
                AutoSize = true,
                ForeColor = Palette.Heading,
                Font = new Font("Segoe UI Semibold", 16.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 1, 0, 0),
                AccessibleName = title + " value"
            };
            card.Controls.Add(value, 0, 2);
            values[key] = value;
            return card;
        }

        private static DataGridView CreateGrid(string accessibleName)
        {
            return new DataGridView
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = Palette.Card,
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = Palette.Border,
                EnableHeadersVisualStyles = SystemInformation.HighContrast,
                ColumnHeadersHeight = 36,
                RowTemplate = { Height = 30 },
                AccessibleName = accessibleName,
                Margin = new Padding(0, 0, 0, 8)
            };
        }

        private static void AddTextColumn(DataGridView grid, string title, int width, DataGridViewAutoSizeColumnMode mode)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = title,
                Width = width,
                AutoSizeMode = mode,
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        private static void AddNumberColumn(DataGridView grid, string title)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = title,
                Width = 112,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N0"
                },
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        private static Chart BuildChart()
        {
            var chart = new Chart
            {
                Dock = DockStyle.Top,
                Height = 270,
                BackColor = Palette.Card,
                BorderlineColor = Palette.Border,
                BorderlineDashStyle = ChartDashStyle.Solid,
                Margin = new Padding(0, 4, 0, 10),
                AccessibleName = "Processed tokens per day",
                Palette = ChartColorPalette.None
            };
            var area = new ChartArea("Usage") { BackColor = Palette.Card };
            area.AxisX.MajorGrid.Enabled = false;
            area.AxisX.LabelStyle.Angle = -45;
            area.AxisX.LabelStyle.ForeColor = Palette.Muted;
            area.AxisY.LabelStyle.ForeColor = Palette.Muted;
            area.AxisY.MajorGrid.LineColor = Palette.Border;
            area.AxisY.Title = "Processed tokens";
            area.AxisY.TitleForeColor = Palette.Muted;
            chart.ChartAreas.Add(area);
            chart.Series.Add(new Series("Processed tokens")
            {
                ChartType = SeriesChartType.Column,
                Color = Palette.Teal,
                XValueType = ChartValueType.String,
                YValueType = ChartValueType.Int64
            });
            return chart;
        }

        private static Label FilterLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Palette.Muted,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(9, 6, 5, 0)
            };
        }

        private static DateTimePicker DatePicker(string accessibleName)
        {
            return new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Width = 112,
                AccessibleName = accessibleName
            };
        }

        private void SetCard(IDictionary<string, Label> values, string key, long value)
        {
            Label label;
            if (!values.TryGetValue(key, out label)) return;
            label.Text = Compact(value);
            label.AccessibleDescription = Exact(value);
            _toolTip.SetToolTip(label, Exact(value));
        }

        private static string Compact(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000D).ToString("0.##", CultureInfo.CurrentCulture) + "B";
            if (value >= 1000000L) return (value / 1000000D).ToString("0.##", CultureInfo.CurrentCulture) + "M";
            if (value >= 1000L) return (value / 1000D).ToString("0.##", CultureInfo.CurrentCulture) + "K";
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string Exact(long value)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private sealed class RefreshChoice
        {
            internal RefreshChoice(string label, int seconds)
            {
                Label = label;
                Seconds = seconds;
            }

            internal string Label { get; }

            internal int Seconds { get; }

            public override string ToString()
            {
                return Label;
            }
        }

        internal static class Palette
        {
            private static readonly bool HighContrast = SystemInformation.HighContrast;

            internal static Color Window { get { return HighContrast ? SystemColors.Window : Color.FromArgb(246, 248, 251); } }

            internal static Color Card { get { return HighContrast ? SystemColors.Control : Color.White; } }

            internal static Color Text { get { return HighContrast ? SystemColors.WindowText : Color.FromArgb(38, 46, 57); } }

            internal static Color Heading { get { return HighContrast ? SystemColors.WindowText : Color.FromArgb(22, 31, 43); } }

            internal static Color Muted { get { return HighContrast ? SystemColors.GrayText : Color.FromArgb(88, 101, 117); } }

            internal static Color Border { get { return HighContrast ? SystemColors.ControlDark : Color.FromArgb(220, 226, 234); } }

            internal static Color InfoBackground { get { return HighContrast ? SystemColors.Info : Color.FromArgb(235, 242, 249); } }

            internal static Color NeutralBadge { get { return HighContrast ? SystemColors.Control : Color.FromArgb(230, 234, 239); } }

            internal static Color SuccessBadge { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(219, 241, 232); } }

            internal static Color SuccessText { get { return HighContrast ? SystemColors.HighlightText : Color.FromArgb(18, 104, 75); } }

            internal static Color WarningBadge { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(255, 235, 205); } }

            internal static Color WarningText { get { return HighContrast ? SystemColors.HighlightText : Color.FromArgb(143, 74, 13); } }

            internal static Color DangerText { get { return HighContrast ? SystemColors.HighlightText : Color.FromArgb(163, 45, 32); } }

            internal static Color Accent { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(24, 122, 154); } }

            internal static Color Blue { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(45, 108, 223); } }

            internal static Color Green { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(41, 142, 104); } }

            internal static Color Purple { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(126, 87, 194); } }

            internal static Color Orange { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(223, 123, 47); } }

            internal static Color Teal { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(24, 142, 151); } }
        }
    }
}
