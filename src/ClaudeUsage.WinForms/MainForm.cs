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
        private const string EmptyCacheJson = "{\"version\":5,\"dailyModelTokensVersion\":5,\"dailyActivity\":[],\"dailyModelTokens\":[],\"modelUsage\":{},\"totalSessions\":0,\"totalMessages\":0,\"hourCounts\":{}}";

        private readonly SettingsStore _settings = new SettingsStore();
        private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Dictionary<string, Label> _todayValues = new Dictionary<string, Label>(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> _historyValues = new Dictionary<string, Label>(StringComparer.Ordinal);
        private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _watchDebounceTimer = new System.Windows.Forms.Timer();

        private string _sourcePath;
        private FileSystemWatcher _cacheWatcher;
        private FileSystemWatcher _projectsWatcher;
        private StatsCacheDocument _historyDocument;
        private StatsCacheDocument _combinedDocument;
        private TodayUsageSnapshot _todaySnapshot;
        private bool _refreshing;
        private bool _refreshQueued;
        private bool _updatingFilters;

        private Label _sourceLabel;
        private Label _sourceStateLabel;
        private Label _todayHeading;
        private Label _todayNotice;
        private Label _todaySecondary;
        private Label _historyNotice;
        private Button _refreshButton;
        private ComboBox _refreshInterval;
        private DataGridView _todayModelsGrid;
        private DataGridView _historyGrid;
        private DataGridView _allTimeGrid;
        private ComboBox _rangePreset;
        private ComboBox _modelFilter;
        private DateTimePicker _fromDate;
        private DateTimePicker _toDate;
        private Chart _historyChart;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripStatusLabel _freshnessLabel;

        public MainForm(string explicitPath)
        {
            _sourcePath = StatsFileLoader.ResolveDefaultPath(explicitPath, _settings.ReadSourcePath());
            InitializeWindow();
            BuildInterface();
            ConfigureTimers();

            Shown += async (sender, args) =>
            {
                SetupWatcher();
                await RefreshDashboardAsync(false);
            };
            FormClosing += OnFormClosing;
        }

        private void InitializeWindow()
        {
            Text = "Claude Usage";
            StartPosition = FormStartPosition.CenterScreen;
            // Keep the window usable on small/high-DPI displays. The tab bodies
            // scroll, and the KPI cards reflow into two rows.
            MinimumSize = new Size(520, 360);
            var workArea = Screen.PrimaryScreen == null
                ? new Rectangle(0, 0, 1180, 720)
                : Screen.PrimaryScreen.WorkingArea;
            Size = new Size(
                Math.Min(1180, Math.Max(MinimumSize.Width, workArea.Width - 32)),
                Math.Min(720, Math.Max(MinimumSize.Height, workArea.Height - 32)));
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Palette.Window;
            ForeColor = Palette.Text;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;
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
                Font = new Font(Font, FontStyle.Regular),
                Padding = new Point(18, 7),
                Margin = new Padding(0, 14, 0, 8),
                AccessibleName = "Usage views"
            };
            tabs.TabPages.Add(BuildTodayTab());
            tabs.TabPages.Add(BuildHistoryTab());
            tabs.TabPages.Add(BuildAllTimeTab());
            root.Controls.Add(tabs, 0, 1);

            var status = new StatusStrip
            {
                Dock = DockStyle.Fill,
                SizingGrip = false,
                BackColor = Palette.Window,
                Padding = new Padding(0, 4, 0, 4)
            };
            _statusLabel = new ToolStripStatusLabel("Waiting to load…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _freshnessLabel = new ToolStripStatusLabel("Local only · Read-only") { TextAlign = ContentAlignment.MiddleRight };
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
                ColumnCount = 4,
                RowCount = 2,
                BackColor = Palette.Window,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var title = new Label
            {
                Text = "Claude Usage",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Palette.Heading,
                Margin = new Padding(0, 0, 18, 2)
            };
            header.Controls.Add(title, 0, 0);

            _sourceStateLabel = new Label
            {
                Text = "NOT LOADED",
                AutoSize = true,
                BackColor = Palette.NeutralBadge,
                ForeColor = Palette.Muted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8, 5, 8, 5),
                Margin = new Padding(0, 7, 0, 0)
            };
            header.Controls.Add(_sourceStateLabel, 1, 0);

            _refreshButton = new Button
            {
                Text = "Refresh now",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(8, 3, 0, 0),
                AccessibleDescription = "Reread the cache and current-day local usage files"
            };
            _refreshButton.Click += async (sender, args) => await RefreshDashboardAsync(true);
            header.Controls.Add(_refreshButton, 2, 0);

            var chooseButton = new Button
            {
                Text = "Choose cache…",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(8, 3, 0, 0),
                AccessibleDescription = "Choose a Claude Code stats-cache.json file"
            };
            chooseButton.Click += ChooseSource;
            header.Controls.Add(chooseButton, 3, 0);

            _sourceLabel = new Label
            {
                Text = _sourcePath,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 25,
                ForeColor = Palette.Muted,
                Margin = new Padding(1, 3, 14, 0),
                AccessibleName = "Usage source path",
                AccessibleDescription = _sourcePath
            };
            _toolTip.SetToolTip(_sourceLabel, _sourcePath);
            header.SetColumnSpan(_sourceLabel, 2);
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
            header.SetColumnSpan(intervalPanel, 2);
            header.Controls.Add(intervalPanel, 2, 1);
            return header;
        }

        private TabPage BuildTodayTab()
        {
            var tab = NewTab("Today");
            var content = NewScrollableStack();
            tab.Controls.Add(content);

            _todayHeading = SectionTitle("Today’s cumulative local usage");
            content.Controls.Add(_todayHeading, 0, 0);

            _todayNotice = NoticeLabel(
                "Live from Claude Code transcript usage metadata. Tokens, messages, and tool calls use each entry’s UTC date; session counts use the date the session began. Processed tokens are local counters, not remaining plan quota.");
            content.Controls.Add(_todayNotice, 0, 1);

            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 2,
                Margin = new Padding(0, 15, 0, 10)
            };
            for (var i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.Controls.Add(CreateCard("Processed tokens", "processed", _todayValues, Palette.Accent), 0, 0);
            cards.Controls.Add(CreateCard("Input", "input", _todayValues, Palette.Blue), 1, 0);
            cards.Controls.Add(CreateCard("Output", "output", _todayValues, Palette.Green), 2, 0);
            cards.Controls.Add(CreateCard("Cache read", "cacheRead", _todayValues, Palette.Purple), 0, 1);
            cards.Controls.Add(CreateCard("Cache creation", "cacheCreate", _todayValues, Palette.Orange), 1, 1);
            cards.Controls.Add(CreateCard("I/O tokens", "io", _todayValues, Palette.Teal), 2, 1);
            content.Controls.Add(cards, 0, 2);

            _todaySecondary = new Label
            {
                Text = "Sessions 0   ·   Messages 0   ·   Tool calls 0",
                AutoSize = true,
                ForeColor = Palette.Muted,
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(2, 0, 0, 14)
            };
            content.Controls.Add(_todaySecondary, 0, 3);

            content.Controls.Add(SubsectionTitle("Today by model"), 0, 4);
            _todayModelsGrid = CreateGrid("Current-day token categories by model");
            AddTextColumn(_todayModelsGrid, "Model", 260, DataGridViewAutoSizeColumnMode.Fill);
            AddNumberColumn(_todayModelsGrid, "Input");
            AddNumberColumn(_todayModelsGrid, "Output");
            AddNumberColumn(_todayModelsGrid, "Cache read");
            AddNumberColumn(_todayModelsGrid, "Cache creation");
            AddNumberColumn(_todayModelsGrid, "Processed");
            _todayModelsGrid.Height = 245;
            content.Controls.Add(_todayModelsGrid, 0, 5);
            return tab;
        }

        private TabPage BuildHistoryTab()
        {
            var tab = NewTab("History");
            var content = NewScrollableStack();
            tab.Controls.Add(content);

            content.Controls.Add(SectionTitle("Historical slices"), 0, 0);
            _historyNotice = NoticeLabel(
                "Current Claude Code history records processed tokens: input + output + cache read + cache creation. Activity counts are date-filtered but cannot be attributed to a model in this cache.");
            content.Controls.Add(_historyNotice, 0, 1);
            content.Controls.Add(BuildHistoryFilters(), 0, 2);

            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 2,
                Margin = new Padding(0, 14, 0, 12)
            };
            for (var i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.Controls.Add(CreateCard("Processed tokens", "tokens", _historyValues, Palette.Teal), 0, 0);
            cards.Controls.Add(CreateCard("Messages · all models", "messages", _historyValues, Palette.Blue), 1, 0);
            cards.Controls.Add(CreateCard("Sessions · all models", "sessions", _historyValues, Palette.Green), 2, 0);
            cards.Controls.Add(CreateCard("Tool calls · all models", "tools", _historyValues, Palette.Purple), 0, 1);
            cards.Controls.Add(CreateCard("Active days", "days", _historyValues, Palette.Orange), 1, 1);
            content.Controls.Add(cards, 0, 3);

            _historyChart = BuildHistoryChart();
            content.Controls.Add(_historyChart, 0, 4);

            content.Controls.Add(SubsectionTitle("Daily values"), 0, 5);
            _historyGrid = CreateGrid("Exact historical values by date");
            AddTextColumn(_historyGrid, "Date", 125, DataGridViewAutoSizeColumnMode.None);
            AddNumberColumn(_historyGrid, "Processed tokens");
            AddNumberColumn(_historyGrid, "Messages · all models");
            AddNumberColumn(_historyGrid, "Sessions · all models");
            AddNumberColumn(_historyGrid, "Tool calls · all models");
            _historyGrid.Height = 270;
            content.Controls.Add(_historyGrid, 0, 6);
            return tab;
        }

        private TabPage BuildAllTimeTab()
        {
            var tab = NewTab("All-time details");
            var content = NewScrollableStack();
            tab.Controls.Add(content);
            content.Controls.Add(SectionTitle("All-time model details"), 0, 0);
            content.Controls.Add(NoticeLabel(
                "These category totals come from the cache plus the current UTC day. They cannot be date-filtered. Cost, when present, is Claude Code’s local value and is not an invoice or subscription usage amount."), 0, 1);

            _allTimeGrid = CreateGrid("All-time token categories by model");
            AddTextColumn(_allTimeGrid, "Model", 260, DataGridViewAutoSizeColumnMode.Fill);
            AddNumberColumn(_allTimeGrid, "Input");
            AddNumberColumn(_allTimeGrid, "Output");
            AddNumberColumn(_allTimeGrid, "Cache read");
            AddNumberColumn(_allTimeGrid, "Cache creation");
            AddNumberColumn(_allTimeGrid, "Processed");
            AddNumberColumn(_allTimeGrid, "Web searches");
            AddTextColumn(_allTimeGrid, "Local cost", 105, DataGridViewAutoSizeColumnMode.None);
            _allTimeGrid.Height = 440;
            _allTimeGrid.Margin = new Padding(0, 16, 0, 0);
            content.Controls.Add(_allTimeGrid, 0, 2);
            return tab;
        }

        private Control BuildHistoryFilters()
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
                Width = 125,
                AccessibleName = "History date preset"
            };
            _rangePreset.Items.AddRange(new object[] { "Today", "Last 7 days", "Last 30 days", "Last 90 days", "All time", "Custom" });
            _rangePreset.SelectedIndex = 2;
            _rangePreset.SelectedIndexChanged += (sender, args) =>
            {
                if (_updatingFilters) return;
                ApplyPresetDates();
                RenderHistory();
            };
            panel.Controls.Add(_rangePreset);

            panel.Controls.Add(FilterLabel("From"));
            _fromDate = DatePicker("History start date");
            _fromDate.ValueChanged += (sender, args) => OnCustomDateChanged();
            panel.Controls.Add(_fromDate);
            panel.Controls.Add(FilterLabel("To"));
            _toDate = DatePicker("History end date");
            _toDate.ValueChanged += (sender, args) => OnCustomDateChanged();
            panel.Controls.Add(_toDate);

            panel.Controls.Add(FilterLabel("Model"));
            _modelFilter = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 270,
                AccessibleName = "History model filter"
            };
            _modelFilter.Items.Add(AllModelsLabel);
            _modelFilter.SelectedIndex = 0;
            _modelFilter.SelectedIndexChanged += (sender, args) =>
            {
                if (!_updatingFilters) RenderHistory();
            };
            panel.Controls.Add(_modelFilter);

            var reset = new Button { Text = "Reset", AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
            reset.Click += (sender, args) =>
            {
                _updatingFilters = true;
                _rangePreset.SelectedIndex = 2;
                _modelFilter.SelectedIndex = 0;
                _updatingFilters = false;
                ApplyPresetDates();
                RenderHistory();
            };
            panel.Controls.Add(reset);
            return panel;
        }

        private void ConfigureTimers()
        {
            _refreshTimer.Tick += async (sender, args) => await RefreshDashboardAsync(false);
            _watchDebounceTimer.Interval = 900;
            _watchDebounceTimer.Tick += async (sender, args) =>
            {
                _watchDebounceTimer.Stop();
                await RefreshDashboardAsync(false);
            };

            var savedSeconds = _settings.ReadRefreshSeconds();
            for (var index = 0; index < _refreshInterval.Items.Count; index++)
            {
                if (((RefreshChoice)_refreshInterval.Items[index]).Seconds == savedSeconds)
                {
                    _refreshInterval.SelectedIndex = index;
                    break;
                }
            }
            if (_refreshInterval.SelectedIndex < 0) _refreshInterval.SelectedIndex = 1;
            _refreshInterval.SelectedIndexChanged += (sender, args) => ApplyRefreshInterval(true);
            ApplyRefreshInterval(false);
        }

        private void ApplyRefreshInterval(bool persist)
        {
            var choice = _refreshInterval.SelectedItem as RefreshChoice;
            var seconds = choice == null ? 30 : choice.Seconds;
            _refreshTimer.Stop();
            if (seconds > 0)
            {
                _refreshTimer.Interval = checked(seconds * 1000);
                _refreshTimer.Start();
            }
            if (persist) _settings.WriteRefreshSeconds(seconds);
        }

        private async Task RefreshDashboardAsync(bool userInitiated)
        {
            if (_refreshing)
            {
                _refreshQueued = true;
                return;
            }

            _refreshing = true;
            _refreshButton.Enabled = false;
            _sourceStateLabel.Text = "REFRESHING";
            _sourceStateLabel.BackColor = Palette.NeutralBadge;
            _sourceStateLabel.ForeColor = Palette.Muted;
            _statusLabel.Text = "Reading local Claude usage metadata…";

            try
            {
                var path = _sourcePath;
                var configDirectory = Path.GetDirectoryName(path);
                var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DashboardLoadResult result;
                var usePreviousHistory = !File.Exists(path) && _historyDocument != null;
                try
                {
                    result = await Task.Run(() => LoadDashboard(
                        path,
                        configDirectory,
                        date,
                        _lifetimeCancellation.Token,
                        usePreviousHistory));
                    result.UsedPreviousHistory = usePreviousHistory;
                }
                catch (Exception error) when (!(error is OperationCanceledException) && _historyDocument != null)
                {
                    // A cache replacement can be briefly unreadable. Keep the last good
                    // historical snapshot while still refreshing live current-day usage.
                    result = await Task.Run(() => LoadDashboard(
                        path,
                        configDirectory,
                        date,
                        _lifetimeCancellation.Token,
                        true));
                    result.UsedPreviousHistory = true;
                }

                if (HasIncompleteLiveScan(result.LiveWarnings) &&
                    _todaySnapshot != null &&
                    string.Equals(_todaySnapshot.Date, date, StringComparison.Ordinal))
                {
                    result = new DashboardLoadResult(
                        result.History,
                        StatsCacheOverlay.ApplyToday(result.History, _todaySnapshot),
                        _todaySnapshot,
                        result.CacheWarnings,
                        result.LiveWarnings,
                        result.CacheWasLoaded,
                        result.CacheReadAtUtc)
                    {
                        UsedPreviousHistory = result.UsedPreviousHistory,
                        UsedPreviousToday = true
                    };
                }
                _historyDocument = result.History;
                _combinedDocument = result.Combined;
                _todaySnapshot = result.Today;
                UpdateDashboard(result);
                var hasImportantWarning = result.CacheWarnings
                    .Concat(result.LiveWarnings)
                    .Any(value => value.Severity != WarningSeverity.Information);
                var stale = result.UsedPreviousHistory || result.UsedPreviousToday;
                _sourceStateLabel.Text = stale
                    ? "LIVE · STALE SNAPSHOT"
                    : (hasImportantWarning
                        ? "LIVE · WARNING"
                        : (result.CacheWasLoaded ? "LIVE" : "LIVE · NO CACHE"));
                _sourceStateLabel.BackColor = stale || hasImportantWarning ? Palette.WarningBadge : Palette.SuccessBadge;
                _sourceStateLabel.ForeColor = stale || hasImportantWarning ? Palette.WarningText : Palette.SuccessText;
            }
            catch (OperationCanceledException)
            {
                // Closing the form cancels in-flight scanning.
            }
            catch (Exception error)
            {
                _sourceStateLabel.Text = "NEEDS ATTENTION";
                _sourceStateLabel.BackColor = Palette.WarningBadge;
                _sourceStateLabel.ForeColor = Palette.WarningText;
                _statusLabel.Text = error.Message;
                _todayNotice.Text = "Usage files could not be read. " + error.Message + " Choose the cache file or try Refresh now.";
                _historyNotice.Text = "Historical data is unavailable. " + error.Message;
                if (userInitiated)
                {
                    MessageBox.Show(
                        this,
                        error.Message + "\r\n\r\nNo Claude files were changed.",
                        "Could not refresh Claude Usage",
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
                    BeginInvoke(new Action(async () => await RefreshDashboardAsync(false)));
                }
            }
        }

        private DashboardLoadResult LoadDashboard(
            string path,
            string configDirectory,
            string utcDate,
            CancellationToken cancellationToken,
            bool usePreviousHistory)
        {
            StatsCacheDocument history;
            IReadOnlyList<ParseWarning> cacheWarnings;
            var cacheLoaded = File.Exists(path);
            DateTime? cacheReadAt = null;
            if (usePreviousHistory)
            {
                if (_historyDocument == null) throw new InvalidOperationException("No prior history is available.");
                history = _historyDocument;
                cacheWarnings = new List<ParseWarning>();
                cacheLoaded = true;
            }
            else if (cacheLoaded)
            {
                var load = StatsFileLoader.LoadWithRetry(path);
                history = load.Parsed.Document;
                cacheWarnings = load.Parsed.Warnings;
                cacheReadAt = load.ReadAtUtc;
            }
            else
            {
                var parsed = StatsCacheParser.Parse(EmptyCacheJson);
                history = parsed.Document;
                cacheWarnings = parsed.Warnings;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var today = CollectTodayWithRetry(configDirectory, utcDate, cancellationToken);
            var combined = StatsCacheOverlay.ApplyToday(history, today);
            return new DashboardLoadResult(
                history,
                combined,
                today,
                cacheWarnings,
                today.Warnings,
                cacheLoaded,
                cacheReadAt);
        }

        private static TodayUsageSnapshot CollectTodayWithRetry(
            string configDirectory,
            string utcDate,
            CancellationToken cancellationToken)
        {
            TodayUsageSnapshot snapshot = null;
            var delays = new[] { 0, 100, 300 };
            foreach (var delay in delays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (delay > 0) Thread.Sleep(delay);
                snapshot = TodayUsageCollector.Collect(configDirectory, utcDate, cancellationToken);
                if (!HasIncompleteLiveScan(snapshot.Warnings)) return snapshot;
            }

            return snapshot;
        }

        private static bool HasIncompleteLiveScan(IEnumerable<ParseWarning> warnings)
        {
            return warnings.Any(value => value.IsTransient);
        }

        private void UpdateDashboard(DashboardLoadResult result)
        {
            RenderToday();
            UpdateModelFilter();
            ApplyPresetDates();
            RenderHistory();
            RenderAllTime();

            var warnings = result.CacheWarnings.Count + result.LiveWarnings.Count;
            var cacheText = result.UsedPreviousToday
                ? "Live scan incomplete; last good Today snapshot retained"
                : (result.UsedPreviousHistory
                    ? "History cache unavailable; last good history retained"
                    : (result.CacheWasLoaded
                        ? "History cache loaded"
                        : "History cache not found; showing live today data"));
            _statusLabel.Text = cacheText + " · " + result.Today.FilesMatched.ToString("N0") +
                                " matching transcript file(s) · " + warnings.ToString("N0") + " warning(s)";
            var importantWarning = result.CacheWarnings
                .Concat(result.LiveWarnings)
                .FirstOrDefault(value => value.Severity != WarningSeverity.Information);
            if (importantWarning != null)
            {
                _statusLabel.Text += " · " + importantWarning.Message;
            }
            _freshnessLabel.Text = "Refreshed " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture) +
                                   " · Cache through " + (result.History.LastComputedDate ?? "unknown");

            if (result.UsedPreviousHistory)
            {
                _historyNotice.Text = "The history cache is temporarily missing or unreadable. The last valid historical snapshot is still shown while live Today data continues to refresh.";
            }

            if (result.UsedPreviousToday)
            {
                _todayNotice.Text = "The latest transcript scan was incomplete, so the last valid Today snapshot remains visible. Automatic refresh will retry; no partial undercount was published.";
            }
            else if (result.CacheWasLoaded)
            {
                _historyNotice.Text = result.History.DailyTokensIncludeCache
                    ? "Daily history includes input, output, cache read, and cache creation tokens. Activity is all-model even when a model filter is active. Cache computed through " + (result.History.LastComputedDate ?? "an unknown date") + "."
                    : "This legacy cache stores input + output tokens only for historical days. Today uses all four token categories. Activity is all-model even when a model filter is active.";
            }
            else
            {
                _historyNotice.Text = "stats-cache.json was not found at the selected location. Today remains live, but historical slices will appear after Claude Code creates the cache (open /usage) or you choose another cache.";
            }
        }

        private void RenderToday()
        {
            if (_todaySnapshot == null) return;
            long input = 0;
            long output = 0;
            long cacheRead = 0;
            long cacheCreate = 0;
            foreach (var usage in _todaySnapshot.ModelUsage.Values)
            {
                input = SaturatingAdd(input, usage.InputTokens);
                output = SaturatingAdd(output, usage.OutputTokens);
                cacheRead = SaturatingAdd(cacheRead, usage.CacheReadInputTokens);
                cacheCreate = SaturatingAdd(cacheCreate, usage.CacheCreationInputTokens);
            }
            var io = SaturatingAdd(input, output);
            var processed = SaturatingAdd(SaturatingAdd(io, cacheRead), cacheCreate);

            _todayHeading.Text = "Today’s cumulative local usage · " + _todaySnapshot.Date + " UTC";
            SetCard(_todayValues, "processed", processed);
            SetCard(_todayValues, "input", input);
            SetCard(_todayValues, "output", output);
            SetCard(_todayValues, "cacheRead", cacheRead);
            SetCard(_todayValues, "cacheCreate", cacheCreate);
            SetCard(_todayValues, "io", io);
            _todaySecondary.Text = "Sessions " + FormatExact(_todaySnapshot.Activity.SessionCount) +
                                   "   ·   Messages " + FormatExact(_todaySnapshot.Activity.MessageCount) +
                                   "   ·   Tool calls " + FormatExact(_todaySnapshot.Activity.ToolCallCount) +
                                   "   ·   Files scanned " + FormatExact(_todaySnapshot.FilesScanned);

            _todayNotice.Text = _todaySnapshot.FilesMatched == 0
                ? "No qualifying Claude Code transcript activity was recorded on " + _todaySnapshot.Date + " UTC yet. This view refreshes automatically and stays entirely on this computer."
                : "Live from " + _todaySnapshot.FilesMatched.ToString("N0") + " local transcript file(s). Processed = input + output + cache read + cache creation. Entries use their UTC date; session counts use their start date. This is not remaining plan quota.";

            _todayModelsGrid.Rows.Clear();
            foreach (var usage in _todaySnapshot.ModelUsage.Values
                         .OrderByDescending(value => ModelProcessed(value))
                         .ThenBy(value => value.ModelId, StringComparer.Ordinal))
            {
                _todayModelsGrid.Rows.Add(
                    usage.ModelId,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheReadInputTokens,
                    usage.CacheCreationInputTokens,
                    ModelProcessed(usage));
            }
        }

        private void UpdateModelFilter()
        {
            if (_combinedDocument == null) return;
            var previous = _modelFilter.SelectedItem as string;
            _updatingFilters = true;
            _modelFilter.Items.Clear();
            _modelFilter.Items.Add(AllModelsLabel);
            foreach (var model in _combinedDocument.ModelIds) _modelFilter.Items.Add(model);
            var index = previous == null ? -1 : _modelFilter.Items.IndexOf(previous);
            _modelFilter.SelectedIndex = index >= 0 ? index : 0;
            _updatingFilters = false;
        }

        private void ApplyPresetDates()
        {
            if (_rangePreset == null || _fromDate == null || _toDate == null) return;
            var preset = _rangePreset.SelectedItem as string ?? "Last 30 days";
            var today = DateTime.UtcNow.Date;
            _updatingFilters = true;
            _fromDate.Enabled = preset == "Custom";
            _toDate.Enabled = preset == "Custom";
            if (preset != "Custom")
            {
                _toDate.Value = today;
                if (preset == "Today") _fromDate.Value = today;
                else if (preset == "Last 7 days") _fromDate.Value = today.AddDays(-6);
                else if (preset == "Last 30 days") _fromDate.Value = today.AddDays(-29);
                else if (preset == "Last 90 days") _fromDate.Value = today.AddDays(-89);
                else
                {
                    var earliest = EarliestDate(_combinedDocument);
                    _fromDate.Value = earliest ?? today;
                }
            }
            _updatingFilters = false;
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
            RenderHistory();
        }

        private void RenderHistory()
        {
            if (_combinedDocument == null || _rangePreset == null) return;
            try
            {
                string from = null;
                string to = null;
                var preset = _rangePreset.SelectedItem as string;
                if (preset != "All time")
                {
                    from = _fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    to = _toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                if (from != null && string.CompareOrdinal(from, to) > 0)
                {
                    _historyNotice.Text = "The From date must be on or before the To date.";
                    return;
                }

                var selected = _modelFilter.SelectedItem as string;
                IEnumerable<string> models = selected == null || selected == AllModelsLabel ? null : new[] { selected };
                var analytics = UsageAnalyticsCalculator.Calculate(_combinedDocument, new UsageFilter(from, to, models));
                SetCard(_historyValues, "tokens", analytics.TotalTokens);
                SetCard(_historyValues, "messages", analytics.TotalMessages);
                SetCard(_historyValues, "sessions", analytics.TotalSessions);
                SetCard(_historyValues, "tools", analytics.TotalToolCalls);
                SetCard(_historyValues, "days", analytics.ActiveDays);

                RenderHistoryChart(analytics);
                _historyGrid.Rows.Clear();
                foreach (var day in analytics.DailyUsage.OrderByDescending(value => value.Date))
                {
                    _historyGrid.Rows.Add(day.Date, day.Tokens, day.MessageCount, day.SessionCount, day.ToolCallCount);
                }

                if (analytics.IsEmpty)
                {
                    _historyNotice.Text = "No usage matches this date and model selection. Activity remains all-model because stats-cache.json has no per-model activity dimension.";
                }
                else if (selected != null && selected != AllModelsLabel)
                {
                    _historyNotice.Text = (_combinedDocument.DailyTokensIncludeCache
                            ? "Processed tokens"
                            : "Legacy input + output tokens") +
                        " are filtered to " + selected + ". Messages, sessions, and tool calls remain date-filtered totals for all models.";
                }
                else
                {
                    _historyNotice.Text = _combinedDocument.DailyTokensIncludeCache
                        ? "Daily processed tokens and activity for the selected dates. Tokens include input, output, cache read, and cache creation; this is not remaining plan quota."
                        : "Legacy daily input + output tokens and activity for the selected dates. The live Today view separately shows all four token categories.";
                }
            }
            catch (ArgumentException error)
            {
                _historyNotice.Text = error.Message;
            }
        }

        private void RenderHistoryChart(UsageAnalytics analytics)
        {
            var series = _historyChart.Series[0];
            series.Points.Clear();
            var byDate = analytics.DailyUsage.ToDictionary(value => value.Date, StringComparer.Ordinal);
            var min = analytics.FromDate ?? (analytics.DailyUsage.Count == 0 ? null : analytics.DailyUsage.Min(value => value.Date));
            var max = analytics.ToDate ?? (analytics.DailyUsage.Count == 0 ? null : analytics.DailyUsage.Max(value => value.Date));
            if (min == null || max == null) return;

            DateTime cursor;
            DateTime last;
            if (!DateTime.TryParseExact(min, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out cursor) ||
                !DateTime.TryParseExact(max, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) return;
            var maximumDays = 5000;
            while (cursor <= last && maximumDays-- > 0)
            {
                var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                DailyUsage day;
                byDate.TryGetValue(key, out day);
                var point = series.Points.Add(day == null ? 0D : day.Tokens);
                point.AxisLabel = key;
                point.ToolTip = key + ": " + FormatExact(day == null ? 0 : day.Tokens) +
                                  (_combinedDocument.DailyTokensIncludeCache
                                      ? " processed tokens"
                                      : " input + output tokens");
                cursor = cursor.AddDays(1);
            }

            var area = _historyChart.ChartAreas[0];
            area.AxisX.Interval = Math.Max(1, Math.Ceiling(series.Points.Count / 10D));
            area.RecalculateAxesScale();
            _historyChart.AccessibleDescription = (_combinedDocument.DailyTokensIncludeCache
                    ? "Processed tokens"
                    : "Input plus output tokens") +
                " over time. Exact values are available in the daily values table below.";
        }

        private void RenderAllTime()
        {
            if (_combinedDocument == null) return;
            _allTimeGrid.Rows.Clear();
            foreach (var usage in _combinedDocument.ModelUsage.Values
                         .OrderByDescending(ModelProcessed)
                         .ThenBy(value => value.ModelId, StringComparer.Ordinal))
            {
                var cost = usage.CostUsd.HasValue ? usage.CostUsd.Value.ToString("C2", CultureInfo.CurrentCulture) : "—";
                _allTimeGrid.Rows.Add(
                    usage.ModelId,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheReadInputTokens,
                    usage.CacheCreationInputTokens,
                    ModelProcessed(usage),
                    usage.WebSearchRequests,
                    cost);
            }
        }

        private void ChooseSource(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Choose Claude Code stats-cache.json",
                Filter = "Claude stats cache (stats-cache.json)|stats-cache.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                FileName = "stats-cache.json",
                InitialDirectory = Directory.Exists(Path.GetDirectoryName(_sourcePath)) ? Path.GetDirectoryName(_sourcePath) : null
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _sourcePath = Path.GetFullPath(dialog.FileName);
                _sourceLabel.Text = _sourcePath;
                _sourceLabel.AccessibleDescription = _sourcePath;
                _toolTip.SetToolTip(_sourceLabel, _sourcePath);
                _settings.WriteSourcePath(_sourcePath);
                _historyDocument = null;
                _combinedDocument = null;
                _todaySnapshot = null;
                SetupWatcher();
                BeginInvoke(new Action(async () => await RefreshDashboardAsync(true)));
            }
        }

        private void SetupWatcher()
        {
            DisposeWatcher(ref _cacheWatcher);
            DisposeWatcher(ref _projectsWatcher);
            try
            {
                var directory = Path.GetDirectoryName(_sourcePath);
                if (!Directory.Exists(directory)) return;
                // Watch only the selected cache and the creation of its sibling
                // projects directory. Events for other top-level files are ignored.
                _cacheWatcher = new FileSystemWatcher(directory, "*")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                AttachWatcher(_cacheWatcher);

                var projectsDirectory = Path.Combine(directory, "projects");
                if (Directory.Exists(projectsDirectory))
                {
                    _projectsWatcher = new FileSystemWatcher(projectsDirectory, "*.jsonl")
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 32768,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    AttachWatcher(_projectsWatcher);
                }
            }
            catch
            {
                // The interval timer remains a reliable fallback for network/WSL paths.
            }
        }

        private void AttachWatcher(FileSystemWatcher watcher)
        {
            watcher.Changed += OnUsageFileChanged;
            watcher.Created += OnUsageFileChanged;
            watcher.Deleted += OnUsageFileChanged;
            watcher.Renamed += OnUsageFileChanged;
            watcher.Error += OnWatcherError;
        }

        private static void DisposeWatcher(ref FileSystemWatcher watcher)
        {
            if (watcher == null) return;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }

        private void OnUsageFileChanged(object sender, FileSystemEventArgs args)
        {
            if (IsDisposed || Disposing) return;
            if (ReferenceEquals(sender, _cacheWatcher))
            {
                try
                {
                    var changedPath = Path.GetFullPath(args.FullPath);
                    var selectedCache = Path.GetFullPath(_sourcePath);
                    var projectsDirectory = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(_sourcePath),
                        "projects"));
                    if (string.Equals(changedPath, projectsDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        ScheduleWatcherRefresh(true);
                        return;
                    }
                    if (!string.Equals(changedPath, selectedCache, StringComparison.OrdinalIgnoreCase)) return;
                }
                catch
                {
                    return;
                }
            }
            else if (!IsAllowedTranscriptPath(args.FullPath))
            {
                return;
            }

            ScheduleWatcherRefresh(false);
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            if (IsDisposed || Disposing) return;
            ScheduleWatcherRefresh(true);
        }

        private void ScheduleWatcherRefresh(bool recreateWatchers)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (recreateWatchers) SetupWatcher();
                    _watchDebounceTimer.Stop();
                    _watchDebounceTimer.Start();
                }));
            }
            catch
            {
                // The window may be closing.
            }
        }

        private bool IsAllowedTranscriptPath(string fullPath)
        {
            if (!string.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase))
                return false;

            var configDirectory = Path.GetDirectoryName(_sourcePath);
            var projectsDirectory = Path.Combine(configDirectory, "projects");
            var prefix = projectsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            var normalized = Path.GetFullPath(fullPath);
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            var relative = normalized.Substring(prefix.Length);
            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2) return true;
            return segments.Length == 4 &&
                   string.Equals(segments[2], "subagents", StringComparison.OrdinalIgnoreCase) &&
                   segments[3].StartsWith("agent-", StringComparison.OrdinalIgnoreCase);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            _refreshTimer.Stop();
            _watchDebounceTimer.Stop();
            _lifetimeCancellation.Cancel();
            DisposeWatcher(ref _cacheWatcher);
            DisposeWatcher(ref _projectsWatcher);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5)
            {
                BeginInvoke(new Action(async () => await RefreshDashboardAsync(true)));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point),
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
                MaximumSize = new Size(1060, 0),
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
                Margin = new Padding(0, 0, 9, 0),
                AccessibleName = title
            };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(new Panel { Height = 3, Dock = DockStyle.Fill, BackColor = accent, Margin = new Padding(0) }, 0, 0);
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
                RowTemplate = { Height = 31 },
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
                Width = 125,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N0"
                },
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        private static Chart BuildHistoryChart()
        {
            var chart = new Chart
            {
                Dock = DockStyle.Top,
                Height = 285,
                BackColor = Palette.Card,
                BorderlineColor = Palette.Border,
                BorderlineDashStyle = ChartDashStyle.Solid,
                Margin = new Padding(0, 0, 0, 10),
                AccessibleName = "Historical tokens over time chart",
                Palette = ChartColorPalette.None
            };
            var area = new ChartArea("Usage")
            {
                BackColor = Palette.Card
            };
            area.AxisX.MajorGrid.Enabled = false;
            area.AxisX.LabelStyle.Angle = -45;
            area.AxisX.LabelStyle.ForeColor = Palette.Muted;
            area.AxisY.LabelStyle.ForeColor = Palette.Muted;
            area.AxisY.MajorGrid.LineColor = Palette.Border;
            area.AxisY.Title = "Tokens";
            area.AxisY.TitleForeColor = Palette.Muted;
            chart.ChartAreas.Add(area);
            chart.Series.Add(new Series("Historical tokens")
            {
                ChartType = SeriesChartType.Column,
                Color = Palette.Teal,
                IsValueShownAsLabel = false,
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
            label.Text = FormatCompact(value);
            label.AccessibleDescription = FormatExact(value);
            _toolTip.SetToolTip(label, FormatExact(value));
        }

        private static string FormatCompact(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000D).ToString("0.##", CultureInfo.CurrentCulture) + "B";
            if (value >= 1000000L) return (value / 1000000D).ToString("0.##", CultureInfo.CurrentCulture) + "M";
            if (value >= 1000L) return (value / 1000D).ToString("0.##", CultureInfo.CurrentCulture) + "K";
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string FormatExact(long value)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static long ModelProcessed(ModelUsage usage)
        {
            return SaturatingAdd(
                SaturatingAdd(usage.InputTokens, usage.OutputTokens),
                SaturatingAdd(usage.CacheReadInputTokens, usage.CacheCreationInputTokens));
        }

        private static long SaturatingAdd(long left, long right)
        {
            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }

        private static DateTime? EarliestDate(StatsCacheDocument document)
        {
            if (document == null) return null;
            var keys = document.DailyActivity.Select(value => value.Date)
                .Concat(document.DailyModelTokens.Select(value => value.Date))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            DateTime parsed;
            return keys.Count > 0 && DateTime.TryParseExact(keys[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                ? parsed
                : (DateTime?)null;
        }

        private sealed class RefreshChoice
        {
            public RefreshChoice(string label, int seconds)
            {
                Label = label;
                Seconds = seconds;
            }

            public string Label { get; private set; }
            public int Seconds { get; private set; }
            public override string ToString() { return Label; }
        }

        private sealed class DashboardLoadResult
        {
            public DashboardLoadResult(
                StatsCacheDocument history,
                StatsCacheDocument combined,
                TodayUsageSnapshot today,
                IReadOnlyList<ParseWarning> cacheWarnings,
                IReadOnlyList<ParseWarning> liveWarnings,
                bool cacheWasLoaded,
                DateTime? cacheReadAtUtc)
            {
                History = history;
                Combined = combined;
                Today = today;
                CacheWarnings = cacheWarnings;
                LiveWarnings = liveWarnings;
                CacheWasLoaded = cacheWasLoaded;
                CacheReadAtUtc = cacheReadAtUtc;
            }

            public StatsCacheDocument History { get; private set; }
            public StatsCacheDocument Combined { get; private set; }
            public TodayUsageSnapshot Today { get; private set; }
            public IReadOnlyList<ParseWarning> CacheWarnings { get; private set; }
            public IReadOnlyList<ParseWarning> LiveWarnings { get; private set; }
            public bool CacheWasLoaded { get; private set; }
            public DateTime? CacheReadAtUtc { get; private set; }
            public bool UsedPreviousHistory { get; set; }
            public bool UsedPreviousToday { get; set; }
        }

        private static class Palette
        {
            private static readonly bool HighContrast = SystemInformation.HighContrast;
            public static Color Window { get { return HighContrast ? SystemColors.Window : Color.FromArgb(246, 248, 251); } }
            public static Color Card { get { return HighContrast ? SystemColors.Control : Color.White; } }
            public static Color Text { get { return HighContrast ? SystemColors.WindowText : Color.FromArgb(38, 46, 57); } }
            public static Color Heading { get { return HighContrast ? SystemColors.WindowText : Color.FromArgb(22, 31, 43); } }
            public static Color Muted { get { return HighContrast ? SystemColors.GrayText : Color.FromArgb(88, 101, 117); } }
            public static Color Border { get { return HighContrast ? SystemColors.ControlDark : Color.FromArgb(220, 226, 234); } }
            public static Color InfoBackground { get { return HighContrast ? SystemColors.Info : Color.FromArgb(235, 242, 249); } }
            public static Color NeutralBadge { get { return HighContrast ? SystemColors.Control : Color.FromArgb(230, 234, 239); } }
            public static Color SuccessBadge { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(219, 241, 232); } }
            public static Color SuccessText { get { return HighContrast ? SystemColors.HighlightText : Color.FromArgb(18, 104, 75); } }
            public static Color WarningBadge { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(255, 235, 205); } }
            public static Color WarningText { get { return HighContrast ? SystemColors.HighlightText : Color.FromArgb(143, 74, 13); } }
            public static Color Accent { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(63, 81, 181); } }
            public static Color Blue { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(45, 108, 223); } }
            public static Color Green { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(41, 142, 104); } }
            public static Color Purple { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(126, 87, 194); } }
            public static Color Orange { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(223, 123, 47); } }
            public static Color Teal { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(24, 142, 151); } }
        }
    }
}
