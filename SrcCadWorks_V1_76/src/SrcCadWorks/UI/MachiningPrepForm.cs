using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    internal sealed class MachiningPrepForm : Form
    {
        private ResumeSession _resume;
        private readonly SldWorks _swApp;
        private readonly BindingList<MachiningRow> _rows = new BindingList<MachiningRow>();
        private DataGridView _grid;
        private TextBox _project, _fileName, _fileCode, _designer, _preparer, _approver, _reason, _note, _output, _template;
        private NumericUpDown _assemblyQuantity;
        private readonly Dictionary<MachiningRow, int> _baseQuantities = new Dictionary<MachiningRow, int>();
        private CheckBox _pdf, _excel, _keepOnTop, _hideProcessed, _includeHidden, _includePassive;
        private Label _status, _progressPercent;
        private ProgressBar _progress;
        private Button _create;
        private RadioButton _speedSafe, _speedNormal, _speedFast;
        private EDrawingsPreviewControl _preview;
        private SplitContainer _contentSplit;
        private int _normalSplitterDistance;
        private bool _previewExpanded;
        private ContextMenuStrip _sortMenu;
        private string _sortProperty = "Sequence";
        private bool _sortAscending = true;
        private SolidWorksOwnedWindow.Tracker _ownerTracker;
        private ProcessProjectInfo _info;
        private bool _busy;
        private bool _pauseRequested;
        private bool _productionRunning;
        private PrepTelegramControl _telegramControl;
        private AssemblyVisibilityTracker _visibilityTracker;

        private static readonly Color Accent = Color.FromArgb(202, 28, 36);
        private static readonly Color Success = Color.FromArgb(28, 128, 65);
        private static readonly Color Surface = Color.White;
        private static readonly Color Border = Color.FromArgb(205, 211, 218);
        private static readonly Color Muted = Color.FromArgb(94, 103, 113);

        internal bool KeepAboveSolidWorksRequested => _keepOnTop == null || _keepOnTop.Checked;

        public MachiningPrepForm(SldWorks swApp)
        {
            _swApp = swApp;
            Text = "SrcCadWorks — Talaşlı İmalat Dosyası Hazırla " + AppVersion.Display;
            SrcCadWorks.Services.BrandIcon.Apply(this);
            Width = 1580;
            Height = 920;
            MinimumSize = new Size(1180, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            BuildUi();
            _resume = new ResumeSession(this, _swApp, "Machining", () => _rows, () => _busy, Scan, CreateOutputs);
            ApplyTextRenderingQuality(this);
            Shown += (s, e) => BeginInvoke(new Action(() =>
            {
                WindowState = FormWindowState.Maximized;
                SolidWorksOwnedWindow.Apply(this, _swApp, KeepAboveSolidWorksRequested, false);
                _ownerTracker = SolidWorksOwnedWindow.Track(this, _swApp, KeepAboveSolidWorksRequested);
                _telegramControl = new PrepTelegramControl(() => _productionRunning, () => _pauseRequested, RequestTelegramPause, RequestTelegramResume);
                _telegramControl.StartMonitor();
                Scan();
            }));
            FormClosing += (s, e) =>
            {
                if (_busy) { e.Cancel = true; return; }
                RestoreHiddenComponents(true);
                try { _telegramControl?.Dispose(); } catch { }
                try { _preview?.Shutdown(); } catch { }
                try { _ownerTracker?.Dispose(); } catch { }
            };
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(14), BackColor = BackColor };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 382));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildFields(), 0, 1);
            root.Controls.Add(BuildStatus(), 0, 2);
            root.Controls.Add(BuildContent(), 0, 3);
            root.Controls.Add(BuildBottom(), 0, 4);
        }

        private Control BuildHeader()
        {
            // Lazer Hazırla ile aynı üst başlık şablonu.
            var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18, 10, 18, 8) };
            var logo = new PictureBox { Dock = DockStyle.Left, Width = 62, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Padding = new Padding(2) };
            try
            {
                string logoPath = FindLogo();
                if (File.Exists(logoPath)) logo.Image = Image.FromFile(logoPath);
            }
            catch { }

            var texts = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 7, 0, 0) };
            var title = new Label
            {
                Text = "SrcCadWorks",
                Dock = DockStyle.Top,
                Height = 33,
                ForeColor = Color.FromArgb(31, 36, 42),
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold)
            };
            var subtitle = new Label
            {
                Text = "TALAŞLI İMALAT DOSYASI HAZIRLA  •  PDF / SEÇİMLİ STEP",
                Dock = DockStyle.Top,
                Height = 27,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 9.5F)
            };
            texts.Controls.Add(subtitle);
            texts.Controls.Add(title);
            header.Controls.Add(texts);
            header.Controls.Add(logo);
            return header;
        }

        private Control BuildFields()
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Surface, Padding = new Padding(12, 8, 12, 8) };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var left = FieldTable(9);
            _project = Field(left, "Proje Adı", 0);
            _fileName = Field(left, "Dosya Adı", 1);
            _fileCode = Field(left, "Dosya Kodu", 2);
            _designer = Field(left, "Tasarlayan", 3);
            _preparer = Field(left, "Hazırlayan", 4);
            _approver = Field(left, "Onaylayan", 5);
            _reason = Field(left, "Talep Sebebi", 6);
            _assemblyQuantity = NumericField(left, "Montaj Adedi", 7, 1, 9999);
            _assemblyQuantity.ValueChanged += (s, e) => ApplyAssemblyMultiplier();
            _note = Field(left, "Not", 8);
            outer.Controls.Add(left, 0, 0);

            var right = RightTable(new[] { 40F, 40F, 44F, 44F, 36F, 44F, 50F });
            BrowseField(right, "Çıktı Klasörü", 0, out _output, BrowseOutput);
            BrowseField(right, "Talaşlı Şablonu", 1, out _template, BrowseTemplate);
            var opts = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = Surface,
                WrapContents = false
            };
            _pdf = new CheckBox { Text = "PDF", Checked = true, AutoSize = true, Margin = new Padding(0, 3, 24, 0) };
            _excel = new CheckBox { Text = "Excel", Checked = true, AutoSize = true, Margin = new Padding(0, 3, 24, 0) };
            _hideProcessed = new CheckBox { Text = "İşlenenleri gizle", Checked = false, AutoSize = true, Margin = new Padding(0, 3, 24, 0) };
            opts.Controls.Add(_pdf);
            opts.Controls.Add(_excel);
            opts.Controls.Add(_hideProcessed);
            right.Controls.Add(opts, 1, 2);
            right.Controls.Add(new Label { Text = "İşlem Modu", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted }, 0, 3);
            right.Controls.Add(BuildSpeedSelector(), 1, 3);
            var scopeOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            _includeHidden = new CheckBox { Text = "Gizlileri dahil et", Checked = true, AutoSize = true, Margin = new Padding(0, 3, 16, 0) };
            _includePassive = new CheckBox { Text = "Pasifleştirilmişleri dahil et", Checked = false, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            scopeOptions.Controls.Add(_includeHidden);
            scopeOptions.Controls.Add(_includePassive);
            right.Controls.Add(scopeOptions, 0, 4);
            right.SetColumnSpan(scopeOptions, 2);
            EventHandler scopeChanged = (s, e) =>
            {
                if (!_busy) SetProgress(0, 1, "Tarama kapsamı değişti — Yeniden Tara düğmesine basın", "");
            };
            _includeHidden.CheckedChanged += scopeChanged;
            _includePassive.CheckedChanged += scopeChanged;

            right.Controls.Add(new Label
            {
                Text = "STEP sütunu varsayılan boş gelir. Yalnız işaretlediğiniz satırlar STEP olarak verilir.",
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(0, 4, 0, 0)
            }, 1, 5);
            right.Controls.Add(new Label
            {
                Text = "TALAŞLI İMALAT kökü Ürün Tanımı ile klasör açar; tüm alt seviyelerdeki TALAŞLI parçaların PDF'leri aynı klasöre gider.",
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(0, 4, 0, 0)
            }, 1, 6);
            outer.Controls.Add(right, 1, 0);
            return outer;
        }

        private Control BuildSpeedSelector()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Surface,
                Margin = Padding.Empty,
                Padding = new Padding(0, 4, 0, 4)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));

            _speedSafe = MakeSpeedChoice("Güvenli", ProcessingSpeedMode.Safe);
            _speedNormal = MakeSpeedChoice("Normal", ProcessingSpeedMode.Normal);
            _speedFast = MakeSpeedChoice("Hızlı", ProcessingSpeedMode.Fast);
            panel.Controls.Add(_speedSafe, 0, 0);
            panel.Controls.Add(_speedNormal, 1, 0);
            panel.Controls.Add(_speedFast, 2, 0);
            SetSpeedChoice(ProcessingSpeedMode.Normal);

            var tip = new ToolTip();
            tip.SetToolTip(_speedSafe, "Daha uzun beklemeler ve daha sakin PDF/STEP geçişleri. Büyük montajlarda en stabil seçenek.");
            tip.SetToolTip(_speedNormal, "Önerilen dengeli çalışma modu.");
            tip.SetToolTip(_speedFast, "PDF/STEP işlemlerinde beklemeleri en aza indirir. Güçlü ve stabil sistemlerde kullanın.");
            return panel;
        }

        private RadioButton MakeSpeedChoice(string text, ProcessingSpeedMode mode)
        {
            var button = new RadioButton
            {
                Text = text,
                Appearance = Appearance.Button,
                AutoCheck = true,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                BackColor = Surface,
                ForeColor = Color.FromArgb(31, 36, 42),
                Margin = new Padding(2, 0, 2, 0),
                Tag = mode
            };
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.CheckedBackColor = Accent;
            button.CheckedChanged += (s, e) => UpdateSpeedChoiceVisuals();
            return button;
        }

        private void UpdateSpeedChoiceVisuals()
        {
            RadioButton[] buttons = { _speedSafe, _speedNormal, _speedFast };
            foreach (var button in buttons)
            {
                if (button == null) continue;
                button.BackColor = button.Checked ? Accent : Surface;
                button.ForeColor = button.Checked ? Color.White : Color.FromArgb(31, 36, 42);
                button.FlatAppearance.BorderColor = button.Checked ? Accent : Border;
            }
        }

        private ProcessingSpeedMode SelectedProcessingSpeed()
        {
            if (_speedSafe != null && _speedSafe.Checked) return ProcessingSpeedMode.Safe;
            if (_speedFast != null && _speedFast.Checked) return ProcessingSpeedMode.Fast;
            return ProcessingSpeedMode.Normal;
        }

        private string SelectedProcessingSpeedText()
        {
            switch (SelectedProcessingSpeed())
            {
                case ProcessingSpeedMode.Safe: return "Güvenli";
                case ProcessingSpeedMode.Fast: return "Hızlı";
                default: return "Normal";
            }
        }

        private void SetSpeedChoice(ProcessingSpeedMode mode)
        {
            if (_speedSafe != null) _speedSafe.Checked = mode == ProcessingSpeedMode.Safe;
            if (_speedNormal != null) _speedNormal.Checked = mode == ProcessingSpeedMode.Normal;
            if (_speedFast != null) _speedFast.Checked = mode == ProcessingSpeedMode.Fast;
            UpdateSpeedChoiceVisuals();
        }

        private void SetSpeedSelectorEnabled(bool enabled)
        {
            if (_includeHidden != null) _includeHidden.Enabled = enabled;
            if (_includePassive != null) _includePassive.Enabled = enabled;
            if (_speedSafe != null) _speedSafe.Enabled = enabled;
            if (_speedNormal != null) _speedNormal.Enabled = enabled;
            if (_speedFast != null) _speedFast.Enabled = enabled;
        }

        private Control BuildStatus()
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = BackColor, Padding = new Padding(4, 8, 4, 4) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            _status = new Label
            {
                Text = "Hazır",
                Dock = DockStyle.Fill,
                ForeColor = Accent,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            _progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
            _progressPercent = new Label
            {
                Text = "0%",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                ForeColor = Accent
            };
            p.Controls.Add(_status, 0, 0);
            p.Controls.Add(_progress, 1, 0);
            p.Controls.Add(_progressPercent, 2, 0);
            return p;
        }

        private Control BuildContent()
        {
            _contentSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2,
                BackColor = BackColor
            };
            _contentSplit.Panel1.Controls.Add(BuildGrid());
            _preview = new EDrawingsPreviewControl { Dock = DockStyle.Fill };
            _preview.PreviewExpandRequested += (s, e) => SetPreviewExpanded(true);
            _preview.BackRequested += (s, e) => SetPreviewExpanded(false);
            _contentSplit.Panel2.Controls.Add(_preview);
            _contentSplit.Resize += (s, e) => ConfigureContentSplitter();
            _contentSplit.HandleCreated += (s, e) => BeginInvoke(new Action(ConfigureContentSplitter));
            return _contentSplit;
        }

        private void ConfigureContentSplitter()
        {
            if (_contentSplit == null || _contentSplit.IsDisposed || !_contentSplit.IsHandleCreated ||
                _previewExpanded || _contentSplit.Panel1Collapsed) return;

            // Same safe SplitContainer strategy used by Lazer/Kaynak: never set
            // Panel1MinSize/Panel2MinSize before the form gets its real width.
            try
            {
                int width = _contentSplit.ClientSize.Width;
                if (width < 900) return;

                int min = 520;
                int max = width - 320 - _contentSplit.SplitterWidth;
                if (max <= min) return;

                int desired = Math.Max(min, Math.Min(max, (int)(width * 0.68)));
                if (_normalSplitterDistance <= 0)
                {
                    _contentSplit.SplitterDistance = desired;
                    _normalSplitterDistance = desired;
                }
                else if (_contentSplit.SplitterDistance < min || _contentSplit.SplitterDistance > max)
                {
                    int safe = Math.Max(min, Math.Min(max, _contentSplit.SplitterDistance));
                    _contentSplit.SplitterDistance = safe;
                    _normalSplitterDistance = safe;
                }
            }
            catch { }
        }

        private void SetPreviewExpanded(bool expanded)
        {
            if (_contentSplit == null || _preview == null) return;
            _previewExpanded = expanded;
            if (expanded)
            {
                if (!_contentSplit.Panel1Collapsed)
                {
                    _normalSplitterDistance = _contentSplit.SplitterDistance;
                    _contentSplit.Panel1Collapsed = true;
                }
            }
            else
            {
                if (_contentSplit.Panel1Collapsed) _contentSplit.Panel1Collapsed = false;
                if (_normalSplitterDistance > 0)
                {
                    try
                    {
                        int width = _contentSplit.ClientSize.Width;
                        int min = 520;
                        int max = width - 320 - _contentSplit.SplitterWidth;
                        if (max > min)
                            _contentSplit.SplitterDistance = Math.Max(min, Math.Min(max, _normalSplitterDistance));
                        else
                            ConfigureContentSplitter();
                    }
                    catch { ConfigureContentSplitter(); }
                }
                else ConfigureContentSplitter();
            }
            _preview.SetExpanded(expanded);
        }

        private Control BuildGrid()
        {
            _grid = new KeyboardDataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToOrderColumns = true,
                ColumnHeadersHeight = 34
            };
            AddTextColumn("Sıra", "Sequence", 60, true);
            AddTextColumn("Parça Kodu", "PartCode", 190, true);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "STEP", DataPropertyName = "StepSelected", MinimumWidth = 62, FillWeight = 45, ReadOnly = false, SortMode = DataGridViewColumnSortMode.Programmatic });
            AddTextColumn("Adet", "Quantity", 70, false);
            AddTextColumn("Ürün Tanımı", "ProductDescription", 260, true);
            AddTextColumn("Klasör", "FolderName", 260, true);
            AddTextColumn("Tür", "IsRoot", 75, true);
            AddTextColumn("Teknik Resim", "DrawingPath", 260, true);
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.Programmatic;
                column.HeaderCell.ToolTipText = "Başlığın sağındaki oka tıklayın: Lazer Hazırla gibi sıralama/seçim menüsü açılır. KÖK + ALT grupları birlikte tutulur.";
            }
            _grid.DataSource = _rows;
            _grid.CellBeginEdit += (sender, args) =>
            {
                if (_grid.Columns[args.ColumnIndex].DataPropertyName != "Quantity") return;
                if (_busy) { args.Cancel = true; return; }
                _quantityEditRow = _grid.Rows[args.RowIndex].DataBoundItem as MachiningRow;
                _quantityBeforeEdit = _quantityEditRow == null ? 0 : _quantityEditRow.Quantity;
            };
            _grid.CellValidating += (sender, args) =>
            {
                if (args.RowIndex < 0 || _grid.Columns[args.ColumnIndex].DataPropertyName != "Quantity") return;
                int quantity;
                bool valid = int.TryParse(Convert.ToString(args.FormattedValue), out quantity) && quantity > 0;
                args.Cancel = !valid;
                _grid.Rows[args.RowIndex].Cells[args.ColumnIndex].ErrorText = valid ? "" : "Adet 1 veya daha büyük bir tam sayı olmalıdır.";
            };
            _grid.CellEndEdit += (sender, args) =>
            {
                if (args.RowIndex < 0 || _grid.Columns[args.ColumnIndex].DataPropertyName != "Quantity") return;
                var row = _grid.Rows[args.RowIndex].DataBoundItem as MachiningRow;
                if (!_busy && row != null && ReferenceEquals(row, _quantityEditRow) && row.Quantity != _quantityBeforeEdit)
                {
                    _manualQuantities[row] = row.Quantity;
                    _grid.Rows[args.RowIndex].Cells[args.ColumnIndex].ToolTipText = "Elle girilen toplam adet. Yeniden Tara ile otomatik hesaba dönülür.";
                }
                _quantityEditRow = null;
            };
            _grid.DataError += (sender, args) =>
            {
                if (args.ColumnIndex >= 0 && _grid.Columns[args.ColumnIndex].DataPropertyName == "Quantity")
                {
                    args.ThrowException = false;
                    args.Cancel = true;
                }
            };
            _grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.SelectionChanged += (s, e) => RefreshPreview();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var row = _grid.Rows[e.RowIndex].DataBoundItem as MachiningRow;
                if (row == null) return;
                if (_grid.Columns[e.ColumnIndex].DataPropertyName == "IsRoot") e.Value = row.IsRoot ? "KÖK" : "ALT";
                if (row.IsRoot)
                {
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                    e.CellStyle.BackColor = Color.FromArgb(244, 238, 220);
                }
            };
            _grid.CellFormatting += (sender, args) =>
            {
                if (args.RowIndex < 0 || _processingRow == null) return;
                if (!ReferenceEquals(_grid.Rows[args.RowIndex].DataBoundItem, _processingRow)) return;
                args.CellStyle.BackColor = Color.FromArgb(255, 225, 228);
                args.CellStyle.SelectionBackColor = Color.FromArgb(255, 225, 228);
                args.CellStyle.SelectionForeColor = Color.FromArgb(40, 40, 40);
            };
            UpdateSortGlyphs();
            ExcelHeaderMenus.Attach(_grid, property => !string.IsNullOrWhiteSpace(property),
                property => string.Equals(property, _sortProperty, StringComparison.OrdinalIgnoreCase)
                    ? (_sortAscending ? 1 : -1) : 0);
            return _grid;
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (_busy || _grid == null || e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            try { _grid.EndEdit(); } catch { }
            if (e.X < _grid.Columns[e.ColumnIndex].Width - ExcelHeaderMenus.ArrowWidth(_grid)) return;
            ShowSortMenu(e.ColumnIndex);
        }

        private void ShowSortMenu(int columnIndex)
        {
            if (_grid == null || columnIndex < 0 || columnIndex >= _grid.Columns.Count) return;
            DataGridViewColumn column = _grid.Columns[columnIndex];
            string property = column.DataPropertyName;
            if (string.IsNullOrWhiteSpace(property)) return;

            try { _sortMenu?.Dispose(); } catch { }
            _sortMenu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            if (string.Equals(property, "StepSelected", StringComparison.OrdinalIgnoreCase))
            {
                var selectAll = new ToolStripMenuItem("Tümünü Seç");
                var clearAll = new ToolStripMenuItem("Seçimi Kaldır");
                selectAll.Click += (s, e) => { foreach (var r in _rows) r.StepSelected = true; _grid.Refresh(); };
                clearAll.Click += (s, e) => { foreach (var r in _rows) r.StepSelected = false; _grid.Refresh(); };
                _sortMenu.Items.Add(selectAll);
                _sortMenu.Items.Add(clearAll);
            }
            else
            {
                bool numeric = string.Equals(property, "Quantity", StringComparison.OrdinalIgnoreCase) || string.Equals(property, "Sequence", StringComparison.OrdinalIgnoreCase);
                string ascText = numeric ? "Küçükten → Büyüğe" : "A → Z";
                string descText = numeric ? "Büyükten → Küçüğe" : "Z → A";
                var asc = new ToolStripMenuItem(ascText) { Checked = string.Equals(_sortProperty, property, StringComparison.OrdinalIgnoreCase) && _sortAscending };
                var desc = new ToolStripMenuItem(descText) { Checked = string.Equals(_sortProperty, property, StringComparison.OrdinalIgnoreCase) && !_sortAscending };
                asc.Click += (s, e) => ApplySortFromMenu(property, true);
                desc.Click += (s, e) => ApplySortFromMenu(property, false);
                _sortMenu.Items.Add(asc);
                _sortMenu.Items.Add(desc);
            }
            Rectangle rect = _grid.GetCellDisplayRectangle(columnIndex, -1, true);
            _sortMenu.Show(_grid, new Point(Math.Max(0, rect.Right - 22), rect.Bottom));
        }

        private void ApplySortFromMenu(string property, bool ascending)
        {
            _sortProperty = property;
            _sortAscending = ascending;
            ApplyCurrentGridSort();
            try { _grid?.Invalidate(); } catch { }
        }

        private void ApplyCurrentGridSort()
        {
            if (_rows.Count == 0) { UpdateSortGlyphs(); return; }
            MachiningRow selected = _grid?.CurrentRow?.DataBoundItem as MachiningRow;

            // TALAŞLI tablosu hiyerarşiktir. Lazer'deki sıralama davranışını kullanırken
            // kök + alt satır grubunu parçalamıyoruz: gruplar seçilen sütuna göre, alt satırlar
            // da kendi grubu içinde aynı sütuna göre sıralanır.
            var groups = new List<List<MachiningRow>>();
            List<MachiningRow> current = null;
            foreach (var row in _rows)
            {
                if (row.IsRoot || current == null)
                {
                    current = new List<MachiningRow>();
                    groups.Add(current);
                }
                current.Add(row);
            }

            Comparison<MachiningRow> rowComparison = (a, b) =>
            {
                int cmp = CompareSortValues(GetSortValue(a, _sortProperty), GetSortValue(b, _sortProperty));
                if (cmp == 0) cmp = StringComparer.CurrentCultureIgnoreCase.Compare(a?.PartCode ?? "", b?.PartCode ?? "");
                return _sortAscending ? cmp : -cmp;
            };

            groups.Sort((ga, gb) => rowComparison(ga.FirstOrDefault(r => r.IsRoot) ?? ga.FirstOrDefault(), gb.FirstOrDefault(r => r.IsRoot) ?? gb.FirstOrDefault()));
            foreach (var group in groups)
            {
                if (group.Count <= 2) continue;
                var root = group.FirstOrDefault(r => r.IsRoot) ?? group[0];
                var children = group.Where(r => !ReferenceEquals(r, root)).ToList();
                children.Sort(rowComparison);
                group.Clear();
                group.Add(root);
                group.AddRange(children);
            }

            _rows.RaiseListChangedEvents = false;
            _rows.Clear();
            int rootNo = 0;
            foreach (var group in groups)
            {
                rootNo++;
                int childNo = 0;
                foreach (var row in group)
                {
                    row.Sequence = row.IsRoot ? rootNo.ToString() : rootNo + "." + (++childNo);
                    _rows.Add(row);
                }
            }
            _rows.RaiseListChangedEvents = true;
            _rows.ResetBindings();
            UpdateSortGlyphs();
            RestoreSelectedRow(selected);
        }

        private void RestoreSelectedRow(MachiningRow selected)
        {
            if (selected == null || _grid == null) return;
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                if (!ReferenceEquals(gridRow.DataBoundItem, selected)) continue;
                _grid.ClearSelection();
                gridRow.Selected = true;
                if (gridRow.Cells.Count > 0) _grid.CurrentCell = gridRow.Cells[0];
                break;
            }
        }

        private void UpdateSortGlyphs()
        {
            if (_grid == null) return;
            foreach (DataGridViewColumn column in _grid.Columns)
                column.HeaderCell.SortGlyphDirection = string.Equals(column.DataPropertyName, _sortProperty, StringComparison.OrdinalIgnoreCase)
                    ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                    : SortOrder.None;
        }

        private static object GetSortValue(MachiningRow row, string property)
        {
            if (row == null) return null;
            switch (property ?? "")
            {
                case "Sequence": return ParseSequence(row.Sequence);
                case "PartCode": return row.PartCode;
                case "StepSelected": return row.StepSelected;
                case "Quantity": return row.Quantity;
                case "ProductDescription": return row.ProductDescription;
                case "FolderName": return row.FolderName;
                case "IsRoot": return row.IsRoot ? 0 : 1;
                case "DrawingPath": return row.DrawingPath;
                default: return "";
            }
        }

        private static decimal ParseSequence(string value)
        {
            decimal d;
            string normalized = (value ?? "").Trim().Replace(',', '.');
            return decimal.TryParse(normalized, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : decimal.MaxValue;
        }

        private static int CompareSortValues(object left, object right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            if (left is string || right is string)
                return StringComparer.CurrentCultureIgnoreCase.Compare(Convert.ToString(left) ?? "", Convert.ToString(right) ?? "");
            var comparable = left as IComparable;
            if (comparable != null && left.GetType() == right.GetType()) return comparable.CompareTo(right);
            return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
        }


        private MachiningRow _processingRow;

        private void SetProcessingRow(MachiningRow row)
        {
            _processingRow = row;
            if (_grid == null) return;
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                if (ReferenceEquals(gridRow.DataBoundItem, row))
                {
                    try { _grid.FirstDisplayedScrollingRowIndex = gridRow.Index; } catch { }
                    break;
                }
            }
            _grid.Refresh();
        }

        private void RefreshPreview()
        {
            if (_preview == null || _busy) return;
            var row = _grid == null || _grid.CurrentRow == null ? null : _grid.CurrentRow.DataBoundItem as MachiningRow;
            if (row == null)
            {
                _preview.LoadRow(null);
                return;
            }
            _preview.LoadRow(new LaserPartRow
            {
                PartCode = row.PartCode,
                SourcePath = row.SourcePath,
                Configuration = row.Configuration,
                DrawingPath = row.DrawingPath
            });
        }

        private void AddTextColumn(string header, string property, int minWidth, bool readOnly)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = property, MinimumWidth = minWidth, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.Programmatic });
        }

        private Control BuildBottom()
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, Padding = new Padding(0, 8, 0, 0), BackColor = BackColor };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            var scan = MakeButton("YENİDEN TARA", false);
            scan.Click += (s, e) => Scan();
            var all = MakeButton("STEP TÜMÜ", false);
            all.Click += (s, e) => { foreach (var r in _rows) r.StepSelected = true; _grid.Refresh(); };
            var clear = MakeButton("STEP TEMİZLE", false);
            clear.Click += (s, e) => { foreach (var r in _rows) r.StepSelected = false; _grid.Refresh(); };

            _keepOnTop = new CheckBox
            {
                Text = "Pencereyi üstte tut",
                Checked = true,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(10, 10, 0, 0)
            };
            _keepOnTop.CheckedChanged += (s, e) =>
            {
                SolidWorksOwnedWindow.Apply(this, _swApp, _keepOnTop.Checked, false);
                _ownerTracker?.SetEnabled(_keepOnTop.Checked);
            };

            var close = MakeButton("KAPAT", false);
            close.Click += (s, e) =>
            {
                if (_busy) MessageBox.Show(this, "İşlem devam ediyor. Önce işlemin tamamlanmasını bekleyin veya ESC ile iptal edin.", "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else Close();
            };
            _create = MakeButton("OLUŞTUR", true);
            _create.Click += (s, e) => CreateOutputs();
            p.Controls.Add(scan, 0, 0);
            p.Controls.Add(all, 1, 0);
            p.Controls.Add(clear, 2, 0);
            p.Controls.Add(_keepOnTop, 3, 0);
            p.Controls.Add(close, 5, 0);
            p.Controls.Add(_create, 6, 0);
            return p;
        }

        private void Scan()
        {
            if (_busy) return;
            _resume.BeforeScan();
            _busy = true;
            _create.Enabled = false;
            SetSpeedSelectorEnabled(false);
            try { _preview?.LoadRow(null); } catch { }
            _rows.Clear();
            _baseQuantities.Clear();
            _manualQuantities.Clear();
            SetProgress(0, 1, "Talaşlı imalat taranıyor", "ESC ile durdurabilirsiniz");
            var esc = new EscapeCancellationMonitor();
            ModalDialogGuard guard = null;
            SolidWorksAutomationScope scope = null;
            try
            {
                guard = new ModalDialogGuard(null);
                scope = new SolidWorksAutomationScope(_swApp, true, true, false, false);
                // Lazer/Kaynak gibi üst proje bilgileri tarama bitmesini beklemeden hemen görünsün.
                _info = ProcessProjectInfoFactory.FromActiveAssembly(_swApp);
                ApplyInfo();
                var scanner = new MachiningAssemblyScanner(_swApp, () => esc.IsCancellationRequested,
                    (done, total, code) => SetProgress(done, total, "Tarama", code),
                    (row, isNew) =>
                    {
                        if (row == null) return;
                        int baseQuantity = Math.Max(0, row.Quantity);
                        _baseQuantities[row] = baseQuantity;
                        ApplyAssemblyMultiplierToRow(row, baseQuantity);
                        if (isNew)
                        {
                            _rows.Add(row);
                            if (_rows.Count == 1 && _grid != null && _grid.Rows.Count > 0)
                            {
                                try { _grid.CurrentCell = _grid.Rows[0].Cells[0]; } catch { }
                            }
                        }
                        else
                        {
                            try { int index = _rows.IndexOf(row); if (index >= 0) _rows.ResetItem(index); } catch { }
                        }
                        Application.DoEvents();
                    }, includeHidden: _includeHidden.Checked, includePassive: _includePassive.Checked);
                var result = scanner.Scan();
                ApplyAssemblyMultiplier();
                _resume.AfterScan();
                SetProgressComplete("Tarama tamamlandı • " + result.RootCount + " grup • " + result.PdfItemCount + " PDF satırı • STEP varsayılan 0" +
                    (result.Warnings.Count > 0 ? " • Uyarı: " + result.Warnings.Count : ""));
            }
            catch (OperationCanceledException)
            {
                SetWarningStatus("Tarama ESC ile iptal edildi.");
            }
            catch (Exception ex)
            {
                Logger.Error("Talaşlı imalat dosyası taraması başarısız", ex);
                MessageBox.Show(ex.Message, "SrcCadWorks");
                SetFailureStatus("Tarama hatası");
            }
            finally
            {
                SetProcessingRow(null);
                try { scope?.Dispose(); } catch { }
                try { guard?.Dispose(); } catch { }
                try { esc.Dispose(); } catch { }
                _busy = false;
                _create.Enabled = true;
                SetSpeedSelectorEnabled(true);
                if (_rows.Count > 0)
                {
                    try
                    {
                        if (_grid != null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
                        RefreshPreview();
                    }
                    catch { }
                }
            }
        }

        private void CreateOutputs()
        {
            if (_busy || _rows.Count == 0) return;

            // Son hücredeki adet de Oluştur'a basıldığında modele aktarılmalıdır.
            if (_grid != null && !_grid.EndEdit()) return;
            if (_grid != null) BindingContext[_rows].EndCurrentEdit();
            List<MachiningRow> stepRows = CommitAndGetSelectedStepRows();
            if (!_pdf.Checked && !_excel.Checked && stepRows.Count == 0)
            {
                MessageBox.Show("En az bir çıktı seçin: PDF, Excel veya STEP.", "SrcCadWorks");
                return;
            }

            PullInfo();
            string excelFileName = (string.IsNullOrWhiteSpace(_info.FileCode) ? "TALAŞLI İMALAT FORMU" : Safe(_info.FileCode.Trim())) + ".xlsx";
            ProcessingSpeedMode processingSpeed = SelectedProcessingSpeed();
            string root = _resume.Folder ?? ProcessFolder(_output.Text, "TALAŞLI İMALAT");
            if (!_resume.WantsResume && Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
            {
                string baseRoot = root;
                int suffix = 2;
                while (Directory.Exists(root)) root = baseRoot + " (" + suffix++ + ")";
            }
            _output.Text = root;
            try { root = _resume.Begin(root); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "SrcCadWorks — Devam kaydı"); return; }
            RestoreHiddenComponents(false);
            _busy = true;
            _productionRunning = true;
            _pauseRequested = false;
            _create.Enabled = false;
            SetSpeedSelectorEnabled(false);
            var esc = new EscapeCancellationMonitor();
            ModalDialogGuard guard = null;
            SolidWorksAutomationScope scope = null;
            var errors = _resume.PreviousErrors();
            var excelOutputNotes = _resume.PreviousNotes();
            int pdfCount = 0, stepCount = 0;
            bool excelCreated = false;
            int totalSteps = (_excel.Checked ? 1 : 0) + (_pdf.Checked ? _rows.Count : 0) + stepRows.Count;
            int completed = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            TelegramSettings telegramSettings = TelegramSettingsStore.Load();
            string telegramOperationName = BuildMachiningTelegramOperationName();
            bool telegramStarted = false;
            var processedRows = new List<MachiningRow>();
            try { _telegramControl?.BeginRun(telegramOperationName, totalSteps); } catch { }

            try
            {
                Directory.CreateDirectory(root);
                SendMachiningTelegramStarted(telegramSettings, telegramOperationName, _rows.Count, stepRows.Count, _pdf.Checked, _excel.Checked, SelectedProcessingSpeedText());
                telegramStarted = true;

                guard = new ModalDialogGuard(null);
                scope = new SolidWorksAutomationScope(_swApp, true, true, false, false);

                if (_pdf.Checked)
                {
                    var pdfExporter = new DrawingPdfExporter(_swApp, processingSpeed);
                    foreach (var row in _rows)
                    {
                        esc.ThrowIfRequested();
                        WaitIfPaused(esc);
                        string folder = Path.Combine(root, Safe(row.FolderName));
                        Directory.CreateDirectory(folder);
                        SetProcessingRow(row);
                        SetProgress(completed, totalSteps, "PDF hazırlanıyor • " + SelectedProcessingSpeedText(), row.PartCode);

                        _resume.ClearFailure("PDF", row, errors, excelOutputNotes, MachiningExcelKey(row));
                        _resume.SaveProgress(errors, excelOutputNotes);
                        if (string.IsNullOrWhiteSpace(row.DrawingPath) || !File.Exists(row.DrawingPath))
                        {
                            errors.Add(row.PartCode + ": teknik resim bulunamadı");
                            AddExcelOutputNote(excelOutputNotes, MachiningExcelKey(row), "HATA: " + row.PartCode + " - Teknik resim bulunamadı");
                        }
                        else
                        {
                            try
                            {
                                string pdfPath = Path.Combine(folder, Safe(row.PartCode) + ".pdf");
                                if (!_resume.Reuse("PDF", row, pdfPath)) { _resume.Start("PDF", row); pdfExporter.Export(row.DrawingPath, pdfPath); }
                                if (ResumeLedger.Complete(pdfPath)) { _resume.Commit("PDF", row, pdfPath); pdfCount++; if (!processedRows.Contains(row)) processedRows.Add(row); }
                                else { errors.Add(row.PartCode + ": PDF doğrulanamadı"); AddExcelOutputNote(excelOutputNotes, MachiningExcelKey(row), "HATA: " + row.PartCode + " - PDF doğrulanamadı"); }
                            }
                            catch (Exception ex) { errors.Add(row.PartCode + " PDF: " + ex.Message); AddExcelOutputNote(excelOutputNotes, MachiningExcelKey(row), "HATA: " + row.PartCode + " - PDF - " + ex.Message); }
                        }

                        _resume.SaveProgress(errors, excelOutputNotes);
                        completed++;
                        SetProgress(completed, totalSteps, "PDF • " + SelectedProcessingSpeedText(), row.PartCode);
                        ApplyInterPartDelay(processingSpeed, completed);
                    }
                }

                // PDF döngüsü bitmeden STEP başlamaz; yalnız sabitlenmiş seçili satırlar.
                if (stepRows.Count > 0)
                {
                    var stepExporter = new StepExporter(_swApp);
                    var usedStepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in stepRows)
                    {
                        esc.ThrowIfRequested();
                        WaitIfPaused(esc);
                        string folder = Path.Combine(root, Safe(row.FolderName));
                        Directory.CreateDirectory(folder);
                        SetProcessingRow(row);
                        SetProgress(completed, totalSteps, "STEP hazırlanıyor • " + SelectedProcessingSpeedText(), row.PartCode);
                        try
                        {
                            string stepPath = Path.Combine(folder, Safe(row.PartCode) + ".STEP");
                            string baseStepPath = stepPath;
                            int stepSuffix = 2;
                            while (!usedStepPaths.Add(stepPath))
                                stepPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(baseStepPath) + " (" + stepSuffix++ + ").STEP");
                            _resume.ClearFailure("STEP", row, errors, excelOutputNotes, MachiningExcelKey(row));
                            _resume.SaveProgress(errors, excelOutputNotes);
                            if (!_resume.Reuse("STEP", row, stepPath)) { _resume.Start("STEP", row); stepExporter.Export(row.SourcePath, row.Configuration, row.DrawingPath, row.PartCode, stepPath); }
                            if (ResumeLedger.Complete(stepPath)) { _resume.Commit("STEP", row, stepPath); stepCount++; if (!processedRows.Contains(row)) processedRows.Add(row); }
                            else { errors.Add(row.PartCode + ": STEP doğrulanamadı"); AddExcelOutputNote(excelOutputNotes, MachiningExcelKey(row), "HATA: " + row.PartCode + " - STEP doğrulanamadı"); }
                        }
                        catch (Exception ex) { errors.Add(row.PartCode + " STEP: " + ex.Message); AddExcelOutputNote(excelOutputNotes, MachiningExcelKey(row), "HATA: " + row.PartCode + " - STEP - " + ex.Message); }

                        _resume.SaveProgress(errors, excelOutputNotes);
                        completed++;
                        SetProgress(completed, totalSteps, "STEP • " + SelectedProcessingSpeedText(), row.PartCode);
                        ApplyInterPartDelay(processingSpeed, completed);

                    }
                }

                if (_excel.Checked)
                {
                    esc.ThrowIfRequested();
                    WaitIfPaused(esc);
                    SetProcessingRow(null);
                    _resume.ClearFailure("Excel", null, errors, excelOutputNotes, null);
                    _resume.SaveProgress(errors, excelOutputNotes);
                    _resume.Start("EXCEL", null);
                    SetProgress(completed, totalSteps, "Excel hazırlanıyor", excelFileName);
                    string x = Path.Combine(root, excelFileName);
                    try
                    {
                        string templatePath = FindTemplate("TALASLI_IMALAT_FORMU.xlsx");
                        _template.Text = templatePath;
                        ExcelManufacturingWriters.CreateMachining(templatePath, x, _info, _rows.Where(r => r.IsRoot).ToList());
                        excelCreated = OutputFileValidator.IsUsable(x);
                        if (excelCreated) processedRows.AddRange(_rows.Where(r => r != null && r.IsRoot));
                        if (!excelCreated) errors.Add("Excel: çıktı dosyası oluşturuldu ancak doğrulanamadı.");
                    }
                    catch (Exception ex)
                    {
                        errors.Add("Excel: " + ex.Message);
                        Logger.Error("Talaşlı imalat Excel oluşturma başarısız", ex);
                    }
                    completed++;
                    SetProgress(completed, totalSteps, excelCreated ? "Excel tamamlandı" : "Excel başarısız", excelFileName);
                }

                if (excelCreated)
                {
                    try
                    {
                        string excelPath = Path.Combine(root, excelFileName);
                        ExcelManufacturingWriters.UpdateMachiningOutputNotes(excelPath, _rows.Where(r => r.IsRoot).ToList(), excelOutputNotes);
                    }
                    catch (Exception ex)
                    {
                        errors.Add("Excel Açıklama: " + ex.Message);
                        Logger.Error("Talaşlı İmalat Excel Açıklama güncelleme başarısız", ex);
                    }
                }

                _resume.SaveProgress(errors, excelOutputNotes);
                _resume.Finish(errors.Count == 0);

                if (_hideProcessed != null && _hideProcessed.Checked && processedRows.Count > 0)
                    HideProcessedRows(processedRows);

                watch.Stop();
                bool anyOutput = excelCreated || pdfCount > 0 || stepCount > 0;
                if (errors.Count == 0)
                {
                    SetProgressComplete("Tamamlandı • Mod: " + SelectedProcessingSpeedText() + " • Excel: " + (excelCreated ? "1" : "0") + " • PDF: " + pdfCount + " • STEP: " + stepCount + " • " + root);
                }
                else if (anyOutput)
                {
                    SetWarningStatus("Kısmi tamamlandı • Excel: " + (excelCreated ? "1" : "0") + " • PDF: " + pdfCount + " • STEP: " + stepCount + " • Hata/Uyarı: " + errors.Count);
                }
                else
                {
                    SetFailureStatus("Çıktı oluşturulamadı • Hata: " + errors.Count);
                }

                try { _telegramControl?.Complete(errors.Count == 0 ? "Tamamlandı" : (anyOutput ? "Kısmen tamamlandı" : "Başarısız"), watch.Elapsed); } catch { }
                SendMachiningTelegramCompleted(telegramSettings, telegramOperationName, _rows.Count, stepRows.Count, _pdf.Checked, pdfCount, _excel.Checked, excelCreated, stepCount, SelectedProcessingSpeedText(), errors, watch.Elapsed);
                ShowResultAndOfferFolder(BuildResultMessage(root, excelCreated, pdfCount, stepCount, errors), root, errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                SetWarningStatus("İşlem ESC ile iptal edildi.");
                try { _telegramControl?.Complete("İptal edildi", watch.Elapsed); } catch { }
                if (telegramStarted) SendPrepTelegramCancelled(telegramSettings, telegramOperationName, watch.Elapsed);
            }
            catch (Exception ex)
            {
                watch.Stop();
                Logger.Error("Talaşlı imalat dosyası çıktı oluşturma başarısız", ex);
                MessageBox.Show(ex.Message, "SrcCadWorks");
                SetFailureStatus("Çıktı oluşturma hatası");
                try { _telegramControl?.Complete("Hata", watch.Elapsed); } catch { }
                SendPrepTelegramFailed(telegramSettings, telegramOperationName, ex, watch.Elapsed);
            }
            finally
            {
                _resume.Suspend();
                SetProcessingRow(null);
                try { scope?.Dispose(); } catch { }
                try { guard?.Dispose(); } catch { }
                try { esc.Dispose(); } catch { }
                _busy = false;
                _productionRunning = false;
                _pauseRequested = false;
                _create.Enabled = true;
                SetSpeedSelectorEnabled(true);
                RefreshPreview();
            }
        }


        private static string MachiningExcelKey(MachiningRow row)
        {
            if (row == null) return "";
            if (row.IsRoot || string.IsNullOrWhiteSpace(row.RootPartCode)) return (row.PartCode ?? "").Trim();
            return row.RootPartCode.Trim();
        }

        private static void AddExcelOutputNote(IDictionary<string, string> notes, string partCode, string note)
        {
            if (notes == null || string.IsNullOrWhiteSpace(partCode) || string.IsNullOrWhiteSpace(note)) return;
            string key = partCode.Trim();
            string value = note.Trim();
            string existing;
            if (!notes.TryGetValue(key, out existing) || string.IsNullOrWhiteSpace(existing))
            {
                notes[key] = value;
                return;
            }
            if (existing.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return;
            notes[key] = existing.TrimEnd() + " | " + value;
        }

        private List<MachiningRow> CommitAndGetSelectedStepRows()
        {
            var selected = new List<MachiningRow>();
            try
            {
                if (_grid != null)
                {
                    if (_grid.IsCurrentCellDirty)
                        _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    _grid.EndEdit();
                    try
                    {
                        CurrencyManager manager = BindingContext[_grid.DataSource] as CurrencyManager;
                        manager?.EndCurrentEdit();
                    }
                    catch { }

                    int stepColumn = -1;
                    foreach (DataGridViewColumn column in _grid.Columns)
                    {
                        if (string.Equals(column.DataPropertyName, "StepSelected", StringComparison.OrdinalIgnoreCase))
                        {
                            stepColumn = column.Index;
                            break;
                        }
                    }

                    if (stepColumn >= 0)
                    {
                        foreach (DataGridViewRow gridRow in _grid.Rows)
                        {
                            var row = gridRow.DataBoundItem as MachiningRow;
                            if (row == null) continue;
                            bool isChecked = false;
                            try
                            {
                                object value = gridRow.Cells[stepColumn].Value;
                                if (value != null && value != DBNull.Value) isChecked = Convert.ToBoolean(value);
                            }
                            catch { isChecked = row.StepSelected; }
                            row.StepSelected = isChecked;
                            if (isChecked) selected.Add(row);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("STEP seçimleri grid üzerinden okunamadı; model değerleri kullanılacak. " + ex.Message);
            }

            // Grid varsa seçim yalnız ekrandaki checkbox değerlerinden gelir. Seçili satır
            // yoksa bu bilinçli olarak 0 STEP demektir; eski model değerlerine geri dönülmez.
            if (_grid == null) selected = _rows.Where(r => r.StepSelected).ToList();

            Logger.Info("Talaşlı STEP seçimi: " + selected.Count + " satır -> " + string.Join(", ", selected.Select(r => r.PartCode).Where(x => !string.IsNullOrWhiteSpace(x))));
            return selected;
        }

        private string BuildMachiningTelegramOperationName()
        {
            string name = (_project == null ? "" : (_project.Text ?? "").Trim());
            if (string.IsNullOrWhiteSpace(name)) name = (_fileName == null ? "" : (_fileName.Text ?? "").Trim());
            if (string.IsNullOrWhiteSpace(name)) name = (_fileCode == null ? "" : (_fileCode.Text ?? "").Trim());
            return string.IsNullOrWhiteSpace(name) ? "Talaşlı İmalat Dosyası Hazırla" : "Talaşlı İmalat Dosyası Hazırla • " + name;
        }

        private static void SendMachiningTelegramStarted(TelegramSettings settings, string operationName, int pdfRows, int stepSelected, bool pdf, bool excel, string speed)
        {
            if (settings == null || !settings.IsConfigured || !settings.SendStartMessage) return;
            string text = "⚙️ " + operationName + " – İşlem Başladı\n\n" +
                "📄 PDF satırı: " + (pdf ? pdfRows.ToString() : "seçilmedi") + "\n" +
                "📦 STEP seçili: " + stepSelected + "\n" +
                "📊 Excel: " + (excel ? "seçili" : "seçilmedi") + "\n" +
                "🚀 İşlem modu: " + speed;
            QueuePrepTelegram(settings, text, "Talaşlı");
        }

        private static void SendMachiningTelegramCompleted(TelegramSettings settings, string operationName, int pdfRows, int stepSelected,
            bool pdfSelected, int pdfCreated, bool excelSelected, bool excelCreated, int stepCreated, string speed,
            IList<string> errors, TimeSpan elapsed)
        {
            if (settings == null || !settings.IsConfigured) return;
            int errorCount = errors == null ? 0 : errors.Count;
            string status = errorCount == 0 ? "✅ Tamamlandı" : "⚠️ Kısmen tamamlandı";
            string text = "⚙️ " + operationName + " – İşlem Tamamlandı\n\n" +
                "📄 PDF: " + (pdfSelected ? (pdfCreated + "/" + pdfRows + " oluşturuldu") : "seçilmedi") + "\n" +
                "📦 STEP: " + stepCreated + "/" + stepSelected + " oluşturuldu\n" +
                "📊 Excel: " + (excelSelected ? (excelCreated ? "oluşturuldu" : "oluşturulamadı") : "seçilmedi") + "\n" +
                "🚀 İşlem modu: " + speed + "\n" +
                "❗ Hata/Uyarı: " + errorCount + "\n" +
                "⏱️ Toplam süre: " + FormatPrepDuration(elapsed) + "\n\nDurum: " + status;
            if (errorCount > 0)
                text += "\n\nİlk hatalar:\n- " + string.Join("\n- ", errors.Where(x => !string.IsNullOrWhiteSpace(x)).Take(5));
            TelegramLastReportStore.Save(text);
            if (settings.SendCompletionMessage) QueuePrepTelegram(settings, text, "Talaşlı");
        }

        private static void SendPrepTelegramCancelled(TelegramSettings settings, string operationName, TimeSpan elapsed)
        {
            if (settings == null || !settings.IsConfigured) return;
            string text = "⏹️ " + operationName + " – İşlem İptal Edildi\n\n⏱️ Geçen süre: " + FormatPrepDuration(elapsed);
            TelegramLastReportStore.Save(text);
            QueuePrepTelegram(settings, text, "Dosya Hazırla");
        }

        private static void SendPrepTelegramFailed(TelegramSettings settings, string operationName, Exception error, TimeSpan elapsed)
        {
            if (settings == null || !settings.IsConfigured) return;
            string text = "❌ " + operationName + " – İşlem Hatası\n\n⚠️ Hata: " + (error == null ? "Bilinmeyen hata" : error.Message) + "\n⏱️ Geçen süre: " + FormatPrepDuration(elapsed);
            TelegramLastReportStore.Save(text);
            QueuePrepTelegram(settings, text, "Dosya Hazırla");
        }

        private static void QueuePrepTelegram(TelegramSettings settings, string text, string logPrefix)
        {
            if (settings == null || !settings.IsConfigured || string.IsNullOrWhiteSpace(text)) return;
            TelegramSettings copy = settings.Clone();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { new TelegramBotClient(copy).SendMessage(text, copy.EnableStatusCommand); }
                catch (Exception ex) { Logger.Error(logPrefix + " Telegram bildirimi gönderilemedi", ex); }
            });
        }

        private void ShowResultAndOfferFolder(string message, string folder, MessageBoxIcon icon)
        {
            string full = message + "\n\nÇıktı klasörünü açmak ister misiniz?";
            DialogResult answer = MessageBox.Show(this, full, "SrcCadWorks", MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", folder); }
            catch (Exception ex) { Logger.Info("Çıktı klasörü açılamadı: " + ex.Message); }
        }

        private static string FormatPrepDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return value.ToString(@"hh\:mm\:ss");
            return value.ToString(@"mm\:ss");
        }

        private void ApplyInfo()
        {
            if (_info == null) return;
            _project.Text = _info.ProjectName ?? "";
            _fileName.Text = _info.FileName ?? "";
            _fileCode.Text = _info.FileCode ?? "";
            _designer.Text = _info.Designer ?? "";
            _preparer.Text = _info.Preparer ?? "";
            _approver.Text = _info.Approver ?? "";
            _reason.Text = _info.RequestReason ?? "YENİ ÜRETİM";
            _note.Text = _info.Note ?? "";

            if (string.IsNullOrWhiteSpace(_output.Text))
            {
                string basePath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
                try
                {
                    string p = (_swApp.ActiveDoc as ModelDoc2)?.GetPathName();
                    string d = Path.GetDirectoryName(p);
                    if (!string.IsNullOrWhiteSpace(d)) basePath = d;
                }
                catch { }
                _output.Text = EnsureProcessFolder(basePath, "TALAŞLI İMALAT");
            }

            // Üretim şablonu SrcCadWorks paketinden gelir; SOLIDWORKS/Program Files veya
            // önceki oturumdan kalan harici bir yol otomatik kullanılmaz.
            _template.Text = FindTemplate("TALASLI_IMALAT_FORMU.xlsx");
        }

        private void PullInfo()
        {
            if (_info == null) _info = new ProcessProjectInfo();
            _info.ProjectName = _project.Text;
            _info.FileName = _fileName.Text;
            _info.FileCode = _fileCode.Text;
            _info.Designer = _designer.Text;
            _info.Preparer = _preparer.Text;
            _info.Approver = _approver.Text;
            _info.RequestReason = _reason.Text;
            _info.Note = _note.Text;
        }

        private void BrowseOutput(object sender, EventArgs e)
        {
            IDisposable zOrderPause = null;
            try
            {
                zOrderPause = _ownerTracker?.Suspend();
                string selected = ParentIfAlreadyProcessFolder(_output.Text, "TALAŞLI İMALAT");
                string picked;
                bool cancelled;
                try
                {
                    if (ModernFolderPicker.TryPickFolder(Handle, selected, "TALAŞLI İMALAT klasörünün oluşturulacağı ana klasörü seçin", out picked, out cancelled))
                    {
                        _output.Text = EnsureProcessFolder(picked, "TALAŞLI İMALAT");
                        return;
                    }
                    if (cancelled) return;
                }
                catch (Exception ex)
                {
                    Logger.Info("Modern klasör seçici açılamadı; klasik seçici kullanılacak. " + ex.Message);
                }

                using (var d = new FolderBrowserDialog { Description = "TALAŞLI İMALAT klasörünün oluşturulacağı ana klasörü seçin", SelectedPath = selected })
                    if (d.ShowDialog(this) == DialogResult.OK) _output.Text = EnsureProcessFolder(d.SelectedPath, "TALAŞLI İMALAT");
            }
            finally { zOrderPause?.Dispose(); }
        }

        private void BrowseTemplate(object sender, EventArgs e)
        {
            IDisposable zOrderPause = null;
            try
            {
                zOrderPause = _ownerTracker?.Suspend();
                string defaultPath = FindTemplate("TALASLI_IMALAT_FORMU.xlsx");
                using (var d = new OpenFileDialog
                {
                    Title = "Talaşlı imalat Excel şablonunu seçin",
                    Filter = "Excel (*.xlsx)|*.xlsx|Tüm dosyalar (*.*)|*.*",
                    InitialDirectory = Directory.Exists(Path.GetDirectoryName(defaultPath)) ? Path.GetDirectoryName(defaultPath) : null,
                    FileName = File.Exists(_template.Text) ? Path.GetFileName(_template.Text) : Path.GetFileName(defaultPath)
                })
                    if (d.ShowDialog(this) == DialogResult.OK) _template.Text = d.FileName;
            }
            finally { zOrderPause?.Dispose(); }
        }

        private static void ApplyInterPartDelay(ProcessingSpeedMode speed, int completedCount)
        {
            int delay = speed == ProcessingSpeedMode.Safe ? 220 : (speed == ProcessingSpeedMode.Fast ? 0 : 60);
            if (delay > 0) System.Threading.Thread.Sleep(delay);

            if (speed == ProcessingSpeedMode.Safe && completedCount > 0 && completedCount % 8 == 0)
                System.Threading.Thread.Sleep(1200);
            else if (speed == ProcessingSpeedMode.Normal && completedCount > 0 && completedCount % 12 == 0)
                System.Threading.Thread.Sleep(600);

            Application.DoEvents();
        }

        private void SetProgress(int index, int total, string stage, string part)
        {
            total = Math.Max(1, total);
            index = Math.Max(0, Math.Min(total, index));
            int percent = (int)Math.Round(index * 100.0 / total);
            if (_progress != null) _progress.Value = Math.Max(0, Math.Min(100, percent));
            if (_progressPercent != null) { _progressPercent.Text = percent + "%"; _progressPercent.ForeColor = Accent; }
            if (_status != null)
            {
                _status.ForeColor = Accent;
                string suffix = string.IsNullOrWhiteSpace(part) ? "" : " • " + part;
                _status.Text = "İlerleme: " + index + "/" + total + " (" + percent + "%) • " + stage + suffix;
            }
            if (_productionRunning) { try { _telegramControl?.Update(index, total, stage, part); } catch { } }
            Application.DoEvents();
        }

        private void SetProgressComplete(string text)
        {
            if (_progress != null) _progress.Value = 100;
            if (_progressPercent != null) { _progressPercent.Text = "100%"; _progressPercent.ForeColor = Success; }
            if (_status != null) { _status.Text = text; _status.ForeColor = Success; }
            Application.DoEvents();
        }

        private void SetWarningStatus(string text)
        {
            if (_status != null) { _status.Text = text; _status.ForeColor = Accent; }
            Application.DoEvents();
        }

        private void SetFailureStatus(string text)
        {
            if (_status != null) { _status.Text = text; _status.ForeColor = Accent; }
            if (_progressPercent != null) _progressPercent.ForeColor = Accent;
            Application.DoEvents();
        }

        private static string BuildResultMessage(string root, bool excelCreated, int pdfCount, int stepCount, IList<string> errors)
        {
            string msg = "Talaşlı imalat dosyası sonucu\n\nKlasör: " + root + "\nExcel: " + (excelCreated ? "oluşturuldu" : "oluşturulmadı") + "\nPDF: " + pdfCount + "\nSTEP: " + stepCount + "\nHata/Uyarı: " + (errors == null ? 0 : errors.Count);
            if (errors != null && errors.Count > 0)
            {
                msg += "\n\nİlk hatalar:\n" + string.Join("\n", errors.Take(10).Select(x => "• " + x));
                if (errors.Count > 10) msg += "\n• ... (" + (errors.Count - 10) + " ek hata)";
            }
            return msg;
        }

        private void RequestTelegramPause()
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired) BeginInvoke(new Action(() => SetPauseRequested(true, true)));
                else SetPauseRequested(true, true);
            }
            catch { }
        }

        private void RequestTelegramResume()
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired) BeginInvoke(new Action(() => SetPauseRequested(false, true)));
                else SetPauseRequested(false, true);
            }
            catch { }
        }

        private void SetPauseRequested(bool pause, bool fromTelegram)
        {
            _pauseRequested = pause;
            if (_status != null && _productionRunning)
                _status.Text = pause
                    ? (fromTelegram ? "Telegram: " : "") + "Duraklatma istendi. Geçerli güvenli adım bitince işlem bekleyecek."
                    : (fromTelegram ? "Telegram: " : "") + "İşlem devam ediyor...";
        }

        private void WaitIfPaused(EscapeCancellationMonitor esc)
        {
            while (_pauseRequested)
            {
                esc?.ThrowIfRequested();
                if (_status != null) _status.Text = "DURAKLATILDI • Telegram Devam Et veya /devam ile işlem sürdürülebilir.";
                Application.DoEvents();
                System.Threading.Thread.Sleep(120);
            }
        }

        private void HideProcessedRows(IEnumerable<MachiningRow> rows)
        {
            try
            {
                if (_visibilityTracker == null) _visibilityTracker = new AssemblyVisibilityTracker(_swApp);
                var proxies = (rows ?? Enumerable.Empty<MachiningRow>())
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.SourcePath))
                    .GroupBy(r => (r.SourcePath ?? "") + "|" + (r.Configuration ?? ""), StringComparer.OrdinalIgnoreCase)
                    .Select(g => { var r = g.First(); return new LaserPartRow { SourcePath = r.SourcePath, Configuration = r.Configuration, PartCode = r.PartCode }; })
                    .ToList();
                if (proxies.Count == 0) return;
                SetWarningStatus("İşlenen parçalar montajda gizleniyor...");
                _visibilityTracker.HideOccurrencesBatch(proxies, (done, total) =>
                {
                    if (_status != null) _status.Text = "İşlenen parçalar gizleniyor: " + done + "/" + total;
                    Application.DoEvents();
                });
                _visibilityTracker.Redraw();
            }
            catch (Exception ex) { Logger.Info("Talaşlı işlenenleri gizleme uyarısı: " + ex.Message); }
        }

        private void RestoreHiddenComponents(bool closing)
        {
            try
            {
                if (_visibilityTracker == null) return;
                int restored = _visibilityTracker.RestoreAll();
                _visibilityTracker.Dispose();
                _visibilityTracker = null;
                if (!closing && restored > 0) SetWarningStatus("Önceki işlemde gizlenen bileşenler geri getirildi: " + restored);
            }
            catch { _visibilityTracker = null; }
        }

        private string FindLogo()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string main = Path.Combine(baseDir, "assets", "SrcCadWorks_Logo_Main.png");
            if (File.Exists(main)) return main;
            string localMain = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "assets", "SrcCadWorks_Logo_Main.png");
            if (File.Exists(localMain)) return localMain;
            string packaged = Path.Combine(baseDir, "assets", "SrcCadWorks_Logo.png");
            if (File.Exists(packaged)) return packaged;
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "assets", "SrcCadWorks_Logo.png");
        }

        private static string ProcessFolder(string basePath, string name)
        {
            if (string.IsNullOrWhiteSpace(basePath)) basePath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
            return EnsureProcessFolder(basePath, name);
        }

        private static string EnsureProcessFolder(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            string clean = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(clean);
            bool numberedProcessFolder = leaf.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(")", StringComparison.Ordinal);
            if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase) || numberedProcessFolder) return clean;
            return Path.Combine(clean, name);
        }

        private static string ParentIfAlreadyProcessFolder(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            string clean = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(clean);
            bool numberedProcessFolder = leaf.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(")", StringComparison.Ordinal);
            if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase) || numberedProcessFolder)
                return Path.GetDirectoryName(clean) ?? clean;
            return clean;
        }

        private static string FindTemplate(string fileName)
        {
            string dll = typeof(MachiningPrepForm).Assembly.Location;
            string baseDir = Path.GetDirectoryName(dll) ?? "";
            string packaged = Path.Combine(baseDir, "templates", fileName);
            if (File.Exists(packaged)) return packaged;
            string local = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "templates", fileName);
            if (File.Exists(local)) return local;
            return packaged;
        }

        private static bool IsSolidWorksInstallTemplate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.IndexOf("\\SOLIDWORKS Corp\\SOLIDWORKS\\templates\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("\\Program Files\\SOLIDWORKS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Safe(string value)
        {
            string s = (value ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(s) ? "TALAŞLI İMALAT" : s;
        }

        private static TableLayoutPanel FieldTable(int rows)
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows, BackColor = Surface };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) p.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            return p;
        }

        private static TableLayoutPanel RightTable(float[] heights)
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = heights.Length, BackColor = Surface };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float h in heights) p.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            return p;
        }

        private static NumericUpDown NumericField(TableLayoutPanel p, string label, int row, int minimum, int maximum)
        {
            p.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, AutoEllipsis = true }, 0, row);
            var n = new NumericUpDown
            {
                Dock = DockStyle.Fill, Minimum = minimum, Maximum = maximum, Value = minimum,
                BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 5, 4, 4), TextAlign = HorizontalAlignment.Left, ThousandsSeparator = true
            };
            p.Controls.Add(n, 1, row);
            return n;
        }

        private readonly Dictionary<MachiningRow, int> _manualQuantities = new Dictionary<MachiningRow, int>();
        private MachiningRow _quantityEditRow;
        private int _quantityBeforeEdit;

        private void ApplyAssemblyMultiplier()
        {
            foreach (var row in _rows)
            {
                int baseQuantity;
                if (!_baseQuantities.TryGetValue(row, out baseQuantity))
                {
                    baseQuantity = Math.Max(0, row.Quantity);
                    _baseQuantities[row] = baseQuantity;
                }
                ApplyAssemblyMultiplierToRow(row, baseQuantity);
            }
            try { _grid?.Refresh(); } catch { }
            RefreshPreview();
        }

        private void ApplyAssemblyMultiplierToRow(MachiningRow row, int baseQuantity)
        {
            if (row == null) return;
            int manualQuantity;
            if (_manualQuantities.TryGetValue(row, out manualQuantity))
            {
                row.Quantity = manualQuantity;
                return;
            }
            int factor = _assemblyQuantity == null ? 1 : Math.Max(1, (int)_assemblyQuantity.Value);
            long quantity = (long)Math.Max(0, baseQuantity) * factor;
            row.Quantity = quantity > int.MaxValue ? int.MaxValue : (int)quantity;
        }

        private static TextBox Field(TableLayoutPanel p, string label, int row)
        {
            p.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, AutoEllipsis = true }, 0, row);
            var t = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 4, 4) };
            p.Controls.Add(t, 1, row);
            return t;
        }

        private static void BrowseField(TableLayoutPanel p, string label, int row, out TextBox textBox, EventHandler handler)
        {
            p.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, AutoEllipsis = true }, 0, row);
            var q = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            q.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            q.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            textBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 4, 4) };
            var b = new Button { Text = "Gözat", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 0, 4) };
            b.Click += handler;
            q.Controls.Add(textBox, 0, 0);
            q.Controls.Add(b, 1, 0);
            p.Controls.Add(q, 1, row);
        }

        private static void ApplyTextRenderingQuality(Control root)
        {
            if (root == null) return;
            var label = root as Label;
            if (label != null) label.UseCompatibleTextRendering = false;
            var button = root as ButtonBase;
            if (button != null) button.UseCompatibleTextRendering = false;
            foreach (Control child in root.Controls) ApplyTextRenderingQuality(child);
        }

        private static Button MakeButton(string text, bool accent)
        {
            var b = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Accent : Surface,
                ForeColor = accent ? Color.White : Color.FromArgb(31, 36, 42),
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold)
            };
            b.FlatAppearance.BorderColor = accent ? Accent : Border;
            return b;
        }
    }
}
