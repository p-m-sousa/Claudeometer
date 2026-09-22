using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    internal sealed class PricingDialog : Form
    {
        private readonly List<ModelPrice> _prices;
        private readonly string[] _discoveredModels;
        private readonly DataGridView _grid;
        private readonly Button _edit;
        private readonly Button _delete;

        internal PricingDialog(PricingCatalog current, IEnumerable<string> discoveredModels, Action<PricingCatalog> save)
        {
            _prices = current.Prices.ToList();
            _discoveredModels = discoveredModels.ToArray();
            PricingUi.Setup(this, "Model pricing", new Size(820, 450));
            var layout = PricingUi.Layout();
            Controls.Add(layout);
            layout.Controls.Add(PricingUi.Note("Estimates in USD per 1M tokens. Select a model to edit its pricing periods. " +
                "Models without an applicable price show Not configured."), 0, 0);
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
                BackgroundColor = MainForm.Palette.Card, AccessibleName = "Configured models",
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells
            };
            _grid.Columns.Add("model", "Model");
            _grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _grid.Columns[0].MinimumWidth = 200;
            _grid.Columns.Add("periods", "Periods");
            _grid.Columns.Add("input", "Input");
            _grid.Columns.Add("output", "Output");
            _grid.Columns.Add("write", "Cache write");
            _grid.Columns.Add("read", "Cache read");
            layout.Controls.Add(_grid, 0, 1);
            layout.Controls.Add(PricingUi.Note("Rates above apply today. A model may also have earlier or future rates. " +
                "All changes take effect when you save this screen."), 0, 2);
            var actions = PricingUi.Buttons();
            var add = PricingUi.Button("Add model…", () => EditModel(null));
            _edit = PricingUi.Button("Edit model…", () => EditModel(SelectedModel));
            _delete = PricingUi.Button("Delete model", DeleteModel);
            actions.Controls.AddRange(new Control[] { add, _edit, _delete });
            layout.Controls.Add(actions, 0, 3);
            var footer = PricingUi.Buttons();
            var ok = PricingUi.Button("Save", () =>
            {
                try
                {
                    var result = new PricingCatalog(_prices);
                    save(result);
                    Result = result;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, "Pricing could not be saved. " + error.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            footer.Controls.AddRange(new Control[] { ok, cancel });
            layout.Controls.Add(footer, 0, 4);
            AcceptButton = ok;
            CancelButton = cancel;
            _grid.SelectionChanged += (sender, args) => UpdateButtons();
            _grid.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) EditModel(SelectedModel); };
            Reload(null);
        }

        internal PricingCatalog Result { get; private set; }
        private string SelectedModel { get { return _grid.CurrentRow == null ? null : _grid.CurrentRow.Cells[0].Value as string; } }

        private void UpdateButtons()
        {
            _edit.Enabled = _delete.Enabled = SelectedModel != null;
        }

        private void Reload(string selected)
        {
            _grid.Rows.Clear();
            var catalog = new PricingCatalog(_prices);
            foreach (var group in _prices.GroupBy(value => value.ModelId).OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var rate = catalog.Find(group.Key, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var row = _grid.Rows.Add(group.Key, group.Count(), Rate(rate, value => value.Input),
                    Rate(rate, value => value.Output), Rate(rate, value => value.CacheWrite), Rate(rate, value => value.CacheRead));
                if (group.Key == selected) _grid.CurrentCell = _grid.Rows[row].Cells[0];
            }
            UpdateButtons();
        }

        private static string Rate(ModelPrice price, Func<ModelPrice, decimal> value)
        {
            return price == null ? "Not configured" : value(price).ToString("0.######", CultureInfo.CurrentCulture);
        }

        private void EditModel(string model)
        {
            using (var editor = new ModelPricingDialog(model, _prices.Where(value => value.ModelId == model),
                _discoveredModels, _prices.Select(value => value.ModelId)))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                if (model != null) _prices.RemoveAll(value => value.ModelId == model);
                _prices.AddRange(editor.Result);
                Reload(editor.Result[0].ModelId);
            }
        }

        private void DeleteModel()
        {
            var model = SelectedModel;
            if (model == null) return;
            if (MessageBox.Show(this, "Delete all pricing periods for " + model + "? Its usage will remain recorded, " +
                "but its spend will be unpriced after saving.", "Delete model pricing", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
            _prices.RemoveAll(value => value.ModelId == model);
            Reload(null);
        }
    }

    internal sealed class ModelPricingDialog : Form
    {
        private readonly List<ModelPrice> _periods;
        private readonly HashSet<string> _configured;
        private readonly bool _newModel;
        private readonly ComboBox _model;
        private readonly ComboBox _period;
        private readonly DateTimePicker _date;
        private readonly NumericUpDown _input;
        private readonly NumericUpDown _output;
        private readonly NumericUpDown _write;
        private readonly NumericUpDown _read;
        private int _active;
        private bool _loading;

        internal ModelPricingDialog(string model, IEnumerable<ModelPrice> prices,
            IEnumerable<string> discovered, IEnumerable<string> configured)
        {
            _newModel = model == null;
            _configured = new HashSet<string>(configured, StringComparer.Ordinal);
            _periods = prices.ToList();
            // The placeholder identifier is only a draft; Save always uses the model field.
            if (_periods.Count == 0) _periods.Add(new ModelPrice("new-model", null, 0, 0, 0, 0));
            PricingUi.Setup(this, model == null ? "Add model pricing" : "Edit model pricing", new Size(620, 570));
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16), RowCount = 10
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < fields.RowCount; i++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scroll.Controls.Add(fields);
            Controls.Add(scroll);
            _model = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, Enabled = _newModel,
                AccessibleName = "Model identifier", AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };
            _model.Items.AddRange(discovered.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).Cast<object>().ToArray());
            _model.Text = model ?? string.Empty;
            AddField(fields, "Model", _model, 0);
            _period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Pricing period" };
            AddField(fields, "Pricing period", _period, 1);
            var periodButtons = PricingUi.Buttons();
            periodButtons.Controls.Add(PricingUi.Button("Add period", AddPeriod));
            periodButtons.Controls.Add(PricingUi.Button("Delete period", RemovePeriod));
            fields.Controls.Add(periodButtons, 1, 2);
            _date = new DateTimePicker
            {
                ShowCheckBox = true, Checked = false, Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd", Width = 165, AccessibleName = "Effective date (unchecked means all history)",
                MinDate = DateTimePicker.MinimumDateTime, MaxDate = DateTimePicker.MaximumDateTime
            };
            AddField(fields, "Effective date (optional)", _date, 3);
            _input = PriceInput("Input price in USD per million tokens");
            _output = PriceInput("Output price in USD per million tokens");
            _write = PriceInput("Cache write price in USD per million tokens");
            _read = PriceInput("Cache read price in USD per million tokens");
            AddField(fields, "Input / 1M tokens", _input, 4);
            AddField(fields, "Output / 1M tokens", _output, 5);
            AddField(fields, "Cache write / 1M tokens", _write, 6);
            AddField(fields, "Cache read / 1M tokens", _read, 7);
            var note = PricingUi.Note("All prices are USD. Choose a discovered model or type its exact identifier. Zero means free. " +
                "Uncheck the date to set the historical baseline. Dated prices apply from that local day until the next change. " +
                "Earlier usage without a baseline is Not configured. Cache write uses cache creation tokens.");
            fields.Controls.Add(note, 0, 8);
            fields.SetColumnSpan(note, 2);
            note.MaximumSize = new Size(540, 0);
            var footer = PricingUi.Buttons();
            var ok = PricingUi.Button("Save model", () =>
            {
                if (!CommitCurrent()) return;
                Result = _periods.ToList();
                DialogResult = DialogResult.OK;
                Close();
            });
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            footer.Controls.AddRange(new Control[] { ok, cancel });
            fields.Controls.Add(footer, 0, 9);
            fields.SetColumnSpan(footer, 2);
            AcceptButton = ok;
            CancelButton = cancel;
            _period.SelectedIndexChanged += (sender, args) =>
            {
                if (_loading) return;
                var next = _period.SelectedIndex;
                if (!CommitCurrent())
                {
                    _loading = true;
                    _period.SelectedIndex = _active;
                    _loading = false;
                    return;
                }
                ShowPeriod(next);
            };
            ShowPeriod(0);
        }

        internal IList<ModelPrice> Result { get; private set; }

        private bool CommitCurrent()
        {
            try
            {
                if (_newModel && _configured.Contains(_model.Text.Trim()))
                    throw new ArgumentException("This model is already configured. Select it in the model list and choose Edit model.");
                var date = _date.Checked ? _date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                var current = new ModelPrice(_model.Text, date, _input.Value, _output.Value, _write.Value, _read.Value);
                var draft = _periods.Select((value, index) => index == _active ? current
                    : new ModelPrice(current.ModelId, value.EffectiveDate, value.Input, value.Output, value.CacheWrite, value.CacheRead)).ToList();
                new PricingCatalog(draft); // Validate duplicate effective dates before accepting a draft.
                _periods.Clear();
                _periods.AddRange(draft);
                return true;
            }
            catch (ArgumentException error)
            {
                MessageBox.Show(this, error.Message, "Check model pricing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void ShowPeriod(int index)
        {
            _loading = true;
            _active = index;
            _period.Items.Clear();
            foreach (var item in _periods) _period.Items.Add(item.EffectiveDate ?? "All history (baseline)");
            _period.SelectedIndex = index;
            var price = _periods[index];
            _date.Value = price.EffectiveDate == null ? DateTime.Today
                : DateTime.ParseExact(price.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            _date.Checked = price.EffectiveDate != null;
            _input.Value = price.Input;
            _output.Value = price.Output;
            _write.Value = price.CacheWrite;
            _read.Value = price.CacheRead;
            _loading = false;
        }

        private void AddPeriod()
        {
            if (!CommitCurrent()) return;
            var date = DateTime.Today;
            while (_periods.Any(value => value.EffectiveDate == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                date = date.AddDays(1);
            var previous = _periods[_active];
            _periods.Add(new ModelPrice(previous.ModelId, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                previous.Input, previous.Output, previous.CacheWrite, previous.CacheRead));
            ShowPeriod(_periods.Count - 1);
        }

        private void RemovePeriod()
        {
            if (_periods.Count == 1)
            {
                MessageBox.Show(this, "Keep at least one pricing period, or delete the model from the model list.", Text);
                return;
            }
            _periods.RemoveAt(_active);
            ShowPeriod(Math.Min(_active, _periods.Count - 1));
        }

        private static void AddField(TableLayoutPanel fields, string title, Control control, int row)
        {
            fields.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 8, 14, 10) }, 0, row);
            fields.Controls.Add(control, 1, row);
        }

        private static NumericUpDown PriceInput(string name)
        {
            return new NumericUpDown
            {
                Minimum = 0, Maximum = 1000000M, DecimalPlaces = 6, Increment = 0.01M,
                ThousandsSeparator = true, Width = 200, AccessibleName = name
            };
        }
    }

    internal static class PricingUi
    {
        internal static void Setup(Form form, string title, Size size)
        {
            form.Text = title;
            form.Font = new Font("Segoe UI", 9F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.StartPosition = FormStartPosition.CenterParent;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.ShowIcon = false;
            form.BackColor = MainForm.Palette.Window;
            form.ClientSize = size;
            form.MinimumSize = new Size(460, 320);
            form.Load += (sender, args) =>
            {
                var area = Screen.FromControl(form).WorkingArea;
                form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
            };
        }

        internal static TableLayoutPanel Layout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16), AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(i == 1 ? SizeType.Percent : SizeType.AutoSize, i == 1 ? 100 : 0));
            return layout;
        }

        internal static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, Dock = DockStyle.Top, ForeColor = MainForm.Palette.Muted, Margin = new Padding(0, 6, 0, 12) };
        }

        internal static FlowLayoutPanel Buttons()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 5, 0, 5) };
        }

        internal static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.System };
            button.Click += (sender, args) => action();
            return button;
        }
    }
}
