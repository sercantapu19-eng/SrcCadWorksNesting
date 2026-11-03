using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    internal sealed class WeldPrepForm : Form
    {
        private ResumeSession _resume;
        private readonly SldWorks _swApp;
        private readonly BindingList<WeldFormRow> _rows = new BindingList<WeldFormRow>();
        private readonly List<WeldFormRow> _originalRows = new List<WeldFormRow>();
        private readonly Dictionary<string, TextBox> _processCodes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private DataGridView _grid;
        private EDrawingsPreviewControl _preview;
        private FlowLayoutPanel _codePanel;
        private SplitContainer _split;
        private TableLayoutPanel _root;
        private Control _topFieldsPanel;
        private Control _statusPanel;
        private Control _bottomPanel;
        private bool _previewExpanded;
        private readonly float[] _previewSavedRowHeights = new float[3];
        private TextBox _project, _fileName, _preparer, _approver, _note, _output, _template;
        private NumericUpDown _assemblyQuantity;
        private readonly Dictionary<WeldFormRow, int> _baseQuantities = new Dictionary<WeldFormRow, int>();
        private CheckBox _pdf, _excel, _keepOnTop, _hideProcessed;
        private AssemblyVisibilityTracker _visibilityTracker;
        private int _hiddenProcessedRows;
        private RadioButton _speedSafe, _speedNormal, _speedFast;
        private Label _status;
        private Label _timing;
        private WeldFormRow _processingRow;
        private readonly Dictionary<string, bool> _drawingExists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private ProgressBar _progress;
        private Button _create;
        private ComboBox _sortField;
        private WeldProjectInfo _info;
        private string _sortProperty = "";
        private bool _sortAscending = true;
        private bool _splitConfigured;
        private SolidWorksOwnedWindow.Tracker _ownedWindowTracker;

        private static readonly Color Accent = Color.FromArgb(202, 28, 36);
        private static readonly Color Border = Color.FromArgb(205, 211, 218);
        private static readonly Color Muted = Color.FromArgb(94, 103, 113);
        private static readonly Color Surface = Color.White;
        private static readonly Color SurfaceAlt = Color.FromArgb(249, 250, 252);

        internal bool KeepAboveSolidWorksRequested => _keepOnTop != null && _keepOnTop.Checked;

        public WeldPrepForm(SldWorks swApp)
        {
            _swApp = swApp;
            Text = "SrcCadWorks — Kaynak Dosyası Hazırla " + AppVersion.Display;
            SrcCadWorks.Services.BrandIcon.Apply(this);
            Width = 1600;
            Height = 940;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1240, 760);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(245, 247, 250);
            ForeColor = Color.FromArgb(31, 36, 42);
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildUi();
            _resume = new ResumeSession(this, _swApp, "Weld", () => _rows, () => !Enabled, Scan, CreatePackage);
            ApplyTextRenderingQuality(this);
            Shown += (s, e) =>
            {
                WindowState = FormWindowState.Maximized;
                BeginInvoke(new Action(() =>
                {
                    if (_keepOnTop != null)
                    {
                        SolidWorksOwnedWindow.Apply(this, _swApp, _keepOnTop.Checked, true);
                        _ownedWindowTracker = SolidWorksOwnedWindow.Track(this, _swApp, _keepOnTop.Checked);
                    }
                    ConfigureSplitter(true);
                    Scan();
                }));
            };
            Resize += (s, e) => ConfigureSplitter(false);
            FormClosing += (s, e) =>
            {
                try { _ownedWindowTracker?.Dispose(); } catch { }
                _ownedWindowTracker = null;
                try { _preview?.Shutdown(); } catch { }
                try { _visibilityTracker?.Dispose(); } catch { }
                _visibilityTracker = null;
            };
        }

        private void BuildUi()
        {
            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(14),
                BackColor = BackColor,
                AutoScroll = true
            };
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 294));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            Controls.Add(_root);

            _root.Controls.Add(BuildHeader(), 0, 0);
            _topFieldsPanel = BuildTopFields();
            _root.Controls.Add(_topFieldsPanel, 0, 1);
            _statusPanel = BuildStatusBar();
            _root.Controls.Add(_statusPanel, 0, 2);
            _root.Controls.Add(BuildMainArea(), 0, 3);
            _bottomPanel = BuildBottom();
            _root.Controls.Add(_bottomPanel, 0, 4);
        }

        private Control BuildHeader()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(18, 10, 18, 8) };
            var title = new Label
            {
                Text = "SrcCadWorks",
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 36, 42),
                Dock = DockStyle.Top,
                Height = 36,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var sub = new Label
            {
                Text = "KAYNAK DOSYASI HAZIRLA  •  KAYNAK PROSES FORMU",
                ForeColor = Muted,
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = false
            };
            var line = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = Accent };
            p.Controls.Add(line);
            p.Controls.Add(sub);
            p.Controls.Add(title);
            return p;
        }

        private Control BuildTopFields()
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Surface,
                Padding = new Padding(12, 10, 12, 10),
                AutoScroll = true
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));

            // Lazer Hazırla ile aynı akış: kullanıcıya ait form alanları solda,
            // Çıktı Klasörü de Not satırının hemen altında yer alır.
            // 36 px satır yüksekliği + 270 px üst alan, %125/%150 DPI'da dahi
            // yazıların ve Gözat düğmesinin birbirine girmemesi için yeterli pay bırakır.
            var left = CreateFieldsTable(7, 36);
            _project = AddField(left, "Proje Adı", 0);
            _fileName = AddField(left, "Dosya Adı", 1);
            _assemblyQuantity = AddNumericField(left, "Montaj Adedi", 2, 1, 9999);
            _assemblyQuantity.ValueChanged += (s, e) => ApplyAssemblyMultiplier();
            _preparer = AddField(left, "Hazırlayan", 3);
            _approver = AddField(left, "Onaylayan", 4);
            _note = AddField(left, "Not", 5);
            AddBrowseField(left, "Çıktı Klasörü", 6, out _output, false, BrowseOutput);
            outer.Controls.Add(left, 0, 0);

            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12, 0, 0, 0),
                BackColor = Surface
            };
            // Sağ tarafta yalnız proses kodları, şablon, çıktı seçenekleri ve hız kalır.
            // Çıktı Klasörü soldaki Not alanının altına taşındı.
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            _codePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 3, 0, 2),
                BackColor = Surface
            };
            right.Controls.Add(_codePanel, 0, 0);

            var templatePanel = BuildBrowseRow("Kaynak Şablonu", out _template, true, BrowseTemplate);
            right.Controls.Add(templatePanel, 0, 1);

            var opts = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 3, 0, 0),
                BackColor = Surface
            };
            _pdf = new CheckBox { Text = "Kaynak PDF", Checked = true, AutoSize = true, Margin = new Padding(0, 2, 22, 0) };
            _excel = new CheckBox { Text = "Kaynak Excel", Checked = true, AutoSize = true, Margin = new Padding(0, 2, 22, 0) };
            _hideProcessed = new CheckBox { Text = "İşlenenleri gizle", Checked = false, AutoSize = true, Margin = new Padding(0, 2, 12, 0) };
            opts.Controls.Add(_pdf);
            opts.Controls.Add(_excel);
            opts.Controls.Add(_hideProcessed);
            right.Controls.Add(opts, 0, 2);
            right.Controls.Add(BuildSpeedRow(), 0, 3);
            outer.Controls.Add(right, 1, 0);
            return outer;
        }

        private Control BuildSpeedRow()
        {
            // İşlem hızı alanı Lazer Hazırla ile aynı ölçekte kompakt tutulur.
            // Dock=Fill kullanan büyük RadioButton hücreleri yüksek DPI'da gereksiz
            // büyüyebildiği için sabit yükseklikli yatay panel kullanılır.
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                BackColor = Surface,
                Margin = Padding.Empty,
                Padding = new Padding(0, 2, 0, 0)
            };

            row.Controls.Add(new Label
            {
                Text = "İşlem Hızı",
                Width = 102,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                AutoEllipsis = false,
                Margin = Padding.Empty
            });

            _speedSafe = MakeSpeedChoice("Güvenli", ProcessingSpeedMode.Safe);
            _speedNormal = MakeSpeedChoice("Normal", ProcessingSpeedMode.Normal);
            _speedFast = MakeSpeedChoice("Hızlı", ProcessingSpeedMode.Fast);
            row.Controls.Add(_speedSafe);
            row.Controls.Add(_speedNormal);
            row.Controls.Add(_speedFast);
            SetSpeedChoice(ProcessingSpeedMode.Normal);

            var tip = new ToolTip();
            tip.SetToolTip(_speedSafe, "Kaynak PDF işlemlerinde daha uzun beklemeler ve düzenli nefes payı. Büyük işlerde en stabil seçenek.");
            tip.SetToolTip(_speedNormal, "Önerilen dengeli çalışma modu.");
            tip.SetToolTip(_speedFast, "Kaynak PDF işlemlerinde en kısa beklemelerle hızlı çalışma modu.");
            return row;
        }

        private RadioButton MakeSpeedChoice(string text, ProcessingSpeedMode mode)
        {
            var button = new RadioButton
            {
                Text = text,
                Appearance = Appearance.Button,
                AutoCheck = true,
                Size = new Size(104, 30),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                BackColor = SurfaceAlt,
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
                button.BackColor = button.Checked ? Accent : SurfaceAlt;
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

        private static TableLayoutPanel CreateFieldsTable(int rows, int rowHeight)
        {
            // Alan satırlarının sonuncusunun, panelde kalan tüm yüksekliği kaplamasını engelle.
            // Son yüzde satırı yalnız boş alanı emer; böylece TextBox/Gözat gibi kontroller
            // DPI veya tam ekran çözünürlüğünde gereksiz yere uzamaz.
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows + 1,
                BackColor = Surface,
                Padding = new Padding(0, 0, 10, 0)
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) p.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            return p;
        }

        private Control BuildBrowseRow(string label, out TextBox text, bool readOnly, EventHandler browseHandler)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Surface, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            panel.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                AutoEllipsis = false
            }, 0, 0);
            text = new TextBox { Dock = DockStyle.Fill, ReadOnly = readOnly, Margin = new Padding(3, 4, 6, 3) };
            panel.Controls.Add(text, 1, 0);
            var browse = MakeButton("Gözat", false);
            browse.Margin = new Padding(3, 2, 0, 2);
            browse.Click += browseHandler;
            panel.Controls.Add(browse, 2, 0);
            return panel;
        }

        private void AddBrowseField(TableLayoutPanel p, string label, int row, out TextBox text, bool readOnly, EventHandler browseHandler)
        {
            p.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                AutoEllipsis = false
            }, 0, row);

            var field = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Surface
            };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));

            text = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = readOnly,
                Margin = new Padding(3, 5, 6, 4)
            };
            field.Controls.Add(text, 0, 0);

            var browse = MakeButton("Gözat", false);
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(3, 2, 0, 2);
            browse.Click += browseHandler;
            field.Controls.Add(browse, 1, 0);

            p.Controls.Add(field, 1, row);
        }

        private NumericUpDown AddNumericField(TableLayoutPanel p, string label, int row, int minimum, int maximum)
        {
            p.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                AutoEllipsis = false
            }, 0, row);
            var n = new NumericUpDown
            {
                Dock = DockStyle.Fill, Minimum = minimum, Maximum = maximum, Value = minimum,
                Margin = new Padding(3, 5, 8, 5), TextAlign = HorizontalAlignment.Left,
                ThousandsSeparator = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White
            };
            p.Controls.Add(n, 1, row);
            return n;
        }

        private void ApplyAssemblyMultiplier()
        {
            int factor = _assemblyQuantity == null ? 1 : Math.Max(1, (int)_assemblyQuantity.Value);
            foreach (var row in _originalRows)
            {
                int baseQuantity;
                if (!_baseQuantities.TryGetValue(row, out baseQuantity))
                {
                    baseQuantity = Math.Max(0, row.Quantity);
                    _baseQuantities[row] = baseQuantity;
                }
                // Montaj Adedi yalnızca kaynak dosyasının toplam üretim adedini değiştirir.
                // Alt ürünler kaynak başına reçete miktarıdır; montaj adediyle çarpılmaz.
                if (row.IsTopLevelWeld)
                {
                    long quantity = (long)baseQuantity * factor;
                    row.Quantity = quantity > int.MaxValue ? int.MaxValue : (int)quantity;
                }
                else
                {
                    row.Quantity = baseQuantity;
                }
            }
            try { _grid?.Refresh(); } catch { }
        }

        private TextBox AddField(TableLayoutPanel p, string label, int row)
        {
            p.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                AutoEllipsis = false
            }, 0, row);
            var t = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 6, 8, 5) };
            p.Controls.Add(t, 1, row);
            return t;
        }

        private Control BuildStatusBar()
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(0, 7, 0, 5),
                BackColor = SurfaceAlt
            };
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            _status = new Label
            {
                Text = "Kaynak montajları taranacak...",
                Dock = DockStyle.Fill,
                ForeColor = Accent,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0)
            };
            _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100, Value = 0 };
            p.Controls.Add(_status, 0, 0);
            _timing = new Label { Dock = DockStyle.Fill, ForeColor = Accent,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0),
                Text = "Tahmini kalan: hesaplanıyor..." };
            p.Controls.Add(_timing, 0, 1);
            p.Controls.Add(_progress, 0, 2);
            return p;
        }

        private Control BuildMainArea()
        {
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Border,
                SplitterWidth = 5,
                FixedPanel = FixedPanel.Panel2
            };
            // Panel minimumlarını burada ayarlamıyoruz. SplitContainer henüz gerçek
            // genişliğini almadan Panel1MinSize/Panel2MinSize vermek WinForms'ta
            // "SplitterDistance must be between..." hatasına yol açabiliyor.
            // Güvenli sınırlar ConfigureSplitter içinde gerçek ClientSize belli olduktan
            // sonra yalnız SplitterDistance üzerinden uygulanır.
            _split.Panel1.BackColor = Surface;
            _split.Panel2.BackColor = SurfaceAlt;

            // V1.12.10: ayrı sıralama çubuğu kaldırılmış olarak korunur. Excel mantığında sıralama
            // doğrudan sütun başlığındaki ▾ menüsünden yapılır.
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Surface };

            _grid = new KeyboardDataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = true,
                RowHeadersVisible = false,
                BackgroundColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 34,
                RowTemplate = { Height = 28 },
                EnableHeadersVisualStyles = false
            };
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            _grid.DefaultCellStyle.ForeColor = Color.FromArgb(31, 36, 42);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(31, 36, 42);
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 247, 250);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(245, 247, 250);
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(31, 36, 42);
            AddGridColumn("Sequence", "Sıra", 70);
            AddGridColumn("PartCode", "Parça/Montaj Kodu", 175);
            AddGridColumn("ProductDescription", "Ürün Tanımı", 235);
            AddGridColumn("Quantity", "Adet", 70);
            AddGridColumn("MassText", "Kütle", 95);
            AddGridColumn("ColorCode", "Renk Kodu", 125);
            AddGridColumn("OperationType", "İşlem Tipi", 125);
            AddGridColumn("FileCode", "Dosya Kodu", 185);
            AddGridColumn("DrawingStatus", "Teknik Resim", 110);
            _grid.Columns["DrawingStatus"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["DrawingStatus"].DefaultCellStyle.Font = new Font(_grid.DefaultCellStyle.Font, FontStyle.Bold);
            _grid.DataSource = _rows;
            _grid.CellFormatting += GridCellFormatting;
            _grid.SelectionChanged += (s, e) => RefreshPreview();
            ExcelHeaderMenus.Attach(_grid, property => !string.IsNullOrWhiteSpace(property),
                property => string.Equals(property, _sortProperty, StringComparison.OrdinalIgnoreCase)
                    ? (_sortAscending ? 1 : -1) : 0);
            _grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            left.Controls.Add(_grid);
            _split.Panel1.Controls.Add(left);

            _preview = new EDrawingsPreviewControl { Dock = DockStyle.Fill };
            _preview.PreviewExpandRequested += (s, e) => SetPreviewExpanded(true);
            _preview.BackRequested += (s, e) => SetPreviewExpanded(false);
            _split.Panel2.Controls.Add(_preview);
            return _split;
        }

        private void SetPreviewExpanded(bool expanded)
        {
            if (_previewExpanded == expanded || _root == null || _split == null || _preview == null) return;
            _previewExpanded = expanded;
            try
            {
                _root.SuspendLayout();
                _split.SuspendLayout();

                if (expanded)
                {
                    // Lazer Hazırla ile aynı eDrawings büyük görünüm akışı:
                    // üst bilgi/ilerleme/alt komut alanlarını gizle, listeyi kapat ve
                    // eDrawings'i genişlet. EDrawingsPreviewControl bu durumda
                    // ← GERİ ve ÖLÇÜM düğmelerini görünür yapar.
                    _previewSavedRowHeights[0] = _root.RowStyles[_root.GetRow(_topFieldsPanel)].Height;
                    _root.RowStyles[_root.GetRow(_topFieldsPanel)].Height = 0;
                    _previewSavedRowHeights[1] = _root.RowStyles[_root.GetRow(_statusPanel)].Height;
                    _root.RowStyles[_root.GetRow(_statusPanel)].Height = 0;
                    _previewSavedRowHeights[2] = _root.RowStyles[_root.GetRow(_bottomPanel)].Height;
                    _root.RowStyles[_root.GetRow(_bottomPanel)].Height = 0;
                    if (_topFieldsPanel != null) _topFieldsPanel.Visible = false;
                    if (_statusPanel != null) _statusPanel.Visible = false;
                    if (_bottomPanel != null) _bottomPanel.Visible = false;

                    _split.Panel1Collapsed = true;
                    _split.IsSplitterFixed = true;
                    _split.Panel2.Padding = new Padding(12);
                }
                else
                {
                    _root.RowStyles[_root.GetRow(_topFieldsPanel)].Height = _previewSavedRowHeights[0];
                    _root.RowStyles[_root.GetRow(_statusPanel)].Height = _previewSavedRowHeights[1];
                    _root.RowStyles[_root.GetRow(_bottomPanel)].Height = _previewSavedRowHeights[2];
                    if (_topFieldsPanel != null) _topFieldsPanel.Visible = true;
                    if (_statusPanel != null) _statusPanel.Visible = true;
                    if (_bottomPanel != null) _bottomPanel.Visible = true;

                    _split.Panel1Collapsed = false;
                    _split.IsSplitterFixed = false;
                    _split.Panel2.Padding = Padding.Empty;
                    ConfigureSplitter(false);
                }

                _preview.SetExpanded(expanded);
            }
            finally
            {
                _split.ResumeLayout(true);
                _root.ResumeLayout(true);
                PerformLayout();
                BeginInvoke(new Action(() => _preview?.FitToWindow()));
            }
        }

        private Control BuildSortBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = SurfaceAlt, Padding = new Padding(8, 6, 8, 5) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.Controls.Add(new Label { Text = "Sıralama", Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

            _sortField = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 2, 8, 2) };
            _sortField.Items.AddRange(new object[]
            {
                new SortChoice("Montaj sırası", ""),
                new SortChoice("Sıra", "Sequence"),
                new SortChoice("Parça/Montaj Kodu", "PartCode"),
                new SortChoice("Ürün Tanımı", "ProductDescription"),
                new SortChoice("Adet", "Quantity"),
                new SortChoice("Kütle", "MassText"),
                new SortChoice("Renk Kodu", "ColorCode"),
                new SortChoice("İşlem Tipi", "OperationType"),
                new SortChoice("Dosya Kodu", "FileCode"),
                new SortChoice("Teknik Resim", "DrawingStatus")
            });
            _sortField.SelectedIndex = 0;
            bar.Controls.Add(_sortField, 1, 0);

            var asc = MakeButton("A → Z", false);
            asc.Click += (s, e) => ApplySortFromSelector(true);
            bar.Controls.Add(asc, 2, 0);
            var desc = MakeButton("Z → A", false);
            desc.Click += (s, e) => ApplySortFromSelector(false);
            bar.Controls.Add(desc, 3, 0);
            var reset = MakeButton("Montaj Sırası", false);
            reset.Click += (s, e) => ResetSort();
            bar.Controls.Add(reset, 4, 0);
            bar.Controls.Add(new Label
            {
                Text = "Sütun başlığına tıklayarak da artan/azalan sıralayabilirsiniz.",
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(10, 0, 0, 0)
            }, 5, 0);
            return bar;
        }

        private void AddGridColumn(string property, string header, int width)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = property,
                DataPropertyName = property,
                HeaderText = header,
                Width = width,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Programmatic
            });
        }

        private Control BuildBottom()
        {
            var bottom = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(10, 13, 10, 11) };
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 430, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Surface };
            var rescan = MakeButton("Yeniden Tara", false);
            rescan.Width = 120;
            rescan.Margin = new Padding(0, 0, 12, 0);
            rescan.Click += (s, e) => Scan();
            _keepOnTop = new CheckBox { Text = "Pencereyi üstte tut", AutoSize = true, Margin = new Padding(4, 9, 0, 0) };
            _keepOnTop.CheckedChanged += (s, e) =>
            {
                SolidWorksOwnedWindow.Apply(this, _swApp, _keepOnTop.Checked, true);
                if (_ownedWindowTracker != null) _ownedWindowTracker.SetEnabled(_keepOnTop.Checked);
            };
            left.Controls.Add(rescan);
            left.Controls.Add(_keepOnTop);

            var info = new Label
            {
                Text = "Kaynak ana satırı = ana montajdaki toplam adet  •  Alt satır = 1 kaynak grubu için yerel reçete adedi",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                AutoEllipsis = true,
                Padding = new Padding(12, 0, 12, 0)
            };
            var close = MakeButton("Kapat", false);
            close.Width = 110;
            close.Dock = DockStyle.Right;
            close.Click += (s, e) => Close();
            _create = MakeButton("KAYNAK DOSYASI OLUŞTUR", true);
            _create.Width = 270;
            _create.Dock = DockStyle.Right;
            _create.Click += (s, e) => CreatePackage();

            bottom.Controls.Add(info);
            bottom.Controls.Add(left);
            bottom.Controls.Add(close);
            bottom.Controls.Add(_create);
            return bottom;
        }

        private Button MakeButton(string text, bool accent)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(104, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Accent : Surface,
                ForeColor = accent ? Color.White : Color.FromArgb(31, 36, 42),
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = new Padding(3)
            };
            b.FlatAppearance.BorderColor = accent ? Accent : Border;
            return b;
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

        private void RestoreHiddenWeldComponents(bool showMessage)
        {
            if (_visibilityTracker == null)
            {
                if (showMessage) MessageBox.Show(this, "SrcCadWorks tarafından gizlenmiş kaynak grubu bulunmuyor.", "SrcCadWorks",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int restored = 0;
            try
            {
                restored = _visibilityTracker.RestoreAll();
                _visibilityTracker.Redraw();
            }
            catch (Exception ex)
            {
                Logger.Info("Kaynak gizlenenlerini geri getirme başarısız: " + ex.Message);
            }

            try { _visibilityTracker.Dispose(); } catch { }
            _visibilityTracker = null;
            _hiddenProcessedRows = 0;
            if (showMessage)
                MessageBox.Show(this, restored > 0 ? (restored + " bileşen yeniden görünür yapıldı.") : "Geri getirilecek bileşen bulunamadı.",
                    "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private LaserPartRow ToVisibilityRow(WeldFormRow row)
        {
            if (row == null) return null;
            return new LaserPartRow
            {
                PartCode = row.PartCode,
                SourcePath = row.SourcePath,
                Configuration = row.Configuration,
                OccurrenceSelectIds = row.OccurrenceSelectIds == null
                    ? new List<string>()
                    : row.OccurrenceSelectIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private void ConfigureSplitter(bool force)
        {
            if (_split == null || _split.IsDisposed || !_split.IsHandleCreated) return;
            try
            {
                int width = _split.ClientSize.Width;
                if (width < 980) return;
                int min = 560;
                int max = width - 390 - _split.SplitterWidth;
                if (max <= min) return;
                int desired = Math.Max(min, Math.Min(max, width - Math.Max(480, Math.Min(650, width * 38 / 100)) - _split.SplitterWidth));
                if (force || !_splitConfigured)
                {
                    _split.SplitterDistance = desired;
                    _splitConfigured = true;
                }
                else if (_split.SplitterDistance < min || _split.SplitterDistance > max)
                {
                    _split.SplitterDistance = Math.Max(min, Math.Min(max, _split.SplitterDistance));
                }
            }
            catch { }
        }

        private void Scan()
        {
            _resume.BeforeScan();
            ModalDialogGuard dialogGuard = null;
            SolidWorksAutomationScope automationScope = null;
            var escapeMonitor = new EscapeCancellationMonitor();
            try
            {
                Enabled = false;
                RestoreHiddenWeldComponents(false);

                // Kaynak taraması Lazer Hazırla ile aynı sessiz otomasyon korumasını kullanır.
                // Tarama sırasında SOLIDWORKS bir bileşeni salt-okunur açıp "Değişiklikler kaydedilsin mi?"
                // sorusunu gösterirse ModalDialogGuard otomatik HAYIR seçer. Geçici parça/drawing
                // sekmeleri de kullanıcı ekranına taşınmadan arka planda tutulur.
                dialogGuard = new ModalDialogGuard(null);
                automationScope = new SolidWorksAutomationScope(_swApp, true, true, false, false);

                _drawingExists.Clear();
                SetProcessingRow(null);
                _rows.Clear();
                _originalRows.Clear();
                _baseQuantities.Clear();
                _processCodes.Clear();
                _codePanel.Controls.Clear();
                _status.Text = "Ana montaj kaynak ağacı sessiz modda taranıyor...  ESC = DURDUR";
                SetProgress(0, 1, "Tarama hazırlanıyor", "", null);
                Application.DoEvents();
                _info = WeldProjectInfoFactory.FromActiveAssembly(_swApp);
                _project.Text = _info.ProjectName ?? "";
                _fileName.Text = _info.FileName ?? "";
                _preparer.Text = _info.Preparer ?? "";
                _approver.Text = _info.Approver ?? "";
                _note.Text = _info.Note ?? "";
                _template.Text = FindTemplate();
                _output.Text = DefaultOutputFolder();

                var watch = Stopwatch.StartNew();
                var scanner = new WeldAssemblyScanner(_swApp);
                var result = scanner.Scan((i, total, code) =>
                {
                    SetProgress(i, total, "Tarama • Sessiz • ESC=DURDUR", code, watch);
                    if ((i % 15) == 0) Application.DoEvents();
                }, () => escapeMonitor.IsCancellationRequested);
                watch.Stop();
                foreach (var row in result.Rows)
                {
                    _baseQuantities[row] = Math.Max(0, row.Quantity);
                    _rows.Add(row);
                    _originalRows.Add(row);
                }
                ApplyAssemblyMultiplier();
                BuildProcessCodeInputs(result.Rows);
                _resume.AfterScan();
                UpdateFileCodePreview();
                ResetSortGlyphs();
                if (_sortField != null) _sortField.SelectedIndex = 0;
                _sortProperty = "";
                _status.Text = "Tarama tamamlandı • Sessiz + hızlı cache • " + result.UniqueWeldCount + " kaynak kodu • " + result.Rows.Count + " Excel satırı • Cache: " + (result.MetadataCacheHits + result.LaserCacheHits) + "/" + (result.MetadataCacheHits + result.LaserCacheHits + result.MetadataCacheMisses) + " • Geçen: " + FormatDuration(watch.Elapsed);
                _progress.Value = 100;
                _timing.Text = "Tarama tamamlandı • Geçen: " + FormatDuration(watch.Elapsed);
                _create.Enabled = result.UniqueWeldCount > 0;
                if (result.Warnings.Count > 0)
                    MessageBox.Show(this, string.Join("\r\n", result.Warnings), "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _timing.Text = "Tarama durduruldu";
                _status.Text = "Kaynak taraması ESC ile durduruldu. Açılmış geçici belgeler kapatılıyor...";
                if (_progress != null) _progress.Value = 0;
            }
            catch (Exception ex)
            {
                _timing.Text = "Tarama durdu";
                _status.Text = "Tarama başarısız: " + ex.Message;
                MessageBox.Show(this, ex.Message, "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { automationScope?.Dispose(); } catch { }
                // Geçici belgeler servisler tarafından kapatılır; ana montajı ActivateDoc3 ile
                // zorla öne çağırmayarak SrcCadWorks ile SOLIDWORKS arasındaki z-order yarışını önle.
                try { dialogGuard?.Dispose(); } catch { }
                try { escapeMonitor.Dispose(); } catch { }
                Enabled = true;
            }
        }

        private void BuildProcessCodeInputs(IEnumerable<WeldFormRow> rows)
        {
            // Yalnız taranan montajda gerçekten bulunan proseslerin dosya kodu alanlarını göster.
            // Böylece Kaynak ekranı gereksiz alanlarla dolmaz; olmayan prosesler için TextBox da
            // oluşturulmaz. İşlem tipi normalizasyonu TESTERE -> İMALAT vb. eşlemeleri korur.
            string[] displayOrder = { "KAYNAK", "LAZER", "İMALAT", "TALAŞLI İMALAT", "MONTAJ" };
            var present = new HashSet<string>((rows ?? Enumerable.Empty<WeldFormRow>())
                .Select(r => WeldAssemblyScanner.NormalizeOperation(r.OperationType))
                .Where(op => !string.IsNullOrWhiteSpace(op)), StringComparer.OrdinalIgnoreCase);
            var order = displayOrder.Where(present.Contains).ToList();

            foreach (string op in order)
            {
                var holder = new Panel { Width = 202, Height = 56, Margin = new Padding(0, 0, 8, 0), BackColor = Surface };
                holder.Controls.Add(new Label
                {
                    Text = op + " DOSYA KODU",
                    Dock = DockStyle.Top,
                    Height = 21,
                    ForeColor = Muted,
                    Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true
                });
                var t = new TextBox { Dock = DockStyle.Bottom, Height = 27 };
                t.TextChanged += (s, e) => UpdateFileCodePreview();
                holder.Controls.Add(t);
                _codePanel.Controls.Add(holder);
                _processCodes[op] = t;
            }
            ApplyTextRenderingQuality(_codePanel);
        }

        private void UpdateFileCodePreview()
        {
            foreach (var row in _rows)
            {
                TextBox box;
                string op = WeldAssemblyScanner.NormalizeOperation(row.OperationType);
                row.FileCode = _processCodes.TryGetValue(op, out box) ? (box.Text ?? "") : "";
            }
            _grid?.Refresh();
        }

        private void RefreshPreview()
        {
            var row = _grid?.CurrentRow?.DataBoundItem as WeldFormRow;
            if (row == null) { _preview.LoadRow(null); return; }
            _preview.LoadRow(new LaserPartRow { PartCode = row.PartCode, SourcePath = row.SourcePath, Configuration = row.Configuration, DrawingPath = row.DrawingPath });
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            if (e.X < _grid.Columns[e.ColumnIndex].Width - ExcelHeaderMenus.ArrowWidth(_grid)) return;
            ShowWeldSortMenu(e.ColumnIndex);
        }

        private void ShowWeldSortMenu(int columnIndex)
        {
            if (_grid == null || columnIndex < 0 || columnIndex >= _grid.Columns.Count) return;
            var column = _grid.Columns[columnIndex];
            string property = column.DataPropertyName;
            if (string.IsNullOrWhiteSpace(property)) return;

            bool numeric = string.Equals(property, "Quantity", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(property, "MassText", StringComparison.OrdinalIgnoreCase);
            var menu = new ContextMenuStrip();
            menu.Items.Add(numeric ? "Küçükten Büyüğe" : "A'dan Z'ye", null, (s, e) => ApplySort(property, true));
            menu.Items.Add(numeric ? "Büyükten Küçüğe" : "Z'den A'ya", null, (s, e) => ApplySort(property, false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Montaj Sırasına Dön", null, (s, e) => ResetSort());

            var rect = _grid.GetCellDisplayRectangle(columnIndex, -1, true);
            menu.Show(_grid, new Point(rect.Left, Math.Max(0, rect.Bottom)));
        }

        private void ApplySortFromSelector(bool ascending)
        {
            var choice = _sortField.SelectedItem as SortChoice;
            if (choice == null || string.IsNullOrWhiteSpace(choice.Property)) { ResetSort(); return; }
            ApplySort(choice.Property, ascending);
        }

        private void ApplySort(string property, bool ascending)
        {
            if (_rows.Count == 0) return;
            var selected = _grid?.CurrentRow?.DataBoundItem as WeldFormRow;
            var sorted = _rows.ToList();
            sorted.Sort((a, b) =>
            {
                int cmp = CompareValues(GetSortValue(a, property), GetSortValue(b, property), property);
                if (cmp == 0) cmp = CompareSequence(a?.Sequence, b?.Sequence);
                return ascending ? cmp : -cmp;
            });
            ReplaceRows(sorted);
            _sortProperty = property;
            _sortAscending = ascending;
            UpdateSortGlyphs();
            RestoreGridSelection(selected);
        }

        private void ResetSort()
        {
            var selected = _grid?.CurrentRow?.DataBoundItem as WeldFormRow;
            ReplaceRows(_originalRows);
            _sortProperty = "";
            _sortAscending = true;
            if (_sortField != null) _sortField.SelectedIndex = 0;
            ResetSortGlyphs();
            RestoreGridSelection(selected);
        }

        private void ReplaceRows(IEnumerable<WeldFormRow> rows)
        {
            bool old = _rows.RaiseListChangedEvents;
            _rows.RaiseListChangedEvents = false;
            _rows.Clear();
            foreach (var row in rows) _rows.Add(row);
            _rows.RaiseListChangedEvents = old;
            if (old) _rows.ResetBindings();
        }

        private void SelectSortChoice(string property)
        {
            if (_sortField == null) return;
            for (int i = 0; i < _sortField.Items.Count; i++)
            {
                var choice = _sortField.Items[i] as SortChoice;
                if (choice != null && string.Equals(choice.Property, property, StringComparison.OrdinalIgnoreCase))
                {
                    _sortField.SelectedIndex = i;
                    return;
                }
            }
        }

        private void UpdateSortGlyphs()
        {
            if (_grid == null) return;
            ResetSortGlyphs();
            if (string.IsNullOrWhiteSpace(_sortProperty)) return;
            DataGridViewColumn column = null;
            try { column = _grid.Columns[_sortProperty]; } catch { }
            if (column != null) column.HeaderCell.SortGlyphDirection = _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
        }

        private void ResetSortGlyphs()
        {
            if (_grid == null) return;
            foreach (DataGridViewColumn c in _grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
            _grid.Invalidate();
        }

        private void RestoreGridSelection(WeldFormRow selected)
        {
            if (selected == null || _grid == null) return;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (ReferenceEquals(row.DataBoundItem, selected))
                {
                    _grid.ClearSelection();
                    row.Selected = true;
                    if (row.Cells.Count > 0) _grid.CurrentCell = row.Cells[0];
                    return;
                }
            }
        }

        private static object GetSortValue(WeldFormRow row, string property)
        {
            if (row == null) return null;
            switch (property ?? "")
            {
                case "Sequence": return row.Sequence;
                case "PartCode": return row.PartCode;
                case "ProductDescription": return row.ProductDescription;
                case "Quantity": return row.Quantity;
                case "MassText": return ParseNumber(row.MassText);
                case "ColorCode": return row.ColorCode;
                case "OperationType": return row.OperationType;
                case "FileCode": return row.FileCode;
                case "DrawingStatus": return row.DrawingStatus;
                default: return "";
            }
        }

        private static double ParseNumber(string value)
        {
            double grams;
            if (MassValueFormatter.TryParseGrams(value, true, out grams)) return grams;
            double d;
            if (double.TryParse(value ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }

        private static int CompareValues(object left, object right, string property)
        {
            if (string.Equals(property, "Sequence", StringComparison.OrdinalIgnoreCase))
                return CompareSequence(Convert.ToString(left), Convert.ToString(right));
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            if (left is int && right is int) return ((int)left).CompareTo((int)right);
            if (left is double && right is double) return ((double)left).CompareTo((double)right);
            return StringComparer.CurrentCultureIgnoreCase.Compare(Convert.ToString(left), Convert.ToString(right));
        }

        private static int CompareSequence(string left, string right)
        {
            string[] a = (left ?? "").Split('.');
            string[] b = (right ?? "").Split('.');
            int max = Math.Max(a.Length, b.Length);
            for (int i = 0; i < max; i++)
            {
                int ai = i < a.Length ? SafeInt(a[i]) : -1;
                int bi = i < b.Length ? SafeInt(b[i]) : -1;
                int cmp = ai.CompareTo(bi);
                if (cmp != 0) return cmp;
            }
            return StringComparer.CurrentCultureIgnoreCase.Compare(left ?? "", right ?? "");
        }

        private static int SafeInt(string value)
        {
            int n;
            return int.TryParse(value, out n) ? n : int.MaxValue;
        }

        private void CreatePackage()
        {
            if (_rows.Count == 0) { MessageBox.Show(this, "Kaynak satırı yok."); return; }
            if (!_pdf.Checked && !_excel.Checked) { MessageBox.Show(this, "En az bir çıktı türü seçmelisiniz: Kaynak PDF veya Kaynak Excel.", "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (_excel.Checked && !File.Exists(_template.Text)) { MessageBox.Show(this, "Kaynak şablonu bulunamadı."); return; }

            TelegramSettings telegramSettings = TelegramSettingsStore.Load();
            string telegramOperationName = BuildWeldTelegramOperationName();
            var totalWatch = Stopwatch.StartNew();
            var pdfWatch = new Stopwatch();
            var excelWatch = new Stopwatch();
            var errors = new List<string>();
            var outputNotes = new Dictionary<string, string>();
            ProcessingSpeedMode processingSpeed = SelectedProcessingSpeed();
            int pdfCreated = 0;
            int pdfMissing = 0;
            bool excelCreated = false;
            string excelImageLayout = "";
            string folder = "";
            ModalDialogGuard dialogGuard = null;
            SolidWorksAutomationScope automationScope = null;
            var escapeMonitor = new EscapeCancellationMonitor();

            try
            {
                Enabled = false;
                _create.Enabled = false;
                _drawingExists.Clear();
                SetProcessingRow(null);

                // PDF/Excel üretimi boyunca SOLIDWORKS kaynak belgeleri sessiz/salt-okunur kullanılır.
                // Kaydetme sorusu çıkarsa kullanıcıya göstermeden otomatik HAYIR seçilir.
                dialogGuard = new ModalDialogGuard(null);
                automationScope = new SolidWorksAutomationScope(_swApp, true, true, false, false);

                folder = _resume.Folder ?? EnsureWeldFolder(_output.Text);
                _output.Text = folder;
                folder = _resume.Begin(folder);
                errors = _resume.PreviousErrors();
                outputNotes = _resume.PreviousNotes();
                Directory.CreateDirectory(folder);
                RestoreHiddenWeldComponents(false);
                escapeMonitor.ThrowIfRequested("Kaynak hazırlama ESC ile iptal edildi.");
                if (_hideProcessed != null && _hideProcessed.Checked)
                {
                    _visibilityTracker = new AssemblyVisibilityTracker(_swApp);
                    _hiddenProcessedRows = 0;
                }
                var codes = _processCodes.ToDictionary(k => k.Key, v => (v.Value.Text ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
                _info.ProjectName = _project.Text.Trim();
                _info.FileName = _fileName.Text.Trim();
                _info.Preparer = _preparer.Text.Trim();
                _info.Approver = _approver.Text.Trim();
                _info.Note = _note.Text.Trim();
                string weldCode = codes.ContainsKey("KAYNAK") ? codes["KAYNAK"] : "";
                if (!string.IsNullOrWhiteSpace(weldCode)) _info.FileCode = weldCode;
                if (string.IsNullOrWhiteSpace(_info.FileCode))
                    _info.FileCode = Path.GetFileNameWithoutExtension((_swApp.ActiveDoc as ModelDoc2)?.GetPathName()) + "-KAYNAK";

                var roots = _rows.Where(r => r.IsWeldRoot)
                    .GroupBy(r => ManufacturingIdentity.Key(r.SourcePath, r.Configuration), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).ToList();
                var processedRootCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int pdfSteps = _pdf.Checked ? roots.Count : 0;
                int excelSteps = _excel.Checked ? Math.Max(1, _rows.Count) : 0;
                int totalSteps = Math.Max(1, pdfSteps + excelSteps);
                int completed = 0;
                SetProgress(0, totalSteps, "Kaynak paketi hazırlanıyor • Hız: " + SelectedProcessingSpeedText(), "", totalWatch);
                SendWeldTelegramStarted(telegramSettings, telegramOperationName, roots.Count, _rows.Count, _pdf.Checked, _excel.Checked, SelectedProcessingSpeedText());

                if (_pdf.Checked)
                {
                    pdfWatch.Start();
                    var exporter = new DrawingPdfExporter(_swApp, processingSpeed);
                    Logger.Info("Kaynak PDF işlem hızı: " + processingSpeed);
                    for (int i = 0; i < roots.Count; i++)
                    {
                        escapeMonitor.ThrowIfRequested("Kaynak hazırlama ESC ile iptal edildi.");
                        var r = roots[i];
                        SetProcessingRow(r);
                        SetProgress(completed, totalSteps, "Kaynak PDF", r.PartCode, totalWatch);
                        Application.DoEvents();
                        _resume.ClearFailure("PDF", r, errors, outputNotes, r.PartCode);
                        _resume.SaveProgress(errors, outputNotes);
                        if (string.IsNullOrWhiteSpace(r.DrawingPath) || !File.Exists(r.DrawingPath))
                        {
                            pdfMissing++;
                            errors.Add(r.PartCode + ": Teknik resim bulunamadı");
                            outputNotes[r.PartCode] = "HATA: Teknik resim bulunamadı";
                            Logger.Info("Kaynak PDF atlandı; teknik resim yok: " + r.PartCode);
                        }
                        else
                        {
                            try
                            {
                                string pdfPath = Path.Combine(folder, SafeFileName(r.PartCode) + ".pdf");
                                if (!_resume.Reuse("PDF", r, pdfPath))
                                {
                                    _resume.Start("PDF", r);
                                    exporter.Export(r.DrawingPath, pdfPath);
                                    escapeMonitor.ThrowIfRequested("Kaynak hazırlama ESC ile iptal edildi.");
                                }
                                _resume.Commit("PDF", r, pdfPath);
                                pdfCreated++;
                                if (!string.IsNullOrWhiteSpace(r.PartCode)) processedRootCodes.Add(r.PartCode);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                errors.Add((r.PartCode ?? "PDF") + " PDF: " + ex.Message);
                                outputNotes[r.PartCode] = "HATA: PDF - " + ex.Message;
                                Logger.Error("Kaynak PDF oluşturulamadı: " + r.PartCode, ex);
                            }
                            ApplyPdfInterPartDelay(processingSpeed, i + 1);
                        }
                        _resume.SaveProgress(errors, outputNotes);
                        completed++;
                        SetProgress(completed, totalSteps, "Kaynak PDF", r.PartCode, totalWatch);
                    }
                    pdfWatch.Stop();
                    SetProcessingRow(null);
                }

                escapeMonitor.ThrowIfRequested("Kaynak hazırlama ESC ile iptal edildi.");
                if (_excel.Checked)
                {
                    _resume.ClearFailure("Excel", null, errors, outputNotes, null);
                    _resume.SaveProgress(errors, outputNotes);
                    excelWatch.Start();
                    string ext = Path.GetExtension(_template.Text);
                    ext = string.Equals(ext, ".xlsm", StringComparison.OrdinalIgnoreCase) ? ".xlsm" : ".xlsx";
                    string excelPath = Path.Combine(folder, SafeFileName(_info.FileCode) + ext);
                    int beforeExcel = completed;
                    int excelCompleted = 0;
                    try
                    {
                        var excelWriter = new ExcelWeldFormWriter(_swApp);
                        excelWriter.CreateFromTemplate(
                            _template.Text,
                            excelPath,
                            _info,
                            _rows.ToList(),
                            codes,
                            (index, total, partCode) =>
                            {
                                int normalized = Math.Max(0, Math.Min(total, index));
                                excelCompleted = normalized;
                                SetProgress(beforeExcel + normalized, totalSteps, "Kaynak Excel", partCode, totalWatch);
                                Application.DoEvents();
                            }, outputNotes, row =>
                            {
                                SetProcessingRow(row);
                                SetProgress(beforeExcel + excelCompleted, totalSteps, "Kaynak Excel", row.PartCode, totalWatch);
                                Application.DoEvents();
                            }, assemblyMultiplier: Math.Max(1, (int)_assemblyQuantity.Value),
                            shouldCancel: () => escapeMonitor.IsCancellationRequested);
                        excelCreated = true;
                        excelImageLayout = excelWriter.ImageLayoutSummary;
                        foreach (var rootRow in roots)
                            if (!string.IsNullOrWhiteSpace(rootRow.PartCode)) processedRootCodes.Add(rootRow.PartCode);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add("Kaynak Excel: " + ex.Message);
                        Logger.Error("Kaynak Excel oluşturulamadı", ex);
                    }
                    foreach (var note in outputNotes)
                    {
                        if ((note.Value ?? "").Contains("Excel görseli alınamadı veya kalite kontrolünden geçmedi"))
                        {
                            string imageError = note.Key + ": " + note.Value;
                            if (!errors.Contains(imageError)) errors.Add(imageError);
                        }
                    }
                    completed = beforeExcel + excelSteps;
                    excelWatch.Stop();
                    SetProcessingRow(null);
                    SetProgress(completed, totalSteps, "Kaynak Excel", _info.FileCode, totalWatch);
                }

                escapeMonitor.ThrowIfRequested("Kaynak hazırlama ESC ile iptal edildi.");
                _resume.SaveProgress(errors, outputNotes);
                _resume.Finish(errors.Count == 0);
                if (_hideProcessed != null && _hideProcessed.Checked && _visibilityTracker != null && processedRootCodes.Count > 0)
                {
                    _status.Text = "İşlenen kaynak grupları montajda gizleniyor...";
                    Application.DoEvents();
                    foreach (var rootRow in roots.Where(r => !string.IsNullOrWhiteSpace(r.PartCode) && processedRootCodes.Contains(r.PartCode)))
                    {
                        escapeMonitor.ThrowIfRequested("Kaynak hazırlama ESC ile iptal edildi.");
                        try
                        {
                            var visibilityRow = ToVisibilityRow(rootRow);
                            if (visibilityRow == null) continue;
                            int hidden = _visibilityTracker.HideOccurrences(visibilityRow);
                            if (hidden > 0) _hiddenProcessedRows++;
                        }
                        catch (Exception ex)
                        {
                            Logger.Info("Kaynak grubu gizlenemedi: " + (rootRow.PartCode ?? "") + " | " + ex.Message);
                        }
                    }
                    try { _visibilityTracker.Redraw(); } catch { }
                }

                totalWatch.Stop();
                SetProcessingRow(null);
                _timing.Text = "Tamamlandı • Geçen: " + FormatDuration(totalWatch.Elapsed);
                _output.Text = folder;
                _status.Text = "Tamamlandı • " + folder + " • Geçen: " + FormatDuration(totalWatch.Elapsed);
                _progress.Value = 100;

                string message = "Kaynak dosyası hazırlandı.\n\n" +
                    "Kaynak ana satırı: " + roots.Count + "\n" +
                    "Excel satırı: " + _rows.Count + "\n" +
                    "Kaynak PDF: " + (_pdf.Checked ? (pdfCreated + " dosya oluşturuldu") : "seçilmedi") + "\n" +
                    "Teknik resmi olmayan: " + (_pdf.Checked ? pdfMissing.ToString() : "-") + "\n" +
                    "Kaynak Excel: " + (_excel.Checked ? (excelCreated ? "oluşturuldu" : "oluşturulamadı") : "seçilmedi") + "\n" +
                    (_excel.Checked && excelCreated ? "Görsel yerleşimi: " + excelImageLayout + "\n" : "") +
                    "İşlem modu: " + SelectedProcessingSpeedText() + "\n" +
                    "Montajda gizlenen kaynak grubu: " + (_hideProcessed != null && _hideProcessed.Checked ? _hiddenProcessedRows.ToString() : "seçilmedi") + "\n" +
                    "Hata: " + errors.Count + "\n\n" +
                    "PDF süresi: " + FormatDuration(pdfWatch.Elapsed) + "\n" +
                    "Excel süresi: " + FormatDuration(excelWatch.Elapsed) + "\n" +
                    "Toplam geçen süre: " + FormatDuration(totalWatch.Elapsed);

                if (errors.Count > 0)
                {
                    message += "\n\nİlk hatalar:\n" + string.Join("\n", errors.Take(5));
                }

                SendWeldTelegramCompleted(
                    telegramSettings,
                    telegramOperationName,
                    roots.Count,
                    _rows.Count,
                    _pdf.Checked,
                    pdfCreated,
                    pdfMissing,
                    _excel.Checked,
                    excelCreated,
                    SelectedProcessingSpeedText(),
                    errors,
                    totalWatch.Elapsed);

                message += "\n\nKlasörü açmak ister misiniz?";

                DialogResult answer = MessageBox.Show(this, message, "SrcCadWorks", MessageBoxButtons.YesNo,
                    errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes && Directory.Exists(folder))
                    Process.Start("explorer.exe", folder);
            }
            catch (OperationCanceledException ex)
            {
                totalWatch.Stop();
                try { _resume.SaveProgress(errors, outputNotes); } catch { }
                Logger.Info("Kaynak hazırlama ESC ile iptal edildi; güvenli sınırda durduruldu. " + ex.Message);
                _status.Text = "İşlem ESC ile iptal edildi. Tamamlanmış PDF'ler korunur; yarım Excel yayımlanmaz.";
                SetProcessingRow(null);
                _timing.Text = "İptal edildi • Geçen: " + FormatDuration(totalWatch.Elapsed);
                SendWeldTelegramCancelled(telegramSettings, telegramOperationName, totalWatch.Elapsed);
                MessageBox.Show(this,
                    "Kaynak hazırlama ESC ile iptal edildi.\n\nTamamlanmış dosyalar korunur. Excel, tamamen oluşturulmadıysa son dosyanın üzerine yazılmaz.",
                    "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                totalWatch.Stop();
                SendWeldTelegramFailed(telegramSettings, telegramOperationName, ex, totalWatch.Elapsed);
                Logger.Error("Kaynak paketi oluşturulamadı", ex);
                _status.Text = "Hata: " + ex.Message;
                SetProcessingRow(null);
                _timing.Text = "İşlem durdu • Geçen: " + FormatDuration(totalWatch.Elapsed);
                MessageBox.Show(this, ex.Message, "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetProcessingRow(null);
                _resume.Suspend();
                try { automationScope?.Dispose(); } catch { }
                // Geçici belgeler servisler tarafından kapatılır; ana montajı ActivateDoc3 ile
                // zorla öne çağırmayarak SrcCadWorks ile SOLIDWORKS arasındaki z-order yarışını önle.
                try { dialogGuard?.Dispose(); } catch { }
                try { escapeMonitor.Dispose(); } catch { }
                Enabled = true;
                _create.Enabled = true;
            }
        }

        private string BuildWeldTelegramOperationName()
        {
            string name = (_project == null ? "" : (_project.Text ?? "").Trim());
            if (string.IsNullOrWhiteSpace(name)) name = (_fileName == null ? "" : (_fileName.Text ?? "").Trim());
            if (string.IsNullOrWhiteSpace(name) && _info != null) name = (_info.FileCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) return "Kaynak Dosyası Hazırla";
            return "Kaynak Dosyası Hazırla • " + name;
        }

        private static void SendWeldTelegramStarted(TelegramSettings settings, string operationName, int rootCount, int excelRows, bool pdf, bool excel, string speed)
        {
            if (settings == null || !settings.IsConfigured || !settings.SendStartMessage) return;
            string text = "🔧 " + operationName + " – İşlem Başladı\n\n" +
                "🔩 Kaynak ana satırı: " + rootCount + "\n" +
                "📋 Excel satırı: " + excelRows + "\n" +
                "📄 Kaynak PDF: " + (pdf ? "seçili" : "seçilmedi") + "\n" +
                "📊 Kaynak Excel: " + (excel ? "seçili" : "seçilmedi") + "\n" +
                "⚙️ İşlem modu: " + speed;
            QueueWeldTelegram(settings, text);
        }

        private static void SendWeldTelegramCompleted(
            TelegramSettings settings, string operationName, int rootCount, int excelRows,
            bool pdfSelected, int pdfCreated, int pdfMissing,
            bool excelSelected, bool excelCreated, string speed,
            IList<string> errors, TimeSpan elapsed)
        {
            if (settings == null || !settings.IsConfigured) return;
            int errorCount = errors == null ? 0 : errors.Count;
            string statusIcon = errorCount == 0 ? "✅" : "⚠️";
            string statusText = errorCount == 0 ? "Tamamlandı" : "Kısmen tamamlandı";
            string text = "🔧 " + operationName + " – İşlem Tamamlandı\n\n" +
                "🔩 Kaynak ana satırı: " + rootCount + "\n" +
                "📋 Excel satırı: " + excelRows + "\n" +
                "📄 Kaynak PDF: " + (pdfSelected ? (pdfCreated + " dosya oluşturuldu") : "seçilmedi") + "\n" +
                "⚠️ Teknik resmi olmayan: " + (pdfSelected ? pdfMissing.ToString() : "-") + "\n" +
                "📊 Kaynak Excel: " + (excelSelected ? (excelCreated ? "oluşturuldu" : "oluşturulamadı") : "seçilmedi") + "\n" +
                "⚙️ İşlem modu: " + speed + "\n" +
                "❗ Hata: " + errorCount + "\n" +
                "⏱️ Toplam süre: " + FormatDuration(elapsed) + "\n\n" +
                "Durum: " + statusIcon + " " + statusText;
            if (errorCount > 0)
                text += "\n\nİlk hatalar:\n- " + string.Join("\n- ", errors.Where(x => !string.IsNullOrWhiteSpace(x)).Take(5));

            TelegramLastReportStore.Save(text);
            if (settings.SendCompletionMessage) QueueWeldTelegram(settings, text);
        }

        private static void SendWeldTelegramCancelled(TelegramSettings settings, string operationName, TimeSpan elapsed)
        {
            if (settings == null || !settings.IsConfigured) return;
            string text = "⏹️ " + operationName + " – İşlem ESC ile İptal Edildi\n\n" +
                "✅ Tamamlanmış dosyalar korunur.\n" +
                "⏱️ Geçen süre: " + FormatDuration(elapsed);
            TelegramLastReportStore.Save(text);
            if (settings.SendCompletionMessage) QueueWeldTelegram(settings, text);
        }

        private static void SendWeldTelegramFailed(TelegramSettings settings, string operationName, Exception error, TimeSpan elapsed)
        {
            if (settings == null || !settings.IsConfigured) return;
            string text = "❌ " + operationName + " – İşlem Hatası\n\n" +
                "⚠️ Hata: " + (error == null ? "Bilinmeyen hata" : error.Message) + "\n" +
                "⏱️ Geçen süre: " + FormatDuration(elapsed);
            TelegramLastReportStore.Save(text);
            QueueWeldTelegram(settings, text);
        }

        private static void QueueWeldTelegram(TelegramSettings settings, string text)
        {
            if (settings == null || !settings.IsConfigured || string.IsNullOrWhiteSpace(text)) return;
            TelegramSettings copy = settings.Clone();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { new TelegramBotClient(copy).SendMessage(text, false); }
                catch (Exception ex) { Logger.Error("Kaynak Telegram bildirimi gönderilemedi", ex); }
            });
        }

        private static void ApplyPdfInterPartDelay(ProcessingSpeedMode speed, int completedCount)
        {
            int delay = speed == ProcessingSpeedMode.Safe ? 400 : (speed == ProcessingSpeedMode.Fast ? 1 : 200);
            if (delay > 0) System.Threading.Thread.Sleep(delay);

            if (completedCount > 0 && completedCount % 10 == 0)
            {
                int rest = speed == ProcessingSpeedMode.Safe ? 1500 : (speed == ProcessingSpeedMode.Normal ? 750 : 0);
                if (rest > 0) System.Threading.Thread.Sleep(rest);
            }
        }

        private void SetProgress(int index, int total, string stage, string partName, Stopwatch watch)
        {
            total = Math.Max(1, total);
            index = Math.Max(0, Math.Min(total, index));
            int percent = (int)Math.Round(index * 100.0 / total);
            int remaining = Math.Max(0, total - index);
            if (_progress != null)
            {
                _progress.Minimum = 0;
                _progress.Maximum = 100;
                _progress.Value = Math.Max(0, Math.Min(100, percent));
            }
            string part = string.IsNullOrWhiteSpace(partName) ? "" : " • " + partName;
            string elapsed = watch == null ? "" : "Geçen: " + FormatDuration(watch.Elapsed) + " • ";
            string estimate = watch == null || index == 0 ? "hesaplanıyor..."
                : index >= total ? "00:00"
                : FormatDuration(TimeSpan.FromSeconds(watch.Elapsed.TotalSeconds / index * remaining));
            _timing.Text = elapsed + "Tahmini kalan: " + estimate;
            _status.Text = "İlerleme: " + index + "/" + total + " (" + percent + "%)" +
                " • Toplam: " + total + " • Kalan: " + remaining + " • " + stage + part;
        }

        private void SetProcessingRow(WeldFormRow row)
        {
            if (ReferenceEquals(_processingRow, row)) return;
            _processingRow = row;
            if (_grid == null || _grid.IsDisposed) return;
            if (row != null)
            {
                foreach (DataGridViewRow gridRow in _grid.Rows)
                {
                    if (!ReferenceEquals(gridRow.DataBoundItem, row) || !gridRow.Visible) continue;
                    try { if (!gridRow.Displayed) _grid.FirstDisplayedScrollingRowIndex = gridRow.Index; } catch { }
                    break;
                }
            }
            // Paint before synchronous SOLIDWORKS/Excel work begins, without changing selection.
            _grid.Refresh();
        }

        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as WeldFormRow;
            if (row == null) return;
            bool active = ReferenceEquals(row, _processingRow);
            string path = row.DrawingPath ?? "";
            bool hasDrawing;
            if (!_drawingExists.TryGetValue(path, out hasDrawing))
            {
                hasDrawing = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
                _drawingExists[path] = hasDrawing;
            }
            Color background = active ? Color.FromArgb(255, 225, 228)
                : !hasDrawing ? Color.FromArgb(255, 247, 220) : Surface;
            Color foreground = active ? Color.FromArgb(128, 20, 26)
                : !hasDrawing ? Color.FromArgb(115, 82, 12) : Color.FromArgb(35, 40, 48);
            if (string.Equals(_grid.Columns[e.ColumnIndex].Name, "DrawingStatus", StringComparison.OrdinalIgnoreCase))
            {
                e.Value = hasDrawing ? "VAR" : "YOK";
                e.FormattingApplied = true;
            }
            e.CellStyle.BackColor = background;
            e.CellStyle.ForeColor = foreground;
            // Selection must not cover processing or missing-drawing status colors.
            if (active || !hasDrawing)
            {
                e.CellStyle.SelectionBackColor = background;
                e.CellStyle.SelectionForeColor = foreground;
            }
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return value.ToString(@"hh\:mm\:ss");
            return value.ToString(@"mm\:ss");
        }

        private void BrowseOutput(object sender, EventArgs e)
        {
            IDisposable zOrderPause = null;
            try
            {
                zOrderPause = _ownedWindowTracker?.Suspend();

                string picked;
                bool cancelled;
                if (ModernFolderPicker.TryPickFolder(Handle, _output.Text, "KAYNAK klasörünün oluşturulacağı ana klasörü seçin", out picked, out cancelled))
                {
                    _output.Text = EnsureWeldFolder(picked);
                    return;
                }
                if (cancelled) return;
                using (var d = new FolderBrowserDialog { Description = "KAYNAK klasörünün oluşturulacağı ana klasörü seçin", SelectedPath = _output.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) _output.Text = EnsureWeldFolder(d.SelectedPath);
            }
            finally
            {
                zOrderPause?.Dispose();
            }
        }

        private void BrowseTemplate(object sender, EventArgs e)
        {
            IDisposable zOrderPause = null;
            try
            {
                zOrderPause = _ownedWindowTracker?.Suspend();
                using (var d = new OpenFileDialog
                {
                    Filter = "Excel dosyaları (*.xls;*.xlsx;*.xlsm)|*.xls;*.xlsx;*.xlsm|Tüm dosyalar (*.*)|*.*",
                    FileName = Path.GetFileName(_template.Text)
                })
                    if (d.ShowDialog(this) == DialogResult.OK) _template.Text = d.FileName;
            }
            finally
            {
                zOrderPause?.Dispose();
            }
        }

        private string DefaultOutputFolder()
        {
            try
            {
                string p = (_swApp.ActiveDoc as ModelDoc2)?.GetPathName();
                string d = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(d)) return Path.Combine(d, "KAYNAK");
            }
            catch { }
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), "KAYNAK");
        }

        private static string EnsureWeldFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string t = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(t), "KAYNAK", StringComparison.OrdinalIgnoreCase) ? t : Path.Combine(t, "KAYNAK");
        }

        private static string FindTemplate()
        {
            string baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            string p = Path.Combine(baseDir, "templates", "KAYNAK FORMU.xls");
            if (File.Exists(p)) return p;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", "KAYNAK FORMU.xls");
        }

        private static string SafeFileName(string value)
        {
            string v = string.IsNullOrWhiteSpace(value) ? "KAYNAK" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) v = v.Replace(c, '_');
            return v;
        }

        private sealed class SortChoice
        {
            public string Text { get; private set; }
            public string Property { get; private set; }
            public SortChoice(string text, string property) { Text = text; Property = property; }
            public override string ToString() => Text;
        }
    }
}
