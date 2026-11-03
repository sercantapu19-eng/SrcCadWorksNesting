using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    internal sealed class LaserPrepForm : Form
    {
        private ResumeSession _resume;
        private static readonly Color CBackground = Color.FromArgb(244, 246, 249);
        private static readonly Color CPanel = Color.White;
        private static readonly Color CPanel2 = Color.FromArgb(249, 250, 252);
        private static readonly Color CBorder = Color.FromArgb(205, 211, 218);
        private static readonly Color CText = Color.FromArgb(31, 36, 42);
        private static readonly Color CMuted = Color.FromArgb(94, 103, 113);
        private static readonly Color CAccent = Color.FromArgb(202, 28, 36);
        private static readonly Color CSelection = Color.FromArgb(255, 232, 234);
        private static readonly Color CWarning = Color.FromArgb(244, 173, 44);
        private static readonly Color CDanger = Color.FromArgb(196, 45, 52);

        private readonly SldWorks _swApp;
        private readonly LaserPrepPreferences _preferences;
        private readonly BindingList<LaserPartRow> _rows = new BindingList<LaserPartRow>();
        private readonly List<LaserPartRow> _allRows = new List<LaserPartRow>();
        private readonly HashSet<string> _excludedControlValues = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        private DataGridView _grid;
        private Label _summary;
        private Label _status;
        private TextBox _project, _fileName, _fileCode, _designer, _preparer, _reason, _note, _template, _output;
        private CheckBox _bendLines, _exportDxf, _exportPdf, _exportExcel, _exportCombinedDxf, _exportCombinedDwg, _includeHidden, _includePassive, _onlyLaserTagged, _onlyWeldTagged, _keepOnTop, _hideProcessed;
        private RadioButton _speedSafe, _speedNormal, _speedFast;
        private Button _generate, _close, _rescan, _restoreHidden, _pause, _telegramSettings;
        private EDrawingsPreviewControl _eDrawingsPreview;
        private bool _loadingRowPreview, _pendingRowPreview;
        private string _loadedRowPreviewKey = null;
        private Button _openPart, _openDrawing;
        private SplitContainer _split;
        private TableLayoutPanel _root;
        private Control _projectPanel;
        private Panel _summaryPanel;
        private Control _bottomBar;
        private TableLayoutPanel _previewLayout;
        private Control _previewSectionLabel;
        private Control _previewActionButtons;
        private bool _previewExpanded;
        private readonly float[] _previewSavedRowHeights = new float[3];
        private NumericUpDown _assemblyQuantity;
        private ProgressBar _progressBar;
        private string _summaryStats = "";
        private volatile bool _cancelRequested;
        private bool _busy;
        private bool _closeAfterCancel;
        private TimeSpan _lastScanDuration = TimeSpan.Zero;
        private AssemblyVisibilityTracker _visibilityTracker;
        private int _hiddenProcessedRows;
        private int _hiddenRowsSinceRedraw;
        private volatile bool _pauseRequested;
        private string _processingPreviewPartCode = "";
        private bool _restoringGridLayout;
        private string _sortProperty = "";
        private bool _sortAscending = true;
        private ContextMenuStrip _sortMenu;
        private int _sortMenuColumnIndex = -1;
        private TelegramStatusMonitor _telegramControlMonitor;
        private TelegramRunNotifier _activeTelegramNotifier;
        private volatile bool _productionRunning;
        private ProductionRecoveryManager _activeRecoveryManager;
        private ProductionHangWatchdog _productionHangWatchdog;
        private ExternalWatchdogLauncher _externalWatchdog;
        private SolidWorksOwnedWindow.Tracker _ownedWindowTracker;

        public LaserPrepForm(SldWorks swApp)
        {
            _swApp = swApp;
            _preferences = LaserPrepPreferencesStore.Load();
            Text = "SrcCadWorks — Lazer Dosyası Hazırla " + AppVersion.Display;
            SrcCadWorks.Services.BrandIcon.Apply(this);
            Width = 1600;
            Height = 940;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1240, 760);
            BackColor = CBackground;
            ForeColor = CText;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape || !_busy) return;
                RequestCancel(false);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            BuildUi();
            _resume = new ResumeSession(this, _swApp, "Laser", () => _rows, () => _busy, Analyze, () => Generate(null, EventArgs.Empty));
            ApplyTextRenderingQuality(this);
            Shown += (s, e) =>
            {
                WindowState = FormWindowState.Maximized;
                RestartTelegramControlMonitor();
                BeginInvoke(new Action(() =>
                {
                    if (_keepOnTop != null)
                    {
                        SolidWorksOwnedWindow.Apply(this, _swApp, _keepOnTop.Checked, true);
                        _ownedWindowTracker = SolidWorksOwnedWindow.Track(this, _swApp, _keepOnTop.Checked);
                    }
                    ConfigureSplitter();
                    Analyze();
                }));
            };
            Resize += (s, e) => ConfigureSplitter();
            FormClosed += (s, e) =>
            {
                try { _ownedWindowTracker?.Dispose(); } catch { }
                _ownedWindowTracker = null;
                StopTelegramControlMonitor();
                try { _sortMenu?.Dispose(); } catch { }
                try { _externalWatchdog?.Dispose(); } catch { }
                SavePreferences();
                DisposePreview();
                RestoreHiddenComponents(true);
            };
            FormClosing += LaserPrepForm_FormClosing;
        }

        internal bool IsBusyForWindowRecovery => _busy;
        internal bool KeepAboveSolidWorksRequested => _keepOnTop != null ? _keepOnTop.Checked : _preferences.KeepOnTop;

        internal bool RestoreFromTaskPane()
        {
            try
            {
                if (IsDisposed) return false;
                if (!Visible) SolidWorksOwnedWindow.Show(this, _swApp, KeepAboveSolidWorksRequested);
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;

                // Rebuild the native owner tracker in case SOLIDWORKS recreated/reordered windows
                // during document automation. This also recovers a stale z-order state.
                try { _ownedWindowTracker?.Dispose(); } catch { }
                _ownedWindowTracker = null;
                if (_keepOnTop != null)
                {
                    SolidWorksOwnedWindow.Apply(this, _swApp, _keepOnTop.Checked, false);
                    _ownedWindowTracker = SolidWorksOwnedWindow.Track(this, _swApp, _keepOnTop.Checked);
                }

                BringToFront();
                Activate();
                return Visible && !IsDisposed;
            }
            catch (Exception ex)
            {
                Logger.Error("Lazer Hazırla penceresi geri getirilemedi", ex);
                return false;
            }
        }

        private void BuildUi()
        {
            SuspendLayout();
            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = CBackground,
                Padding = new Padding(0),
                AutoScroll = true
            };
            var root = _root;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 362));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            _projectPanel = BuildProjectPanel();
            root.Controls.Add(_projectPanel, 0, 1);

            _summaryPanel = new Panel { Dock = DockStyle.Fill, BackColor = CPanel2, Padding = new Padding(16, 7, 16, 6) };
            var summaryPanel = _summaryPanel;
            _summary = new Label
            {
                Dock = DockStyle.Top,
                Height = 25,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = CAccent,
                BackColor = CPanel2,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 14,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            summaryPanel.Controls.Add(_summary);
            summaryPanel.Controls.Add(_progressBar);
            root.Controls.Add(summaryPanel, 0, 2);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = CBorder,
                SplitterWidth = 5,
                FixedPanel = FixedPanel.Panel2
            };
            _split.Panel1.BackColor = CPanel;
            _split.Panel2.BackColor = CPanel2;
            _split.Panel1.Padding = new Padding(12, 10, 6, 10);
            _split.Panel2.Padding = new Padding(10);
            _grid = BuildGrid();
            _split.Panel1.Controls.Add(_grid);
            _split.Panel2.Controls.Add(BuildPreviewPanel());
            root.Controls.Add(_split, 0, 3);

            _bottomBar = BuildBottomBar();
            root.Controls.Add(_bottomBar, 0, 4);
            WirePreferencePersistence();
            ResumeLayout(true);
        }

        private void ConfigureSplitter()
        {
            if (_split == null || _split.IsDisposed || !_split.IsHandleCreated) return;
            try
            {
                int width = _split.ClientSize.Width;
                if (width < 700) return;

                // SplitterDistance, form ilk oluşturulurken panel ölçüleri henüz oturmadan
                // verilirse WinForms ArgumentOutOfRangeException fırlatabiliyor. Burada yalnızca
                // gerçek genişlik belli olduktan sonra güvenli aralıkta ayarlanır.
                int previewWidth = Math.Max(520, Math.Min(680, width * 38 / 100));
                int desired = width - previewWidth - _split.SplitterWidth;
                int min = 480;
                int max = width - 480 - _split.SplitterWidth;
                if (max <= min) return;
                desired = Math.Max(min, Math.Min(max, desired));

                if (Math.Abs(_split.SplitterDistance - desired) > 8)
                    _split.SplitterDistance = desired;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Yeniden boyutlandırma sırasında WinForms geçici olarak geçersiz ölçü döndürebilir.
                // Bir sonraki Resize/Shown turunda yeniden denenecek; kullanıcıya hata penceresi çıkmaz.
            }
        }

        private Control BuildHeader()
        {
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
                ForeColor = CText,
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold)
            };
            var subtitle = new Label
            {
                Text = "LAZER DOSYASI HAZIRLA  •  ÜRETİM PAKETİ",
                Dock = DockStyle.Top,
                Height = 27,
                ForeColor = CMuted,
                Font = new Font("Segoe UI", 9.5F)
            };
            texts.Controls.Add(subtitle);
            texts.Controls.Add(title);
            header.Controls.Add(texts);
            header.Controls.Add(logo);
            return header;
        }

        private Control BuildProjectPanel()
        {
            var outer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CPanel,
                Padding = new Padding(16, 12, 16, 12),
                AutoScroll = true,
                AutoScrollMinSize = new Size(0, 344)
            };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, MinimumSize = new Size(1000, 342) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var left = CreateFieldsTable(6);
            var right = CreateFieldsTable(9);
            table.Controls.Add(left, 0, 0);
            table.Controls.Add(right, 1, 0);

            _project = AddField(left, "Proje Adı", 0);
            _fileName = AddField(left, "Dosya Adı", 1);
            _fileCode = AddField(left, "Dosya Kodu", 2);
            _designer = AddField(left, "Tasarlayan", 3);
            _assemblyQuantity = AddNumericField(left, "Montaj Adedi", 4, 1, 9999);
            _assemblyQuantity.ValueChanged += (s, e) => ApplyAssemblyMultiplier();
            _output = AddBrowseField(left, "Çıktı Klasörü", 5, BrowseOutput);

            _preparer = AddField(right, "Hazırlayan", 0);
            _reason = AddField(right, "Talep Sebebi", 1);
            _note = AddField(right, "Not", 2);
            _template = AddBrowseField(right, "Lazer Şablonu", 3, BrowseTemplate);

            var tagOptions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CPanel,
                Padding = new Padding(0, 2, 0, 0)
            };
            var scopeOptions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                BackColor = CPanel,
                Padding = new Padding(0, 2, 0, 0)
            };
            var outputOptions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 2,
                BackColor = CPanel,
                Margin = new Padding(0),
                Padding = new Padding(0, 1, 0, 0)
            };
            for (int c = 0; c < 4; c++) outputOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            outputOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            outputOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            _exportDxf = MakeCheck("DXF", _preferences.ExportDxf);
            _bendLines = MakeCheck("Büküm çizgileri", _preferences.BendLines);
            _exportPdf = MakeCheck("Teknik resim PDF", _preferences.ExportPdf);
            _exportExcel = MakeCheck("Lazer Excel", _preferences.ExportExcel);
            _exportCombinedDxf = MakeCheck("Toplu DXF", _preferences.ExportCombinedDxf);
            _exportCombinedDwg = MakeCheck("Toplu DWG", _preferences.ExportCombinedDwg);
            _onlyLaserTagged = MakeCheck("Sadece lazer etiketli", _preferences.OnlyLaserTagged);
            _onlyWeldTagged = MakeCheck("Kaynak etiketli", _preferences.OnlyWeldTagged);
            _includeHidden = MakeCheck("Gizlileri dahil et", _preferences.IncludeHidden);
            _includePassive = MakeCheck("Pasifleri dahil et", _preferences.IncludePassive);
            _hideProcessed = MakeCheck("İşlenenleri gizle", _preferences.HideProcessed);
            _restoreHidden = MakeButton("Gizlenenleri Göster", 165);
            _restoreHidden.Dock = DockStyle.Fill;
            _restoreHidden.Margin = new Padding(3, 2, 6, 2);
            _restoreHidden.Enabled = false;
            _restoreHidden.Click += (s, e) => RestoreHiddenComponents(false);

            _onlyLaserTagged.CheckedChanged += (s, e) => { if (!_busy) ApplyLaserFilter(); };
            _onlyWeldTagged.CheckedChanged += (s, e) => { if (!_busy) ApplyLaserFilter(); };
            _includeHidden.CheckedChanged += (s, e) => MarkScopeChanged();
            _includePassive.CheckedChanged += (s, e) => MarkScopeChanged();

            tagOptions.Controls.Add(_onlyLaserTagged);
            tagOptions.Controls.Add(_onlyWeldTagged);
            scopeOptions.Controls.Add(_includeHidden);
            scopeOptions.Controls.Add(_includePassive);

            outputOptions.Controls.Add(_exportDxf, 0, 0);
            outputOptions.Controls.Add(_bendLines, 1, 0);
            outputOptions.Controls.Add(_exportPdf, 2, 0);
            outputOptions.Controls.Add(_exportExcel, 3, 0);
            outputOptions.Controls.Add(_exportCombinedDxf, 0, 1);
            outputOptions.Controls.Add(_exportCombinedDwg, 1, 1);
            outputOptions.Controls.Add(_hideProcessed, 2, 1);
            outputOptions.Controls.Add(_restoreHidden, 3, 1);

            right.Controls.Add(new Label { Text = "İşlem Tipi", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CMuted }, 0, 4);
            right.Controls.Add(tagOptions, 1, 4);
            right.Controls.Add(new Label { Text = "Tarama Kapsamı", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CMuted }, 0, 5);
            right.Controls.Add(scopeOptions, 1, 5);
            right.Controls.Add(new Label { Text = "Çıktılar", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CMuted }, 0, 6);
            right.Controls.Add(outputOptions, 1, 6);
            right.SetRowSpan(outputOptions, 2);

            right.Controls.Add(new Label { Text = "Hız", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CMuted }, 0, 8);
            right.Controls.Add(BuildSpeedSelector(), 1, 8);
            outer.Controls.Add(table);
            return outer;
        }

        private Control BuildSpeedSelector()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = CPanel,
                Margin = new Padding(0),
                Padding = new Padding(3, 3, 8, 3)
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

            ProcessingSpeedMode initial = ParseProcessingSpeed(_preferences == null ? null : _preferences.ProcessingSpeed);
            SetSpeedChoice(initial, false);

            var tip = new ToolTip();
            tip.SetToolTip(_speedSafe, "PDF/teknik resim işlemlerinde daha uzun beklemeler. Büyük işlerde en stabil seçenek.");
            tip.SetToolTip(_speedNormal, "Önerilen varsayılan. PDF işlemlerinde dengeli bekleme ve performans.");
            tip.SetToolTip(_speedFast, "V1.11.16 hızına yakın. DWG/DXF hızlı kalır; PDF tarafında en az bekleme uygulanır.");
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
                BackColor = CPanel2,
                ForeColor = CText,
                Margin = new Padding(2, 1, 2, 1),
                Tag = mode
            };
            button.FlatAppearance.BorderColor = CBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.CheckedBackColor = CAccent;
            button.CheckedChanged += SpeedChoice_CheckedChanged;
            return button;
        }

        private void SpeedChoice_CheckedChanged(object sender, EventArgs e)
        {
            var button = sender as RadioButton;
            if (button == null) return;
            UpdateSpeedChoiceVisuals();
            if (button.Checked) SavePreferences();
        }

        private void UpdateSpeedChoiceVisuals()
        {
            RadioButton[] buttons = { _speedSafe, _speedNormal, _speedFast };
            foreach (var button in buttons)
            {
                if (button == null) continue;
                button.BackColor = button.Checked ? CAccent : CPanel2;
                button.ForeColor = button.Checked ? Color.White : CText;
                button.FlatAppearance.BorderColor = button.Checked ? CAccent : CBorder;
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

        private static ProcessingSpeedMode ParseProcessingSpeed(string value)
        {
            ProcessingSpeedMode mode;
            return Enum.TryParse(value ?? "", true, out mode) ? mode : ProcessingSpeedMode.Normal;
        }

        private void SetSpeedChoice(ProcessingSpeedMode mode, bool save)
        {
            if (_speedSafe != null) _speedSafe.Checked = mode == ProcessingSpeedMode.Safe;
            if (_speedNormal != null) _speedNormal.Checked = mode == ProcessingSpeedMode.Normal;
            if (_speedFast != null) _speedFast.Checked = mode == ProcessingSpeedMode.Fast;
            UpdateSpeedChoiceVisuals();
            if (save) SavePreferences();
        }

        private void SetSpeedSelectorEnabled(bool enabled)
        {
            if (_speedSafe != null) _speedSafe.Enabled = enabled;
            if (_speedNormal != null) _speedNormal.Enabled = enabled;
            if (_speedFast != null) _speedFast.Enabled = enabled;
        }

        private TableLayoutPanel CreateFieldsTable(int rows)
        {
            // Tüm satırlar Absolute olduğunda TableLayoutPanel bazı DPI/ölçülerde kalan
            // yüksekliği son satıra ekleyebiliyor. Çıktı Klasörü satırı son satır olduğu için
            // Gözat düğmesi gereksiz yere boydan boya uzuyordu. Boş bir yüzde satırı kalan
            // alanı emerek form alanlarını sabit 38 px yükseklikte tutar.
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows + 1, BackColor = CPanel, Padding = new Padding(0, 0, 10, 0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            return t;
        }

        private NumericUpDown AddNumericField(TableLayoutPanel p, string label, int row, int minimum, int maximum)
        {
            p.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = CMuted, AutoEllipsis = false }, 0, row);
            var n = new NumericUpDown
            {
                Dock = DockStyle.Fill, Minimum = minimum, Maximum = maximum, Value = minimum,
                BackColor = Color.White, ForeColor = CText, BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(3, 5, 8, 5), TextAlign = HorizontalAlignment.Left, ThousandsSeparator = true
            };
            p.Controls.Add(n, 1, row);
            return n;
        }

        private TextBox AddField(TableLayoutPanel p, string label, int row)
        {
            p.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = CMuted, AutoEllipsis = false }, 0, row);
            var t = MakeTextBox();
            p.Controls.Add(t, 1, row);
            return t;
        }

        private TextBox AddBrowseField(TableLayoutPanel p, string label, int row, EventHandler browse)
        {
            p.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = CMuted, AutoEllipsis = false }, 0, row);
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = CPanel };
            var t = MakeTextBox();
            t.Dock = DockStyle.Fill;
            var button = MakeButton("Gözat", 62);
            button.Dock = DockStyle.Right;
            button.Click += browse;
            panel.Controls.Add(t);
            panel.Controls.Add(button);
            p.Controls.Add(panel, 1, row);
            return t;
        }

        private TextBox MakeTextBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = CText,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(3, 5, 8, 5)
            };
        }

        private void MarkScopeChanged()
        {
            if (_busy) return;
            if (_status != null)
                _status.Text = "Tarama kapsamı değişti. Yeni kapsamı uygulamak için YENİDEN TARA düğmesine basın.";
        }

        private CheckBox MakeCheck(string text, bool value)
        {
            return new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = CText, Margin = new Padding(4, 4, 12, 2) };
        }

        private void WirePreferencePersistence()
        {
            CheckBox[] controls =
            {
                _exportDxf, _bendLines, _exportPdf, _exportExcel, _exportCombinedDxf, _exportCombinedDwg,
                _includeHidden, _includePassive, _onlyLaserTagged, _onlyWeldTagged, _hideProcessed, _keepOnTop
            };
            foreach (var check in controls)
            {
                if (check != null) check.CheckedChanged += (s, e) => SavePreferences();
            }
        }

        private void SavePreferences()
        {
            if (_preferences == null) return;
            if (_exportDxf != null) _preferences.ExportDxf = _exportDxf.Checked;
            if (_bendLines != null) _preferences.BendLines = _bendLines.Checked;
            if (_exportPdf != null) _preferences.ExportPdf = _exportPdf.Checked;
            if (_exportExcel != null) _preferences.ExportExcel = _exportExcel.Checked;
            if (_exportCombinedDxf != null) _preferences.ExportCombinedDxf = _exportCombinedDxf.Checked;
            if (_exportCombinedDwg != null) _preferences.ExportCombinedDwg = _exportCombinedDwg.Checked;
            if (_onlyLaserTagged != null) _preferences.OnlyLaserTagged = _onlyLaserTagged.Checked;
            if (_onlyWeldTagged != null) _preferences.OnlyWeldTagged = _onlyWeldTagged.Checked;
            if (_includeHidden != null) _preferences.IncludeHidden = _includeHidden.Checked;
            if (_includePassive != null) _preferences.IncludePassive = _includePassive.Checked;
            if (_hideProcessed != null) _preferences.HideProcessed = _hideProcessed.Checked;
            if (_keepOnTop != null) _preferences.KeepOnTop = _keepOnTop.Checked;
            _preferences.ProcessingSpeed = SelectedProcessingSpeed().ToString();
            if (_grid != null && _grid.Columns.Count > 0 && !_restoringGridLayout)
            {
                var ordered = _grid.Columns.Cast<DataGridViewColumn>()
                    .Where(c => !string.IsNullOrWhiteSpace(c.DataPropertyName))
                    .OrderBy(c => c.DisplayIndex)
                    .ToList();
                _preferences.GridColumnOrder = string.Join("|", ordered.Select(c => c.DataPropertyName));
                _preferences.GridColumnWidths = string.Join("|", ordered.Select(c => c.DataPropertyName + "=" + c.Width));
            }
            LaserPrepPreferencesStore.Save(_preferences);
        }

        private DataGridView BuildGrid()
        {
            var grid = new KeyboardDataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                DataSource = _rows,
                BackgroundColor = CPanel,
                BorderStyle = BorderStyle.None,
                GridColor = CBorder,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ScrollBars = ScrollBars.Both,
                ShowCellToolTips = true,
                AllowUserToOrderColumns = true,
                ColumnHeadersHeight = 36,
                RowTemplate = { Height = 30 }
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 241, 245);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(134, 21, 27);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(238, 241, 245);
            grid.DefaultCellStyle.BackColor = CPanel;
            grid.DefaultCellStyle.ForeColor = CText;
            grid.DefaultCellStyle.SelectionBackColor = CSelection;
            grid.DefaultCellStyle.SelectionForeColor = CText;

            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "RowNumber", HeaderText = "Sıra No", Width = 58, MinimumWidth = 50, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Selected", HeaderText = "Seç", Width = 58, MinimumWidth = 54, SortMode = DataGridViewColumnSortMode.Programmatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "PartCode", HeaderText = "Parça Kodu", Width = 155, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Quantity", HeaderText = "Adet", Width = 55, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ThicknessText", HeaderText = "Kalınlık", Width = 75, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Material", HeaderText = "Malzeme (manuel düzenlenebilir)", Width = 190, ReadOnly = false });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "TappedHoleText", HeaderText = "Kılavuz Algısı", Width = 105, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "CountersinkText", HeaderText = "Havşa Algısı", Width = 100, ReadOnly = true });
            grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Operation", HeaderText = "İşlem", Width = 280, DropDownWidth = 320, DataSource = LaserOperation.Choices, FlatStyle = FlatStyle.Flat });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ColorCode", HeaderText = "Renk Kodu", Width = 135 });
            grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "WeldProcess", HeaderText = "Kaynak", Width = 75, DataSource = new[] { "", "VAR", "YOK" }, FlatStyle = FlatStyle.Flat });
            // Son sütunları Fill modunda bırakmak yatay kaydırma aralığını daraltıyordu.
            // Sabit/geniş kolonlar kullanarak kullanıcı sağa doğru daha fazla kaydırabilir ve uzun rota/kontrol metinlerini görebilir.
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Description", HeaderText = "Açıklama", Width = 260, MinimumWidth = 180 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ProductionDefinition", HeaderText = "Ürün Tanımı", Width = 260, MinimumWidth = 180, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ControlText", HeaderText = "Kontrol", Width = 620, MinimumWidth = 320, ReadOnly = true });

            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.Programmatic;
                bool sortable = !string.Equals(column.DataPropertyName, "RowNumber", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(column.DataPropertyName, "Selected", StringComparison.OrdinalIgnoreCase))
                {
                    column.HeaderCell.ToolTipText = "Çift tık veya Ctrl+A: tümünü seç / tüm seçimi kaldır. Sağdaki küçük ok: sıralama.";
                }
                else
                {
                    column.HeaderCell.ToolTipText = sortable
                        ? "Başlığın sağındaki küçük oka basarak sıralayın; sütunu taşımak için başlığı basılı tutup sürükleyin."
                        : "Sıra numarası. Sütunu taşımak için başlığı basılı tutup sürükleyin.";
                }
                if (sortable)
                {
                    var style = column.HeaderCell.Style.Clone();
                    // Başlığın sağında sıralama oku için sabit bir alan ayır.
                    // Böylece dar sütunlarda (Seç, Adet, Kaynak vb.) ok metnin üstüne binmez.
                    style.Padding = new Padding(style.Padding.Left, style.Padding.Top, Math.Max(style.Padding.Right, 26), style.Padding.Bottom);
                    column.HeaderCell.Style = style;
                }
            }

            RestoreGridLayout(grid);
            grid.ColumnDisplayIndexChanged += GridColumnDisplayIndexChanged;
            grid.ColumnWidthChanged += GridColumnWidthChanged;
            grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            grid.ColumnHeaderMouseDoubleClick += GridColumnHeaderMouseDoubleClick;
            grid.KeyDown += GridKeyDown;
            grid.EditingControlShowing += GridEditingControlShowing;
            ExcelHeaderMenus.Attach(grid, IsSortableProperty,
                property => string.Equals(property, _sortProperty, StringComparison.OrdinalIgnoreCase)
                    ? (_sortAscending ? 1 : -1) : 0);
            grid.CellFormatting += GridCellFormatting;
            grid.SelectionChanged += (s, e) => RefreshPreview();
            grid.CellDoubleClick += GridCellDoubleClick;
            grid.CellMouseDown += GridPartCodeMouseDown;
            grid.MouseEnter += (sender, args) => RestoreIdleCursor();
            grid.MouseMove += (sender, args) =>
            {
                if (!_busy && (System.Windows.Forms.Cursor.Current == Cursors.WaitCursor || System.Windows.Forms.Cursor.Current == Cursors.AppStarting)) RestoreIdleCursor();
            };
            return grid;
        }


        private void RestoreGridLayout(DataGridView grid)
        {
            if (grid == null || _preferences == null) return;
            _restoringGridLayout = true;
            try
            {
                var columnsByName = grid.Columns.Cast<DataGridViewColumn>()
                    .Where(c => !string.IsNullOrWhiteSpace(c.DataPropertyName))
                    .ToDictionary(c => c.DataPropertyName, c => c, StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(_preferences.GridColumnOrder))
                {
                    var order = _preferences.GridColumnOrder
                        .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(name => columnsByName.ContainsKey(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var name in columnsByName.Keys)
                    {
                        if (!order.Contains(name, StringComparer.OrdinalIgnoreCase)) order.Add(name);
                    }

                    // V1.11.12-14 geçişinde eski kayıtlı sütun düzenlerinde RowNumber
                    // son sütuna eklenebiliyordu. Bunu yalnızca bir kez düzeltiyoruz.
                    // Sonraki açılışlarda kullanıcının elle yaptığı sıralamaya dokunulmaz.
                    if (_preferences.GridLayoutVersion < 2)
                    {
                        order.RemoveAll(name => string.Equals(name, "RowNumber", StringComparison.OrdinalIgnoreCase));
                        int selectedIndex = order.FindIndex(name => string.Equals(name, "Selected", StringComparison.OrdinalIgnoreCase));
                        if (selectedIndex < 0) selectedIndex = 0;
                        order.Insert(selectedIndex, "RowNumber");
                        _preferences.GridLayoutVersion = 2;
                    }

                    for (int i = 0; i < order.Count; i++)
                    {
                        DataGridViewColumn col;
                        if (columnsByName.TryGetValue(order[i], out col)) col.DisplayIndex = i;
                    }
                }
                else if (_preferences.GridLayoutVersion < 2)
                {
                    // Hiç kayıt yoksa varsayılan: Sıra No | Seç | Parça Kodu ...
                    DataGridViewColumn rowNo;
                    DataGridViewColumn selected;
                    if (columnsByName.TryGetValue("RowNumber", out rowNo)) rowNo.DisplayIndex = 0;
                    if (columnsByName.TryGetValue("Selected", out selected)) selected.DisplayIndex = 1;
                    _preferences.GridLayoutVersion = 2;
                }

                if (!string.IsNullOrWhiteSpace(_preferences.GridColumnWidths))
                {
                    foreach (var item in _preferences.GridColumnWidths.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = item.Split(new[] { '=' }, 2);
                        if (parts.Length != 2) continue;
                        DataGridViewColumn col;
                        int width;
                        if (columnsByName.TryGetValue(parts[0], out col) && int.TryParse(parts[1], out width))
                        {
                            int minWidth = Math.Max(col.MinimumWidth, 35);
                            col.Width = Math.Max(minWidth, Math.Min(width, 1200));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Tablo sütun düzeni geri yüklenemedi; varsayılan düzen kullanılacak: " + ex.Message);
            }
            finally
            {
                _restoringGridLayout = false;
                try
                {
                    if (_preferences != null && _preferences.GridLayoutVersion >= 2)
                    {
                        var ordered = grid.Columns.Cast<DataGridViewColumn>()
                            .Where(c => !string.IsNullOrWhiteSpace(c.DataPropertyName))
                            .OrderBy(c => c.DisplayIndex)
                            .ToList();
                        _preferences.GridColumnOrder = string.Join("|", ordered.Select(c => c.DataPropertyName));
                        _preferences.GridColumnWidths = string.Join("|", ordered.Select(c => c.DataPropertyName + "=" + c.Width));
                        LaserPrepPreferencesStore.Save(_preferences);
                    }
                }
                catch { }
            }
        }

        private void GridColumnDisplayIndexChanged(object sender, DataGridViewColumnEventArgs e)
        {
            SaveGridLayout();
        }

        private void GridColumnWidthChanged(object sender, DataGridViewColumnEventArgs e)
        {
            SaveGridLayout();
        }

        private void SaveGridLayout()
        {
            if (_restoringGridLayout || _grid == null || _preferences == null || _grid.Columns.Count == 0) return;
            try
            {
                var ordered = _grid.Columns.Cast<DataGridViewColumn>()
                    .Where(c => !string.IsNullOrWhiteSpace(c.DataPropertyName))
                    .OrderBy(c => c.DisplayIndex)
                    .ToList();

                _preferences.GridColumnOrder = string.Join("|", ordered.Select(c => c.DataPropertyName));
                _preferences.GridColumnWidths = string.Join("|", ordered.Select(c => c.DataPropertyName + "=" + c.Width));
                LaserPrepPreferencesStore.Save(_preferences);
            }
            catch (Exception ex)
            {
                Logger.Info("Tablo sütun düzeni kaydedilemedi: " + ex.Message);
            }
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (_busy || _grid == null || e.Button != MouseButtons.Left || e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            var column = _grid.Columns[e.ColumnIndex];
            string property = column.DataPropertyName;
            if (!IsSortableProperty(property)) return;

            // Excel benzeri kullanım: sıralama yalnız başlığın sağındaki küçük menü okundan açılır.
            // Böylece başlığın geri kalan kısmı sürükleyip sütun taşımak için rahatça kullanılabilir.
            int arrowAreaWidth = ExcelHeaderMenus.ArrowWidth(_grid);
            if (e.X < Math.Max(0, column.Width - arrowAreaWidth)) return;
            ShowSortMenu(e.ColumnIndex);
        }

        private void GridColumnHeaderMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (_busy || _grid == null || e.Button != MouseButtons.Left || e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            var column = _grid.Columns[e.ColumnIndex];
            if (!string.Equals(column.DataPropertyName, "Selected", StringComparison.OrdinalIgnoreCase)) return;

            // "Seç" başlığına çift tık: tümünü seç / tüm seçimi kaldır.
            ToggleAllPartSelections();
        }

        private void GridKeyDown(object sender, KeyEventArgs e)
        {
            if (_busy || _grid == null) return;

            // Kaynak sütununda mouse kullanmadan hızlı veri girişi:
            // V = VAR, Y = YOK, Delete/Backspace = boş.
            // Hücre edit moduna girmese bile doğrudan satıra uygula.
            if (!e.Control && !e.Alt && TryApplyWeldKeyboardShortcut(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (!e.Control || e.KeyCode != Keys.A) return;

            // Bir metin hücresi düzenleniyorsa Ctrl+A metni seçmeye devam etsin.
            // Normal tablo odağında Ctrl+A, Seç sütunundaki tüm işaretleri topluca açıp kapatır.
            if (_grid.EditingControl != null) return;

            ToggleAllPartSelections();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void GridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var combo = e.Control as ComboBox;
            if (combo == null) return;
            combo.KeyDown -= WeldComboKeyDown;

            if (_grid?.CurrentCell == null) return;
            var column = _grid.Columns[_grid.CurrentCell.ColumnIndex];
            if (string.Equals(column.DataPropertyName, "WeldProcess", StringComparison.OrdinalIgnoreCase))
                combo.KeyDown += WeldComboKeyDown;
        }

        private void WeldComboKeyDown(object sender, KeyEventArgs e)
        {
            if (_busy) return;
            string value = null;
            if (e.KeyCode == Keys.V) value = "VAR";
            else if (e.KeyCode == Keys.Y) value = "YOK";
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) value = "";
            else return;

            var combo = sender as ComboBox;
            if (combo != null) combo.SelectedItem = value;
            ApplyWeldValueToCurrentRow(value);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private bool TryApplyWeldKeyboardShortcut(Keys key)
        {
            if (_grid?.CurrentCell == null) return false;
            var column = _grid.Columns[_grid.CurrentCell.ColumnIndex];
            if (!string.Equals(column.DataPropertyName, "WeldProcess", StringComparison.OrdinalIgnoreCase)) return false;

            string value;
            if (key == Keys.V) value = "VAR";
            else if (key == Keys.Y) value = "YOK";
            else if (key == Keys.Delete || key == Keys.Back) value = "";
            else return false;

            ApplyWeldValueToCurrentRow(value);
            return true;
        }

        private void ApplyWeldValueToCurrentRow(string value)
        {
            if (_grid?.CurrentCell == null) return;
            try
            {
                var row = _grid.CurrentRow?.DataBoundItem as LaserPartRow;
                if (row != null) row.WeldProcess = value ?? "";
                _grid.CurrentCell.Value = value ?? "";
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                _grid.EndEdit();
                _grid.InvalidateRow(_grid.CurrentCell.RowIndex);
            }
            catch { }
        }

        private void SetAllPartSelections(bool selected)
        {
            if (_busy || _grid == null || _rows == null || _rows.Count == 0) return;
            try { _grid.EndEdit(); } catch { }

            bool oldRaise = _rows.RaiseListChangedEvents;
            _rows.RaiseListChangedEvents = false;
            try
            {
                // Menü yalnız o anda tabloda görünen/uygun satırları etkiler.
                foreach (var row in _rows)
                {
                    if (row != null) row.Selected = selected;
                }
            }
            finally
            {
                _rows.RaiseListChangedEvents = oldRaise;
            }

            if (oldRaise) _rows.ResetBindings();
            _grid.Invalidate();
        }

        private void ToggleAllPartSelections()
        {
            if (_busy || _grid == null || _rows == null || _rows.Count == 0) return;

            try { _grid.EndEdit(); } catch { }

            bool allSelected = _rows.All(r => r != null && r.Selected);
            bool newValue = !allSelected;

            bool oldRaise = _rows.RaiseListChangedEvents;
            _rows.RaiseListChangedEvents = false;
            try
            {
                foreach (var row in _rows)
                {
                    if (row != null) row.Selected = newValue;
                }
            }
            finally
            {
                _rows.RaiseListChangedEvents = oldRaise;
            }

            if (oldRaise) _rows.ResetBindings();

            // Seç sütununa göre sıralanmışsa görünümü de güncel tut.
            if (string.Equals(_sortProperty, "Selected", StringComparison.OrdinalIgnoreCase))
                ApplyCurrentGridSort();
            else
                _grid.Invalidate();
        }

        private void ShowSortMenu(int columnIndex)
        {
            if (_grid == null || columnIndex < 0 || columnIndex >= _grid.Columns.Count) return;
            var column = _grid.Columns[columnIndex];
            string property = column.DataPropertyName;
            if (!IsSortableProperty(property)) return;

            if (_sortMenu != null)
            {
                try { _sortMenu.Dispose(); } catch { }
                _sortMenu = null;
            }

            _sortMenuColumnIndex = columnIndex;
            _sortMenu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

            if (string.Equals(property, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                var selectAll = new ToolStripMenuItem("Tümünü Seç");
                var clearAll = new ToolStripMenuItem("Seçimi Kaldır");
                selectAll.Click += (s, e) => SetAllPartSelections(true);
                clearAll.Click += (s, e) => SetAllPartSelections(false);
                _sortMenu.Items.Add(selectAll);
                _sortMenu.Items.Add(clearAll);
            }
            else
            {
                string ascText;
                string descText;
                if (IsNumericSortProperty(property))
                {
                    ascText = "Küçükten → Büyüğe";
                    descText = "Büyükten → Küçüğe";
                }
                else
                {
                    ascText = "A → Z";
                    descText = "Z → A";
                }

                var asc = new ToolStripMenuItem(ascText) { Checked = string.Equals(_sortProperty, property, StringComparison.OrdinalIgnoreCase) && _sortAscending };
                var desc = new ToolStripMenuItem(descText) { Checked = string.Equals(_sortProperty, property, StringComparison.OrdinalIgnoreCase) && !_sortAscending };
                asc.Click += (s, e) => ApplySortFromMenu(property, true);
                desc.Click += (s, e) => ApplySortFromMenu(property, false);
                _sortMenu.Items.Add(asc);
                _sortMenu.Items.Add(desc);
                if (string.Equals(property, "ControlText", StringComparison.OrdinalIgnoreCase))
                    AddControlValueFilter(_sortMenu);
            }

            Rectangle rect = _grid.GetCellDisplayRectangle(columnIndex, -1, true);
            Point location = new Point(Math.Max(0, rect.Right - 22), rect.Bottom);
            _sortMenu.Show(_grid, location);
        }

        private static string ControlFilterValue(LaserPartRow row)
        {
            return string.IsNullOrWhiteSpace(row?.ControlText) ? "(Boş)" : row.ControlText.Trim();
        }

        private void AddControlValueFilter(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());
            var clearFilter = new ToolStripMenuItem("Kontrol filtresini temizle") { Enabled = !_busy && _excludedControlValues.Count > 0 };
            clearFilter.Click += (sender, args) =>
            {
                _excludedControlValues.Clear();
                ApplyLaserFilter();
                menu.Close();
            };
            menu.Items.Add(clearFilter);
            menu.Items.Add(new ToolStripLabel("Gösterilecek kontrol değerleri:"));

            var panel = new TableLayoutPanel
            {
                Width = 500, Height = 250, ColumnCount = 1, RowCount = 3,
                BackColor = Color.White, Padding = new Padding(5), Margin = Padding.Empty
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            var list = new CheckedListBox
            {
                Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
                HorizontalScrollbar = true, Font = _grid.Font, BorderStyle = BorderStyle.FixedSingle
            };
            foreach (string value in _allRows.Select(ControlFilterValue).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(sortValue => sortValue, StringComparer.CurrentCultureIgnoreCase))
                list.Items.Add(value, !_excludedControlValues.Contains(value));

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var selectAll = new Button { Text = "Tümünü Seç", AutoSize = true, Height = 26 };
            var deselectAll = new Button { Text = "Seçimi Kaldır", AutoSize = true, Height = 26 };
            selectAll.Click += (sender, args) => { for (int index = 0; index < list.Items.Count; index++) list.SetItemChecked(index, true); };
            deselectAll.Click += (sender, args) => { for (int index = 0; index < list.Items.Count; index++) list.SetItemChecked(index, false); };
            actions.Controls.Add(selectAll);
            actions.Controls.Add(deselectAll);
            panel.Controls.Add(actions, 0, 0);
            panel.Controls.Add(list, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var apply = new Button { Text = "Uygula", Width = 88, Height = 28 };
            var cancel = new Button { Text = "İptal", Width = 88, Height = 28 };
            apply.Click += (sender, args) =>
            {
                _excludedControlValues.Clear();
                for (int index = 0; index < list.Items.Count; index++)
                    if (!list.GetItemChecked(index)) _excludedControlValues.Add((string)list.Items[index]);
                ApplyLaserFilter();
                menu.Close();
            };
            cancel.Click += (sender, args) => menu.Close();
            buttons.Controls.Add(apply);
            buttons.Controls.Add(cancel);
            panel.Controls.Add(buttons, 0, 2);
            menu.Items.Add(new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty, Enabled = !_busy });
        }

        private void ApplySortFromMenu(string property, bool ascending)
        {
            if (!IsSortableProperty(property)) return;
            _sortProperty = property;
            _sortAscending = ascending;
            ApplyCurrentGridSort();
            try { _grid?.Invalidate(); } catch { }
        }

        private static bool IsSortableProperty(string property)
        {
            if (string.IsNullOrWhiteSpace(property)) return false;
            return !string.Equals(property, "RowNumber", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumericSortProperty(string property)
        {
            return string.Equals(property, "Quantity", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(property, "ThicknessText", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyCurrentGridSort()
        {
            if (_grid == null || _rows.Count == 0)
            {
                UpdateSortGlyphs();
                return;
            }

            LaserPartRow selectedRow = _grid.CurrentRow == null ? null : _grid.CurrentRow.DataBoundItem as LaserPartRow;
            var sorted = _rows.ToList();
            sorted.Sort((a, b) =>
            {
                int cmp = CompareSortValues(GetSortValue(a, _sortProperty), GetSortValue(b, _sortProperty));
                if (cmp == 0)
                    cmp = StringComparer.CurrentCultureIgnoreCase.Compare(a == null ? "" : a.PartCode ?? "", b == null ? "" : b.PartCode ?? "");
                return _sortAscending ? cmp : -cmp;
            });

            _rows.RaiseListChangedEvents = false;
            _rows.Clear();
            foreach (var row in sorted) _rows.Add(row);
            for (int i = 0; i < _rows.Count; i++) _rows[i].RowNumber = i + 1;
            _rows.RaiseListChangedEvents = true;
            _rows.ResetBindings();
            UpdateSortGlyphs();

            if (selectedRow != null)
            {
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    if (ReferenceEquals(_grid.Rows[i].DataBoundItem, selectedRow))
                    {
                        _grid.ClearSelection();
                        _grid.Rows[i].Selected = true;
                        if (_grid.Rows[i].Cells.Count > 0) _grid.CurrentCell = _grid.Rows[i].Cells[0];
                        break;
                    }
                }
            }
        }

        private void UpdateRowNumbers()
        {
            if (_rows == null) return;
            bool oldRaise = _rows.RaiseListChangedEvents;
            _rows.RaiseListChangedEvents = false;
            for (int i = 0; i < _rows.Count; i++) _rows[i].RowNumber = i + 1;
            _rows.RaiseListChangedEvents = oldRaise;
            if (oldRaise) _rows.ResetBindings();
        }

        private void UpdateSortGlyphs()
        {
            if (_grid == null) return;
            // Standart DataGridView sort glyph'i, Excel benzeri menü okumuzla çakışmasın.
            foreach (DataGridViewColumn column in _grid.Columns)
                column.HeaderCell.SortGlyphDirection = SortOrder.None;
            _grid.Invalidate();
        }

        private static object GetSortValue(LaserPartRow row, string property)
        {
            if (row == null) return null;
            switch (property ?? "")
            {
                case "RowNumber": return row.RowNumber;
                case "Selected": return row.Selected;
                case "PartCode": return row.PartCode;
                case "Quantity": return row.Quantity;
                case "ThicknessText": return row.ThicknessMm;
                case "Material": return row.Material;
                case "TappedHoleText": return row.TappedHoleText;
                case "CountersinkText": return row.CountersinkText;
                case "Operation": return row.Operation;
                case "ColorCode": return row.ColorCode;
                case "WeldProcess": return row.WeldProcess;
                case "Description": return row.Description;
                case "ProductionDefinition": return row.ProductionDefinition;
                case "ControlText": return row.ControlText;
                default: return "";
            }
        }

        private static int CompareSortValues(object left, object right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;

            var leftString = left as string;
            var rightString = right as string;
            if (leftString != null || rightString != null)
                return StringComparer.CurrentCultureIgnoreCase.Compare(left == null ? "" : left.ToString(), right == null ? "" : right.ToString());

            var comparable = left as IComparable;
            if (comparable != null && left.GetType() == right.GetType())
                return comparable.CompareTo(right);

            return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
        }

        private Control BuildPreviewPanel()
        {
            // Parça bilgileri tabloda/Kontrol sütununda zaten mevcut.
            // Sağ panel yalnızca mümkün olan en büyük eDrawings görüntüsünü ve
            // doğrudan açma eylemlerini gösterir.
            _previewLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = CPanel2,
                Padding = new Padding(8)
            };
            var panel = _previewLayout;
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            _previewSectionLabel = new Label
            {
                Text = "PARÇA ÖNİZLEME",
                Dock = DockStyle.Fill,
                ForeColor = CAccent,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(_previewSectionLabel, 0, 0);

            _eDrawingsPreview = new EDrawingsPreviewControl
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 4)
            };
            _eDrawingsPreview.PreviewExpandRequested += (s, e) => SetPreviewExpanded(true);
            _eDrawingsPreview.BackRequested += (s, e) => SetPreviewExpanded(false);
            panel.Controls.Add(_eDrawingsPreview, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = CPanel2,
                Margin = Padding.Empty,
                Padding = new Padding(0, 2, 0, 0)
            };
            _previewActionButtons = buttons;
            _openPart = MakeButton("Parçayı Aç", 110);
            _openDrawing = MakeButton("Teknik Resmi Aç", 130);
            _openPart.Click += (s2, e2) => OpenSelectedPart();
            _openDrawing.Click += (s2, e2) => OpenSelectedDrawing();
            buttons.Controls.Add(_openPart);
            buttons.Controls.Add(_openDrawing);
            panel.Controls.Add(buttons, 0, 2);

            return panel;
        }

        private void SetPreviewExpanded(bool expanded)
        {
            if (_previewExpanded == expanded || _root == null || _split == null || _previewLayout == null) return;
            _previewExpanded = expanded;
            try
            {
                _root.SuspendLayout();
                _split.SuspendLayout();
                _previewLayout.SuspendLayout();

                if (expanded)
                {
                    _previewSavedRowHeights[0] = _root.RowStyles[_root.GetRow(_projectPanel)].Height;
                    _root.RowStyles[_root.GetRow(_projectPanel)].Height = 0;
                    _previewSavedRowHeights[1] = _root.RowStyles[_root.GetRow(_summaryPanel)].Height;
                    _root.RowStyles[_root.GetRow(_summaryPanel)].Height = 0;
                    _previewSavedRowHeights[2] = _root.RowStyles[_root.GetRow(_bottomBar)].Height;
                    _root.RowStyles[_root.GetRow(_bottomBar)].Height = 0;
                    if (_projectPanel != null) _projectPanel.Visible = false;
                    if (_summaryPanel != null) _summaryPanel.Visible = false;
                    if (_bottomBar != null) _bottomBar.Visible = false;

                    _split.Panel1Collapsed = true;
                    _split.IsSplitterFixed = true;
                    _split.Panel2.Padding = new Padding(12);

                    _previewLayout.RowStyles[0].SizeType = SizeType.Absolute;
                    _previewLayout.RowStyles[0].Height = 0;
                    _previewLayout.RowStyles[1].SizeType = SizeType.Percent;
                    _previewLayout.RowStyles[1].Height = 100;
                    _previewLayout.RowStyles[2].SizeType = SizeType.Absolute;
                    _previewLayout.RowStyles[2].Height = 0;

                    if (_previewSectionLabel != null) _previewSectionLabel.Visible = false;
                    if (_previewActionButtons != null) _previewActionButtons.Visible = false;
                }
                else
                {
                    _root.RowStyles[_root.GetRow(_projectPanel)].Height = _previewSavedRowHeights[0];
                    _root.RowStyles[_root.GetRow(_summaryPanel)].Height = _previewSavedRowHeights[1];
                    _root.RowStyles[_root.GetRow(_bottomBar)].Height = _previewSavedRowHeights[2];
                    if (_projectPanel != null) _projectPanel.Visible = true;
                    if (_summaryPanel != null) _summaryPanel.Visible = true;
                    if (_bottomBar != null) _bottomBar.Visible = true;

                    _split.Panel1Collapsed = false;
                    _split.IsSplitterFixed = false;
                    _split.Panel2.Padding = new Padding(10);
                    ConfigureSplitter();

                    _previewLayout.RowStyles[0].SizeType = SizeType.Absolute;
                    _previewLayout.RowStyles[0].Height = 30;
                    _previewLayout.RowStyles[1].SizeType = SizeType.Percent;
                    _previewLayout.RowStyles[1].Height = 100;
                    _previewLayout.RowStyles[2].SizeType = SizeType.Absolute;
                    _previewLayout.RowStyles[2].Height = 44;

                    if (_previewSectionLabel != null) _previewSectionLabel.Visible = true;
                    if (_previewActionButtons != null) _previewActionButtons.Visible = true;
                }

                _eDrawingsPreview?.SetExpanded(expanded);
            }
            finally
            {
                _previewLayout.ResumeLayout(true);
                _split.ResumeLayout(true);
                _root.ResumeLayout(true);
                PerformLayout();
                BeginInvoke(new Action(() => _eDrawingsPreview?.FitToWindow()));
            }
        }

        private Control BuildBottomBar()
        {
            var bottom = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 14, 14, 12) };

            var leftActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                Width = 565,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.White,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            _rescan = MakeButton("Yeniden Tara", 110);
            _rescan.Margin = new Padding(0, 0, 10, 0);
            _rescan.Click += (s, e) => { if (!_busy) Analyze(); };

            _keepOnTop = MakeCheck("Pencereyi üstte tut", _preferences.KeepOnTop);
            _keepOnTop.Margin = new Padding(2, 9, 0, 0);
            _keepOnTop.CheckedChanged += (s, e) =>
            {
                SolidWorksOwnedWindow.Apply(this, _swApp, _keepOnTop.Checked, true);
                if (_ownedWindowTracker != null) _ownedWindowTracker.SetEnabled(_keepOnTop.Checked);
            };

            _pause = MakeButton("Duraklat", 90);
            _pause.Margin = new Padding(0, 0, 8, 0);
            _pause.Enabled = false;
            _pause.Click += (s, e) => TogglePause();

            _telegramSettings = MakeButton("Telegram Ayarları", 125);
            _telegramSettings.Margin = new Padding(0, 0, 8, 0);
            _telegramSettings.Click += (s, e) => OpenTelegramSettings();

            leftActions.Controls.Add(_rescan);
            leftActions.Controls.Add(_pause);
            leftActions.Controls.Add(_telegramSettings);
            leftActions.Controls.Add(_keepOnTop);

            _close = MakeButton("Kapat", 90);
            _close.Dock = DockStyle.Right;
            _close.Click += (s, e) =>
            {
                if (_busy) RequestCloseWhileBusy();
                else Close();
            };
            _generate = MakeButton("OLUŞTUR", 250, true);
            _generate.Dock = DockStyle.Right;
            _generate.Click += Generate;
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 10, 0), ForeColor = CMuted, AutoEllipsis = true };

            bottom.Controls.Add(_status);
            bottom.Controls.Add(leftActions);
            bottom.Controls.Add(_close);
            bottom.Controls.Add(_generate);
            return bottom;
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

        private Button MakeButton(string text, int width, bool accent = false)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? CAccent : CPanel2,
                ForeColor = accent ? Color.White : CText,
                Cursor = Cursors.Hand,
                Margin = new Padding(3)
            };
            b.FlatAppearance.BorderColor = accent ? CAccent : CBorder;
            return b;
        }

        private void OpenTelegramSettings()
        {
            if (_busy)
            {
                MessageBox.Show(this, "Telegram ayarlarını değiştirmek için devam eden üretim işleminin tamamlanmasını bekleyin.",
                    "Telegram Ayarları", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StopTelegramControlMonitor();
            try
            {
                using (var form = new TelegramSettingsForm())
                    form.ShowDialog(this);
            }
            finally
            {
                RestartTelegramControlMonitor();
            }
        }

        private void RestartTelegramControlMonitor()
        {
            StopTelegramControlMonitor();
            try
            {
                var settings = TelegramSettingsStore.Load();
                if (!settings.IsConfigured || !settings.EnableStatusCommand) return;

                _telegramControlMonitor = new TelegramStatusMonitor(
                    settings,
                    () => _activeTelegramNotifier == null ? null : _activeTelegramNotifier.Snapshot(),
                    () => _productionRunning,
                    () => _pauseRequested,
                    RequestTelegramPause,
                    RequestTelegramResume,
                    TelegramLastReportStore.Load);
                _telegramControlMonitor.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram uzaktan kontrol dinleyicisi başlatılamadı", ex);
            }
        }

        private void StopTelegramControlMonitor()
        {
            try { _telegramControlMonitor?.Stop(); } catch { }
            _telegramControlMonitor = null;
        }

        private void RequestTelegramPause()
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired)
                    BeginInvoke(new Action(() => SetPauseRequested(true, true)));
                else
                    SetPauseRequested(true, true);
            }
            catch (Exception ex)
            {
                Logger.Info("Telegram duraklat isteği arayüze aktarılamadı: " + ex.Message);
            }
        }

        private void RequestTelegramResume()
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired)
                    BeginInvoke(new Action(() => SetPauseRequested(false, true)));
                else
                    SetPauseRequested(false, true);
            }
            catch (Exception ex)
            {
                Logger.Info("Telegram devam isteği arayüze aktarılamadı: " + ex.Message);
            }
        }

        private void Analyze()
        {
            if (_busy) return;
            _resume.BeforeScan();
            ModalDialogGuard dialogGuard = null;
            SolidWorksAutomationScope automationScope = null;
            var scanWatch = Stopwatch.StartNew();
            var escapeMonitor = new EscapeCancellationMonitor();
            try
            {
                _busy = true;
                if (_onlyLaserTagged != null) _onlyLaserTagged.Enabled = false;
                if (_onlyWeldTagged != null) _onlyWeldTagged.Enabled = false;
                _cancelRequested = false;
                _pauseRequested = false;
                _processingPreviewPartCode = "";
                _closeAfterCancel = false;
                if (_close != null) _close.Text = "İptal";
                if (_rescan != null) _rescan.Enabled = false;
                UseWaitCursor = false; // Progress is displayed in the status bar; avoid inherited sticky wait cursors.
                _status.Text = "Montaj taranıyor... ESC = DURDUR. Adetler ve özel özellikler güncel montajdan yeniden okunuyor.";
                _generate.Enabled = false;
                DisposePreview();
                _allRows.Clear();
                _rows.Clear();
                EnsureReadOnlyNoSavePrompt();
                Application.DoEvents();

                dialogGuard = new ModalDialogGuard(null);
                automationScope = new SolidWorksAutomationScope(_swApp, true, false, false, false);

                var info = ProjectInfoFactory.FromActiveAssembly(_swApp);
                _project.Text = info.ProjectName;
                _fileName.Text = info.FileName;
                _fileCode.Text = info.FileCode;
                _designer.Text = info.Designer;
                _preparer.Text = info.Preparer;
                if (string.IsNullOrWhiteSpace(_reason.Text)) _reason.Text = "YENİ ÜRETİM";

                var active = _swApp.ActiveDoc as ModelDoc2;
                string assemblyFolder = active == null ? "" : (Path.GetDirectoryName(active.GetPathName()) ?? "");
                if (string.IsNullOrWhiteSpace(_output.Text)) _output.Text = EnsureLaserFolder(assemblyFolder);
                if (string.IsNullOrWhiteSpace(_template.Text)) _template.Text = FindTemplate();

                ResetProgress();
                var result = new AssemblyScanner(_swApp).ScanActiveAssembly(p =>
                {
                    if (p.Row != null)
                    {
                        int factor = _assemblyQuantity == null ? 1 : Math.Max(1, (int)_assemblyQuantity.Value);
                        long qty = (long)Math.Max(0, p.Row.BaseQuantity) * factor;
                        p.Row.Quantity = qty > int.MaxValue ? int.MaxValue : (int)qty;
                        if (!_allRows.Contains(p.Row)) _allRows.Add(p.Row);
                        if (!_rows.Contains(p.Row) && PassesLaserFilters(p.Row))
                        {
                            p.Row.RowNumber = _rows.Count + 1;
                            _rows.Add(p.Row);
                        }
                    }
                    string stage = p.CacheHit ? "TARAMA • CACHE" : "TARAMA • ANALİZ";
                    string part = p.PartName + (string.IsNullOrWhiteSpace(p.ResourceText) ? "" : "   |   " + p.ResourceText);
                    SetProgress(p.Index, p.Total, stage, part, p.Row == null ? null : p.Row.PartCode, scanWatch.Elapsed, EstimateRemaining(scanWatch.Elapsed, p.Index, p.Total));
                    try { automationScope?.CleanupTransientDocuments(); } catch { }
                    Application.DoEvents();
                }, () => _cancelRequested || escapeMonitor.IsCancellationRequested,
                    true,
                    _includeHidden == null || _includeHidden.Checked,
                    _includePassive != null && _includePassive.Checked);

                if (_cancelRequested)
                    throw new OperationCanceledException("Tarama iptal edildi.");

                // Canlı tarama sırasında satırlar zaten eklendi. Sonunda sıralı ve tekil görünüm oluştur.
                _allRows.Clear();
                _allRows.AddRange(result.Rows.OrderBy(x => x.PartCode, StringComparer.OrdinalIgnoreCase));
                ApplyAssemblyMultiplier();
                ApplyLaserFilter();

                scanWatch.Stop();
                _lastScanDuration = scanWatch.Elapsed;
                int blocked = _allRows.Count(r => r.HasBlockingError);
                int tagged = _allRows.Count(r => r.IsLaserTagged);
                int weldTagged = _allRows.Count(r => r.IsWeldTagged);
                string scopeText = "Gizli " + ((_includeHidden == null || _includeHidden.Checked) ? "dahil" : "hariç") +
                                   " / Pasif " + ((_includePassive != null && _includePassive.Checked) ? "dahil" : "hariç");
                _summaryStats = $"Bileşen: {result.ComponentOccurrences}   |   Gizli atlandı: {result.HiddenOccurrencesSkipped}   |   Pasif atlandı: {result.PassiveOccurrencesSkipped}   |   Kapsam: {scopeText}   |   Benzersiz: {result.UniquePartConfigurations}   |   Hafif: {result.LightweightOccurrences}   |   Sac: {_allRows.Count}   |   LAZER etiketli: {tagged}   |   KAYNAK etiketli: {weldTagged}   |   Cache: {result.CacheHits}/{result.UniquePartConfigurations}   |   Gösterilen: {_rows.Count}   |   Blokaj: {blocked}";
                _summary.Text = "İlerleme: tamamlandı   |   Tarama: " + FormatDuration(_lastScanDuration) + "   |   " + _summaryStats;
                if (_progressBar != null) _progressBar.Value = _progressBar.Maximum;
                _status.Text = result.Warnings.Count == 0
                    ? "Analiz tamamlandı. Kapsam: " + scopeText + "."
                    : "Analiz tamamlandı. Kapsam: " + scopeText + "; bazı bileşenler için uyarı var, log dosyasını kontrol edin.";
                _resume.AfterScan();
                _generate.Enabled = _rows.Count > 0;

            }
            catch (OperationCanceledException)
            {
                _cancelRequested = true;
                _status.Text = "Tarama ESC/kullanıcı isteği ile durduruldu. Kaynak dosyalar kaydedilmedi.";
                _summary.Text = "İlerleme: kullanıcı iptali" + (string.IsNullOrWhiteSpace(_summaryStats) ? "" : "   |   " + _summaryStats);
            }
            catch (InvalidOperationException ex) when ((ex.Message ?? "").IndexOf("kaynakları güvenli sınırı", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Logger.Error("Montaj taraması kaynak korumasıyla durdu", ex);
                _status.Text = "Tarama kaynak koruması nedeniyle durdu: " + ex.Message;
                _summary.Text = "İlerleme: kaynak koruması" + (string.IsNullOrWhiteSpace(_summaryStats) ? "" : "   |   " + _summaryStats);
            }
            catch (Exception ex)
            {
                Logger.Error("Montaj tarama hatası", ex);
                MessageBox.Show(this, ex.Message, "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _status.Text = "Analiz başarısız.";
            }
            finally
            {
                try { automationScope?.CleanupTransientDocuments(); } catch { }
                try { automationScope?.Dispose(); } catch { }
                try { dialogGuard?.Dispose(); } catch { }
                try { escapeMonitor.Dispose(); } catch { }
                UseWaitCursor = false;
                _busy = false;
                if (_onlyLaserTagged != null) _onlyLaserTagged.Enabled = true;
                if (_onlyWeldTagged != null) _onlyWeldTagged.Enabled = true;
                if (!_closeAfterCancel && Visible) RefreshPreview();
                RestoreIdleCursor();
                if (_close != null) _close.Text = "Kapat";
                _pauseRequested = false;
                _processingPreviewPartCode = "";
                if (_pause != null) { _pause.Enabled = false; _pause.Text = "Duraklat"; }
                if (_rescan != null) _rescan.Enabled = true;
                if (_telegramSettings != null) _telegramSettings.Enabled = true;
                if (_generate != null) _generate.Enabled = _rows.Count > 0;
                if (_restoreHidden != null) _restoreHidden.Enabled = _visibilityTracker != null && _visibilityTracker.HiddenOccurrenceCount > 0;
                if (_closeAfterCancel) BeginInvoke(new Action(Close));
            }
        }

        private bool PassesLaserFilters(LaserPartRow row)
        {
            return row != null && LaserOperation.MatchesProcessFilter(row.IsLaserTagged, row.IsWeldTagged,
                _onlyLaserTagged != null && _onlyLaserTagged.Checked,
                _onlyWeldTagged != null && _onlyWeldTagged.Checked)
                && !_excludedControlValues.Contains(ControlFilterValue(row));
        }

        private string ProcessFilterText()
        {
            bool laser = _onlyLaserTagged != null && _onlyLaserTagged.Checked;
            bool weld = _onlyWeldTagged != null && _onlyWeldTagged.Checked;
            if (laser && weld) return "İşlem Tipi = LAZER veya KAYNAK";
            if (laser) return "İşlem Tipi = LAZER";
            if (weld) return "İşlem Tipi = KAYNAK";
            return "Tüm sac parçalar (etiket filtresi kapalı)";
        }

        private void ApplyLaserFilter()
        {
            if (_grid != null) _grid.EndEdit();
            IEnumerable<LaserPartRow> source = _allRows.Where(PassesLaserFilters);

            _rows.RaiseListChangedEvents = false;
            _rows.Clear();
            foreach (var row in source) _rows.Add(row);
            _rows.RaiseListChangedEvents = true;
            _rows.ResetBindings();
            if (!string.IsNullOrWhiteSpace(_sortProperty))
                ApplyCurrentGridSort();
            else
                UpdateRowNumbers();

            if (_generate != null) _generate.Enabled = !_busy && _rows.Count > 0;
            var controlColumn = _grid?.Columns.Cast<DataGridViewColumn>().FirstOrDefault(column => column.DataPropertyName == "ControlText");
            if (controlColumn != null) controlColumn.HeaderText = _excludedControlValues.Count > 0 ? "Kontrol (Filtre)" : "Kontrol";
            if (!_busy)
            {
                string countText = "Gösterilen: " + _rows.Count;
                _summaryStats = System.Text.RegularExpressions.Regex.Replace(_summaryStats, @"Gösterilen: \d+", countText);
                if (_summary != null) _summary.Text = System.Text.RegularExpressions.Regex.Replace(_summary.Text, @"Gösterilen: \d+", countText);
                if (_status != null) _status.Text = countText + " / " + _allRows.Count + " satır • "
                    + ProcessFilterText();
            }
            RefreshPreview();
        }

        private ProductionRecoveryState PrepareRecoveryIfAvailable(LaserProjectInfo info, out HashSet<string> recoverySkips)
        {
            recoverySkips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ProductionRecoveryState state = ProductionRecoveryStore.Load();
            if (state == null) return null;

            // Çok eski veya başka projeye ait kayıt yeni üretime karışmasın.
            if (state.StartedUtc != DateTime.MinValue && DateTime.UtcNow - state.StartedUtc > TimeSpan.FromDays(7))
            {
                ProductionRecoveryStore.DeleteCache(state.RunId);
                ProductionRecoveryStore.Delete();
                return null;
            }
            if (!RecoveryMatches(info, state)) return null;

            string stage = string.IsNullOrWhiteSpace(state.Stage) ? "bilinmiyor" : state.Stage;
            string part = string.IsNullOrWhiteSpace(state.PartCode) ? "-" : state.PartCode;
            string last = state.LastProgressUtc == DateTime.MinValue ? "-" : state.LastProgressUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
            string hung = state.HangDetected ? "\nWatchdog: SOLIDWORKS yanıt vermiyor uyarısı kaydedilmiş." : "";
            string message =
                "Yarım kalan bir SrcCadWorks üretimi bulundu.\n\n" +
                "Aşama: " + stage + "\n" +
                "Parça: " + part + "\n" +
                "İlerleme: " + state.Index + "/" + state.Total + "\n" +
                "Son ilerleme: " + last + hung + "\n" +
                "Çıktı klasörü: " + (string.IsNullOrWhiteSpace(state.OutputFolder) ? "-" : state.OutputFolder) + "\n" +
                "Tamamlanan DXF: " + (state.CompletedDxfPartCodes == null ? 0 : state.CompletedDxfPartCodes.Count) +
                "   |   PDF: " + (state.CompletedPdfPartCodes == null ? 0 : state.CompletedPdfPartCodes.Count) + "\n\n" +
                "EVET: Takıldığı görünen parçayı bu koşuda atla ve kayıtlı seçeneklerle devam et.\n" +
                "HAYIR: Kayıtlı seçeneklerle devam et, takılan parçayı tekrar dene.\n" +
                "İPTAL: Kurtarma kaydını sil ve mevcut seçimlerle yeni işlem başlat.\n\n" +
                "Not: DXF/PDF yalnız kaynak dosyanın SHA-256 izi, tam yolu, konfigürasyonu ve export seçenekleri birebir aynıysa tekrar kullanılabilir. Geometri/delik/büküm/ölçü/kalınlık değişirse yeniden üretilir.";

            DialogResult answer = MessageBox.Show(this, message, "SrcCadWorks — Yarım Kalan İşlem",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

            if (answer == DialogResult.Cancel)
            {
                ProductionRecoveryStore.DeleteCache(state.RunId);
                ProductionRecoveryStore.Delete();
                return null;
            }

            ApplyRecoveryState(state);
            // V1.46+: yalnız fingerprint'li kimlikler atlanabilir.
            // Eski sürümlerin yalnız parça kodu tutan skip kayıtları güvenli kabul edilmez.
            foreach (string oldSkip in state.RecoverySkippedPartKeys ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(oldSkip)) recoverySkips.Add(oldSkip.Trim());

            if (answer == DialogResult.Yes)
            {
                string key = state.CurrentPartRecoveryKey ?? "";
                // Kaynak dosya ARGE sırasında değiştiyse eski recovery key artık eşleşmez;
                // böylece aynı parça kodu olsa bile yeni geometri kesinlikle tekrar üretilir.
                if (!string.IsNullOrWhiteSpace(key))
                    recoverySkips.Add(key);
                else
                    _status.Text = "Eski recovery kaydı fingerprint içermiyor; güvenlik için takılan parça yeniden denenecek.";
            }
            return state;
        }

        private bool RecoveryMatches(LaserProjectInfo info, ProductionRecoveryState state)
        {
            if (info == null || state == null) return false;
            string currentCode = (info.FileCode ?? "").Trim();
            string savedCode = (state.FileCode ?? "").Trim();
            if (currentCode.Length > 0 && savedCode.Length > 0)
                return string.Equals(currentCode, savedCode, StringComparison.OrdinalIgnoreCase);

            string currentName = (info.FileName ?? "").Trim();
            string savedName = (state.FileName ?? "").Trim();
            return currentName.Length > 0 && savedName.Length > 0 &&
                   string.Equals(currentName, savedName, StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyRecoveryState(ProductionRecoveryState state)
        {
            if (state == null) return;
            var selectedPartKeys = new HashSet<string>(
                (state.SelectedParts ?? new List<ProductionRecoveryPartState>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.RecoveryKey))
                    .Select(x => x.RecoveryKey),
                StringComparer.OrdinalIgnoreCase);

            if (selectedPartKeys.Count > 0)
            {
                foreach (var row in _rows)
                    row.Selected = row != null && selectedPartKeys.Contains(OutputProvenanceStore.BuildRecoveryKey(row));
            }
            else
            {
                // Eski recovery dosyaları için yalnız seçim geri yükleme fallback'i.
                // Bu bilgi çıktı güncelliği kararı için kullanılmaz.
                var selectedCodes = new HashSet<string>(state.SelectedPartCodes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                if (selectedCodes.Count > 0)
                    foreach (var row in _rows)
                        row.Selected = row != null && selectedCodes.Contains(row.PartCode ?? "");
            }

            if (_exportDxf != null) _exportDxf.Checked = state.ExportDxf;
            if (_exportPdf != null) _exportPdf.Checked = state.ExportPdf;
            if (_exportExcel != null) _exportExcel.Checked = state.ExportExcel;
            if (_bendLines != null) _bendLines.Checked = state.IncludeBendLines;
            if (_exportCombinedDxf != null) _exportCombinedDxf.Checked = state.ExportCombinedDxf;
            if (_exportCombinedDwg != null) _exportCombinedDwg.Checked = state.ExportCombinedDwg;
            SetSpeedChoice(state.ProcessingSpeed, false);
            if (_output != null && !string.IsNullOrWhiteSpace(state.OutputFolder)) _output.Text = state.OutputFolder;
            if (_template != null && !string.IsNullOrWhiteSpace(state.TemplatePath)) _template.Text = state.TemplatePath;
        }

        private bool ConfirmOtherSolidWorksProcesses()
        {
            try
            {
                int currentPid = Process.GetCurrentProcess().Id;
                var others = Process.GetProcessesByName("SLDWORKS")
                    .Where(p => p != null && p.Id != currentPid)
                    .ToList();
                if (others.Count == 0) return true;

                var details = new List<string>();
                foreach (var process in others)
                {
                    try
                    {
                        string title = process.MainWindowTitle ?? "";
                        details.Add("PID " + process.Id + (string.IsNullOrWhiteSpace(title) ? " (başlıksız/arka plan)" : " – " + title));
                    }
                    catch { details.Add("PID " + process.Id); }
                }

                string message = "Bilgisayarda bu SOLIDWORKS dışında " + others.Count + " adet ek SLDWORKS süreci bulundu.\n\n" +
                    string.Join("\n", details) +
                    "\n\nÖzellikle başlıksız ve yüksek CPU kullanan eski/orphan SOLIDWORKS süreci DXF/PDF otomasyonunu kararsızlaştırabilir. " +
                    "Devam etmeden önce Görev Yöneticisi'nden gerçekten kullanılmayan süreci kapatmanız önerilir.\n\n" +
                    "Yine de üretime devam etmek istiyor musunuz?";
                return MessageBox.Show(this, message, "SrcCadWorks — Ek SOLIDWORKS Süreci",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            catch (Exception ex)
            {
                Logger.Info("Ek SOLIDWORKS süreç kontrolü yapılamadı: " + ex.Message);
                return true;
            }
        }

        private void Generate(object sender, EventArgs e)
        {
            if (_busy) return;
            ModalDialogGuard dialogGuard = null;
            SolidWorksAutomationScope automationScope = null;
            TelegramRunNotifier telegramNotifier = null;
            var escapeMonitor = new EscapeCancellationMonitor();
            try
            {
                _grid.EndEdit();

                var info = new LaserProjectInfo
                {
                    ProjectName = _project.Text.Trim(),
                    FileName = _fileName.Text.Trim(),
                    FileCode = _fileCode.Text.Trim(),
                    Designer = _designer.Text.Trim(),
                    Preparer = _preparer.Text.Trim(),
                    RequestReason = _reason.Text.Trim(),
                    Note = _note.Text.Trim()
                };

                HashSet<string> recoverySkips;
                ProductionRecoveryState recoveryState = null;
                recoverySkips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var selected = _rows.Where(r => r.Selected).ToList();
                if (selected.Any(r => r.HasBlockingError))
                {
                    MessageBox.Show(this, "Kırmızı kontrol hatası bulunan seçili satırlar var. Bu satırları düzeltin veya seçimlerini kaldırın.", "Kontrol", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (_exportExcel.Checked && !File.Exists(_template.Text)) { MessageBox.Show(this, "Lazer formu şablonu bulunamadı."); return; }
                if (string.IsNullOrWhiteSpace(_output.Text)) { MessageBox.Show(this, "Çıktı klasörünü seçin."); return; }
                if (!ConfirmOtherSolidWorksProcesses()) return;
                RestoreHiddenComponents(false);
                if (_hideProcessed != null && _hideProcessed.Checked)
                {
                    _visibilityTracker = new AssemblyVisibilityTracker(_swApp);
                    _hiddenProcessedRows = 0;
                    _hiddenRowsSinceRedraw = 0;
                    if (_restoreHidden != null) _restoreHidden.Enabled = false;
                }

                var options = new ProductionPackageOptions
                {
                    ExportDxf = _exportDxf.Checked,
                    ExportPdf = _exportPdf.Checked,
                    ExportExcel = _exportExcel.Checked,
                    IncludeBendLines = _bendLines.Checked,
                    ExportCombinedDxf = _exportCombinedDxf.Checked,
                    ExportCombinedDwg = _exportCombinedDwg.Checked,
                    ProcessingSpeed = SelectedProcessingSpeed()
                };

                _busy = true;
                if (_onlyLaserTagged != null) _onlyLaserTagged.Enabled = false;
                if (_onlyWeldTagged != null) _onlyWeldTagged.Enabled = false;
                _cancelRequested = false;
                _closeAfterCancel = false;
                if (_close != null) _close.Text = "İptal";
                if (_rescan != null) _rescan.Enabled = false;
                if (_telegramSettings != null) _telegramSettings.Enabled = false;
                SetSpeedSelectorEnabled(false);
                if (_pause != null) { _pause.Enabled = true; _pause.Text = "Duraklat"; }
                EnsureReadOnlyNoSavePrompt();
                ResetProgress();
                // Üretim modeless çalışır; bekleme imleci yerine ilerleme satırı kullanılır.
                // Böylece kullanıcı SOLIDWORKS model alanında döndürme/zoom/seçim yapmaya devam edebilir.
                UseWaitCursor = false;
                _generate.Enabled = false;
                _status.Text = "Üretim başladı • ESC = DURDUR";

                dialogGuard = new ModalDialogGuard(null);
                automationScope = new SolidWorksAutomationScope(_swApp, true, true, false, false);

                string operationName = !string.IsNullOrWhiteSpace(info.ProjectName) ? info.ProjectName :
                    (!string.IsNullOrWhiteSpace(info.FileName) ? info.FileName :
                    (!string.IsNullOrWhiteSpace(info.FileCode) ? info.FileCode : "Üretim Paketi"));
                var telegramSettings = TelegramSettingsStore.Load();
                telegramNotifier = new TelegramRunNotifier(telegramSettings, operationName, selected.Count, false);
                _activeTelegramNotifier = telegramNotifier;
                _productionRunning = true;
                telegramNotifier.Start();

                string productionFolder = _resume.Folder ?? EnsureLaserFolder(_output.Text);
                Directory.CreateDirectory(productionFolder);
                _output.Text = productionFolder;
                productionFolder = _resume.Begin(productionFolder);
                Logger.Info("Üretim çıktı klasörü: " + productionFolder);

                _activeRecoveryManager = new ProductionRecoveryManager();
                _activeRecoveryManager.Begin(info, operationName, productionFolder, _template.Text, options, selected, 120, recoverySkips, recoveryState);
                // V1.11.17: watchdog ayrı EXE olarak çalışmaya devam eder. SOLIDWORKS/COM tamamen
                // kilitlense bile yardımcı süreç recovery dosyasını izlemeye ve Telegram uyarısı
                // göndermeye devam eder. EXE herhangi bir nedenle başlatılamazsa eski süreç-içi
                // watchdog yedek olarak devreye girer.
                _externalWatchdog = new ExternalWatchdogLauncher();
                bool externalStarted = _externalWatchdog.Start(Process.GetCurrentProcess().Id);
                if (!externalStarted)
                {
                    try { _externalWatchdog.Dispose(); } catch { }
                    _externalWatchdog = null;
                    _productionHangWatchdog = new ProductionHangWatchdog(_activeRecoveryManager, telegramSettings);
                    _productionHangWatchdog.Start();
                }

                var service = new ProductionPackageService(_swApp);
                var result = service.Generate(_rows.ToList(), info, _template.Text, productionFolder, options,
                    p =>
                    {
                        _activeRecoveryManager?.Touch(p);
                        telegramNotifier?.Update(p);
                        _status.Text = (p.Message ?? "") + (string.IsNullOrWhiteSpace(p.ResourceText) ? "" : "   |   " + p.ResourceText);
                        int pi = p.OverallTotal > 0 ? p.OverallIndex : p.Index;
                        int pt = p.OverallTotal > 0 ? p.OverallTotal : p.Total;
                        SetProgress(pi, pt, p.Stage, p.PartCode, p.PartCode, p.Elapsed, p.EstimatedRemaining);
                        if (!p.ReusedOutput) UpdateProcessingPreview(p.PartCode);
                        try { automationScope?.CleanupTransientDocuments(); } catch { }
                        Application.DoEvents();
                    },
                    () => _cancelRequested || escapeMonitor.IsCancellationRequested,
                    row =>
                    {
                        if (_hideProcessed == null || !_hideProcessed.Checked || _visibilityTracker == null || row == null) return;
                        int hiddenOccurrences = _visibilityTracker.HideOccurrences(row);
                        if (hiddenOccurrences > 0)
                        {
                            _hiddenProcessedRows++;
                            _hiddenRowsSinceRedraw++;
                            if (_restoreHidden != null) _restoreHidden.Enabled = true;
                            if (_hiddenRowsSinceRedraw >= 8)
                            {
                                _visibilityTracker.Redraw();
                                _hiddenRowsSinceRedraw = 0;
                            }
                        }
                    },
                    CooperativeYield,
                    recoverySkips,
                    (stage, partCode, path) =>
                    {
                        try { _activeRecoveryManager?.MarkOutputCommitted(stage, partCode, path); } catch { }
                    }, _resume);

                // V1.11.19: Sadece Excel üretiminde işlenenleri tek tek gizlemek yerine
                // montaj bileşenleri yalnız bir kez taranır. Büyük montajlarda O(parça x bileşen)
                // COM çağrısı oluşmasını ve SOLIDWORKS'ün uzun süre yanıtsız kalmasını önler.
                bool excelOnlyRun = options.ExportExcel && !options.ExportDxf && !options.ExportPdf &&
                    !options.ExportCombinedDxf && !options.ExportCombinedDwg;
                if (excelOnlyRun && result.ExcelCreated && _hideProcessed != null && _hideProcessed.Checked &&
                    _visibilityTracker != null && selected.Count > 0)
                {
                    _status.Text = "Excel hazır. İşlenen parçalar montajda toplu gizleniyor...";
                    Application.DoEvents();
                    int hiddenOccurrences = _visibilityTracker.HideOccurrencesBatch(selected, (done, total) =>
                    {
                        string msg = "İşlenen parçalar gizleniyor: " + done + "/" + total;
                        _status.Text = msg;

                        // V1.11.21: Gizleme gerçek bir üretim aşaması olarak watchdog'a heartbeat verir.
                        // Böylece binlerce occurrence gizlenirken yanlış "SOLIDWORKS yanıt vermiyor" uyarısı oluşmaz.
                        var hideProgress = new PackageProgress
                        {
                            Stage = "GİZLE",
                            Index = done,
                            Total = Math.Max(1, total),
                            PartCode = "",
                            Message = msg,
                            OverallIndex = 1,
                            OverallTotal = 1,
                            Elapsed = TimeSpan.Zero,
                            EstimatedRemaining = null
                        };
                        try { _activeRecoveryManager?.Touch(hideProgress); } catch { }
                        try { telegramNotifier?.Update(hideProgress); } catch { }
                        Application.DoEvents();
                    });
                    if (hiddenOccurrences > 0)
                    {
                        _hiddenProcessedRows += selected.Count;
                        if (_restoreHidden != null) _restoreHidden.Enabled = true;
                    }
                    _visibilityTracker.Redraw();
                    _hiddenRowsSinceRedraw = 0;
                }

                try { _externalWatchdog?.Dispose(); } catch { }
                _externalWatchdog = null;
                try { _productionHangWatchdog?.Dispose(); } catch { }
                _productionHangWatchdog = null;
                _resume.Finish(result.Errors.Count == 0);
                _activeRecoveryManager?.Complete();
                _activeRecoveryManager = null;

                _visibilityTracker?.Redraw();
                _hiddenRowsSinceRedraw = 0;

                if (escapeMonitor.IsCancellationRequested) _cancelRequested = true;
                if (_cancelRequested)
                    throw new OperationCanceledException("Üretim işlemi ESC/kullanıcı isteği ile iptal edildi.");

                telegramNotifier?.Complete(result, options);

                _output.Text = EnsureLaserFolder(_output.Text);
                _status.Text = "Tamamlandı: " + _output.Text;
                TimeSpan totalDuration = _lastScanDuration + result.ProductionDuration;
                double avgSeconds = result.SelectedPartCount > 0 ? result.ProductionDuration.TotalSeconds / result.SelectedPartCount : 0;
                string message = "Üretim paketi hazırlandı.\n\n" +
                    "DXF yeni: " + result.DxfCreated + "   |   güncel atlandı: " + result.DxfSkipped + "\n" +
                    "PDF yeni: " + result.PdfCreated + "   |   güncel atlandı: " + result.PdfSkipped + "\n" +
                    "Toplu DXF: " + (result.CombinedDxfCreated ? "oluşturuldu" : "seçilmedi/oluşturulamadı") + "\n" +
                    "Toplu DWG: " + (result.CombinedDwgCreated ? "oluşturuldu" : "seçilmedi/oluşturulamadı") + "\n" +
                    "Teknik resmi olmayan: " + result.PdfMissingDrawing + "\n" +
                    "Kurtarmada atlanan: " + result.RecoverySkippedPartCodes.Count + "\n" +
                    "Hata: " + result.Errors.Count + "\n" +
                    "Montajda gizlenen parça satırı: " + _hiddenProcessedRows + "\n\n" +
                    "Tarama süresi: " + FormatDuration(_lastScanDuration) + "\n" +
                    "DXF süresi: " + FormatDuration(result.DxfDuration) + "\n" +
                    "PDF süresi: " + FormatDuration(result.PdfDuration) + "   |   Hız: " + SelectedProcessingSpeedText() + "\n" +
                    "Excel süresi: " + FormatDuration(result.ExcelDuration) + "\n" +
                    "Toplu DXF süresi: " + FormatDuration(result.CombinedDxfDuration) + "\n" +
                    "Toplu DWG süresi: " + FormatDuration(result.CombinedDwgDuration) + "\n" +
                    "Üretim süresi: " + FormatDuration(result.ProductionDuration) + "\n" +
                    "Toplam süre: " + FormatDuration(totalDuration) +
                    (avgSeconds > 0 ? "\nOrtalama: " + avgSeconds.ToString("0.0") + " sn/parça" : "");
                if (result.Errors.Count > 0)
                {
                    message += "\n\nBir parça hata verse bile diğer dosyalar oluşturulmaya devam edildi.";
                    message += "\nİlk hatalar:\n" + string.Join("\n", result.Errors.Take(5));
                    if (!string.IsNullOrWhiteSpace(result.ErrorReportPath))
                        message += "\n\nHata raporu: " + Path.GetFileName(result.ErrorReportPath);
                }
                message += "\n\nKlasörü açmak ister misiniz?";

                var answer = MessageBox.Show(this, message, "SrcCadWorks", MessageBoxButtons.YesNo,
                    result.Errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes) Process.Start("explorer.exe", _output.Text);
            }
            catch (OperationCanceledException)
            {
                try { _externalWatchdog?.Dispose(); } catch { }
                _externalWatchdog = null;
                try { _productionHangWatchdog?.Dispose(); } catch { }
                _productionHangWatchdog = null;
                try { _activeRecoveryManager?.Complete(); } catch { }
                _activeRecoveryManager = null;
                telegramNotifier?.Cancelled();
                _status.Text = "Üretim işlemi ESC/kullanıcı isteği ile iptal edildi. Oluşturulmuş dosyalar bırakıldı; kaynak SOLIDWORKS belgeleri kaydedilmedi.";
                _summary.Text = "İlerleme: ESC/kullanıcı iptali" + (string.IsNullOrWhiteSpace(_summaryStats) ? "" : "   |   " + _summaryStats);
            }
            catch (InvalidOperationException ex) when ((ex.Message ?? "").IndexOf("kaynakları güvenli sınırı", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                telegramNotifier?.Failed(ex);
                Logger.Error("Üretim kaynak korumasıyla durdu", ex);
                _status.Text = "Kaynak koruması: " + ex.Message + " Oluşturulmuş dosyalar bırakıldı; Kaldığın Yerden Devam Et ile doğrulanmış çıktılar korunarak sürdürülebilir.";
                _summary.Text = "İlerleme: kaynak koruması" + (string.IsNullOrWhiteSpace(_summaryStats) ? "" : "   |   " + _summaryStats);
            }
            catch (Exception ex)
            {
                telegramNotifier?.Failed(ex);
                Logger.Error("Üretim paketi oluşturma hatası", ex);
                MessageBox.Show(this, ex.Message, "Üretim oluşturulamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _status.Text = "Hata: " + ex.Message;
            }
            finally
            {
                _resume.Suspend();
                try { _externalWatchdog?.Dispose(); } catch { }
                _externalWatchdog = null;
                try { _productionHangWatchdog?.Dispose(); } catch { }
                _productionHangWatchdog = null;
                _activeRecoveryManager = null;
                try { telegramNotifier?.Dispose(); } catch { }
                try { automationScope?.CleanupTransientDocuments(); } catch { }
                try { automationScope?.Dispose(); } catch { }
                try { dialogGuard?.Dispose(); } catch { }
                try { escapeMonitor.Dispose(); } catch { }
                UseWaitCursor = false;
                _productionRunning = false;
                _pauseRequested = false;
                if (_pause != null) { _pause.Enabled = false; _pause.Text = "Duraklat"; }
                _busy = false;
                if (_onlyLaserTagged != null) _onlyLaserTagged.Enabled = true;
                if (_onlyWeldTagged != null) _onlyWeldTagged.Enabled = true;
                if (!_closeAfterCancel && Visible) RefreshPreview();
                RestoreIdleCursor();
                if (_close != null) _close.Text = "Kapat";
                if (_rescan != null) _rescan.Enabled = true;
                if (_telegramSettings != null) _telegramSettings.Enabled = true;
                SetSpeedSelectorEnabled(true);
                if (_generate != null) _generate.Enabled = _rows.Count > 0;
                if (_closeAfterCancel) BeginInvoke(new Action(Close));
            }
        }

        private void RestoreHiddenComponents(bool closing)
        {
            try
            {
                if (_visibilityTracker == null)
                {
                    if (_restoreHidden != null) _restoreHidden.Enabled = false;
                    return;
                }

                int restored = _visibilityTracker.RestoreAll();
                _visibilityTracker.Dispose();
                _visibilityTracker = null;
                _hiddenProcessedRows = 0;
                _hiddenRowsSinceRedraw = 0;
                if (_restoreHidden != null) _restoreHidden.Enabled = false;

                if (!closing && _status != null)
                    _status.Text = restored > 0
                        ? "SrcCadWorks tarafından gizlenen " + restored + " montaj bileşeni geri getirildi."
                        : "SrcCadWorks tarafından geri getirilecek gizli bileşen yok.";
            }
            catch (Exception ex)
            {
                Logger.Error("Gizlenen montaj bileşenleri geri getirilemedi", ex);
                if (!closing && _status != null) _status.Text = "Gizlenen bileşenleri geri getirme uyarısı: " + ex.Message;
            }
        }

        private void RestoreIdleCursor()
        {
            if (_busy || IsDisposed || Disposing) return;
            ClearWaitCursor(this);
            System.Windows.Forms.Cursor.Current = Cursors.Default;
        }

        private static void ClearWaitCursor(Control control)
        {
            control.UseWaitCursor = false;
            foreach (Control child in control.Controls) ClearWaitCursor(child);
        }

        private static string PreviewFileStamp(string path)
        {
            try { var file = new FileInfo(path); return file.Exists ? file.LastWriteTimeUtc.Ticks + ":" + file.Length : ""; }
            catch { return ""; }
        }

        private void RefreshPreview(bool forceDuringBusy = false)
        {
            if (IsDisposed || Disposing) return;
            if (_busy && !forceDuringBusy) return;
            if (_loadingRowPreview) { _pendingRowPreview = true; return; }
            LaserPartRow row = SelectedRow();
            string key = row == null ? "" : (row.SourcePath ?? "") + "|" + (row.Configuration ?? "") + "|" + (row.DrawingPath ?? "") + "|" + PreviewModePreference.PreferDrawing + "|" + PreviewFileStamp(row.SourcePath) + "|" + PreviewFileStamp(row.DrawingPath);
            _openPart.Enabled = row != null && File.Exists(row.SourcePath);
            _openDrawing.Enabled = row != null && row.HasDrawing;
            if (key == _loadedRowPreviewKey) { RestoreIdleCursor(); return; }
            _loadingRowPreview = true;
            try
            {
                _eDrawingsPreview?.LoadRow(row);
                _loadedRowPreviewKey = key;
            }
            catch (Exception ex)
            {
                Logger.Info("eDrawings satır önizlemesi yüklenemedi: " + ex.Message);
            }
            finally
            {
                _loadingRowPreview = false;
                RestoreIdleCursor();
                if (_pendingRowPreview && !IsDisposed && !Disposing)
                {
                    _pendingRowPreview = false;
                    BeginInvoke(new Action(() => RefreshPreview()));
                }
            }
        }

        private void UpdateProcessingPreview(string partCode)
        {
            if (!_busy || string.IsNullOrWhiteSpace(partCode)) return;
            if (string.Equals(_processingPreviewPartCode, partCode, StringComparison.OrdinalIgnoreCase)) return;

            LaserPartRow row = _rows.FirstOrDefault(r =>
                string.Equals(r.PartCode, partCode, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;

            _processingPreviewPartCode = partCode;

            try
            {
                if (_grid != null)
                {
                    _grid.ClearSelection();
                    foreach (DataGridViewRow gridRow in _grid.Rows)
                    {
                        var data = gridRow.DataBoundItem as LaserPartRow;
                        if (data == row || (data != null && string.Equals(data.PartCode, partCode, StringComparison.OrdinalIgnoreCase)))
                        {
                            gridRow.Selected = true;
                            if (gridRow.Cells.Count > 0) _grid.CurrentCell = gridRow.Cells[0];
                            try { _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, gridRow.Index - 2); } catch { }
                            break;
                        }
                    }
                }

                // Üretim sırasında normal SelectionChanged preview'i performans nedeniyle engelli.
                // Burada yalnız parça gerçekten değiştiğinde eDrawings 3D görünümünü bir kez yeniliyoruz.
                RefreshPreview(true);
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                Logger.Info("Aktif işlem eDrawings önizlemesi yenilenemedi: " + ex.Message);
            }
        }

        private void TogglePause()
        {
            if (!_productionRunning) return;
            SetPauseRequested(!_pauseRequested, false);
        }

        private void SetPauseRequested(bool pause, bool fromTelegram)
        {
            if (!_productionRunning) return;
            _pauseRequested = pause;
            if (_pause != null) _pause.Text = _pauseRequested ? "Devam Et" : "Duraklat";

            if (_status != null)
            {
                if (_pauseRequested)
                    _status.Text = (fromTelegram ? "Telegram: " : "") + "Duraklatma istendi. Geçerli parça tamamlanınca SrcCadWorks bekleyecek; SOLIDWORKS ile çalışabilirsiniz.";
                else
                    _status.Text = (fromTelegram ? "Telegram: " : "") + "Üretim devam ediyor...";
            }

            Application.DoEvents();
        }

        private void CooperativeYield()
        {
            // SOLIDWORKS COM çağrıları güvenli biçimde ana STA/UI thread üzerinde kalır.
            // Her parça/ana adım arasında Windows mesajlarını işleriz. CommandInProgress uzun
            // üretim boyunca kapalı olduğu için ana SOLIDWORKS penceresi de etkileşimli kalır.
            // Pencereyi görünür tutarız fakat her tur Activate/BringToFront yapıp kullanıcının
            // SOLIDWORKS odağını çalmayız.
            try
            {
                if (!Visible && !_closeAfterCancel) Show();
                // Native SOLIDWORKS owner ilişkisi polling olmadan korunur.
                // Arka plan otomasyonu Chrome/Explorer odağını çalmaz.
                Refresh();
            }
            catch { }
            Application.DoEvents();

            while (_pauseRequested && !_cancelRequested)
            {
                if (_status != null)
                    _status.Text = "DURAKLATILDI • SOLIDWORKS ile çalışabilirsiniz. Devam Et ile üretim kaldığı yerden sürer.";
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }

            if (!_cancelRequested)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(1);
            }
        }

        private LaserPartRow SelectedRow()
        {
            if (_grid == null || _grid.CurrentRow == null) return null;
            return _grid.CurrentRow.DataBoundItem as LaserPartRow;
        }

        private void DisposePreview()
        {
            _loadedRowPreviewKey = null;
            try { _eDrawingsPreview?.CloseDocument(); } catch { }
        }

        private void OpenSelectedPart()
        {
            var row = SelectedRow();
            if (row == null || !File.Exists(row.SourcePath)) return;
            OpenInSolidWorks(row.SourcePath, (int)swDocumentTypes_e.swDocPART, row.Configuration);
        }

        private void OpenSelectedDrawing()
        {
            var row = SelectedRow();
            if (row == null || !row.HasDrawing) return;
            OpenInSolidWorks(row.DrawingPath, (int)swDocumentTypes_e.swDocDRAWING, "");
        }

        private void OpenInSolidWorks(string path, int documentType, string configuration)
        {
            try
            {
                var existing = _swApp.GetOpenDocumentByName(path) as ModelDoc2;
                if (existing != null)
                {
                    int e = 0;
                    _swApp.ActivateDoc3(existing.GetTitle(), true, 0, ref e);
                    return;
                }
                int errors = 0, warnings = 0;
                var doc = _swApp.OpenDoc6(path, documentType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    configuration ?? "", ref errors, ref warnings) as ModelDoc2;
                if (doc == null) MessageBox.Show(this, "Dosya açılamadı. Hata=" + errors, "SrcCadWorks");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SrcCadWorks"); }
        }

        private void GridPartCodeMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (_busy || e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (!string.Equals(_grid.Columns[e.ColumnIndex].DataPropertyName, "PartCode", StringComparison.OrdinalIgnoreCase)) return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as LaserPartRow;
            if (row == null) return;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("SOLIDWORKS'te Aç") { Enabled = File.Exists(row.SourcePath) };
            open.Click += (senderControl, args) =>
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || Disposing || _busy) return;
                    try { OpenInSolidWorks(row.SourcePath, (int)swDocumentTypes_e.swDocPART, row.Configuration); }
                    finally { RestoreIdleCursor(); }
                }));
            };
            menu.Items.Add(open);
            menu.Closed += (senderControl, args) =>
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(() => { if (!menu.IsDisposed) menu.Dispose(); }));
            };
            Rectangle cell = _grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, true);
            menu.Show(_grid, new Point(cell.Left + e.X, cell.Top + e.Y));
        }

        private void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0) OpenSelectedPart();
        }

        private void BrowseTemplate(object sender, EventArgs e)
        {
            IDisposable zOrderPause = null;
            try
            {
                zOrderPause = _ownedWindowTracker?.Suspend();
                using (var d = new OpenFileDialog { Filter = "Excel Workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm", FileName = Path.GetFileName(_template.Text) })
                    if (d.ShowDialog(this) == DialogResult.OK) _template.Text = d.FileName;
            }
            finally
            {
                zOrderPause?.Dispose();
            }
        }

        private void BrowseOutput(object sender, EventArgs e)
        {
            IDisposable zOrderPause = null;
            try
            {
                zOrderPause = _ownedWindowTracker?.Suspend();

            string selected = _output.Text;
            if (!string.IsNullOrWhiteSpace(selected) && string.Equals(Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar)), "LAZER", StringComparison.OrdinalIgnoreCase))
                selected = Path.GetDirectoryName(selected.TrimEnd(Path.DirectorySeparatorChar));

            string picked;
            bool cancelled = false;
            try
            {
                if (ModernFolderPicker.TryPickFolder(Handle, selected, "LAZER klasörünün oluşturulacağı ana klasörü seçin", out picked, out cancelled))
                {
                    _output.Text = EnsureLaserFolder(picked);
                    return;
                }

                // Kullanıcı modern pencerede İptal/Vazgeç dediyse bunu gerçek bir iptal olarak kabul et.
                // Eski klasör seçiciyi ikinci kez açma ve mevcut Çıktı Klasörü yolunu değiştirme.
                if (cancelled) return;
            }
            catch (Exception ex)
            {
                Logger.Info("Modern klasör seçici açılamadı; klasik seçici kullanılacak. " + ex.Message);
            }

            // Yalnız modern seçici gerçekten açılamazsa/COM hatası verirse klasik seçici yedek olarak kullanılır.
            using (var d = new FolderBrowserDialog { Description = "LAZER klasörünün oluşturulacağı ana klasörü seçin", SelectedPath = selected })
                if (d.ShowDialog(this) == DialogResult.OK) _output.Text = EnsureLaserFolder(d.SelectedPath);
            }
            finally
            {
                zOrderPause?.Dispose();
            }
        }

        private void ApplyAssemblyMultiplier()
        {
            int factor = _assemblyQuantity == null ? 1 : Math.Max(1, (int)_assemblyQuantity.Value);
            foreach (var row in _allRows)
            {
                long qty = (long)Math.Max(0, row.BaseQuantity) * factor;
                row.Quantity = qty > int.MaxValue ? int.MaxValue : (int)qty;
            }
            if (_grid != null) _grid.Refresh();
            RefreshPreview();
        }

        private void ResetProgress()
        {
            if (_progressBar != null) { _progressBar.Minimum = 0; _progressBar.Maximum = 100; _progressBar.Value = 0; }
        }

        private void SetProgress(int index, int total, string stage, string partName, string selectPartCode, TimeSpan? elapsed = null, TimeSpan? remaining = null)
        {
            total = Math.Max(1, total);
            index = Math.Max(0, Math.Min(total, index));
            int percent = (int)Math.Round(index * 100.0 / total);
            if (_progressBar != null)
            {
                _progressBar.Maximum = 100;
                _progressBar.Value = Math.Max(0, Math.Min(100, percent));
            }
            string current = string.IsNullOrWhiteSpace(partName) ? "" : "   |   " + partName;
            string timing = "";
            if (elapsed.HasValue)
            {
                timing = "   |   Geçen: " + FormatDuration(elapsed.Value);
                if (remaining.HasValue && remaining.Value.TotalSeconds > 0)
                    timing += "   |   Tahmini kalan: " + FormatDuration(remaining.Value);
            }
            _summary.Text = $"İlerleme: {index}/{total} ({percent}%)   |   {stage}{current}{timing}" + (string.IsNullOrWhiteSpace(_summaryStats) ? "" : "   |   " + _summaryStats);

            if (!string.IsNullOrWhiteSpace(selectPartCode) && _grid != null)
            {
                foreach (DataGridViewRow gridRow in _grid.Rows)
                {
                    var data = gridRow.DataBoundItem as LaserPartRow;
                    if (data != null && string.Equals(data.PartCode, selectPartCode, StringComparison.OrdinalIgnoreCase))
                    {
                        gridRow.Selected = true;
                        if (gridRow.Cells.Count > 0) _grid.CurrentCell = gridRow.Cells[0];
                        try { _grid.FirstDisplayedScrollingRowIndex = gridRow.Index; } catch { }
                        break;
                    }
                }
            }
        }

        private static TimeSpan? EstimateRemaining(TimeSpan elapsed, int index, int total)
        {
            if (index <= 1 || total <= 1 || index > total) return null;
            double secondsPerItem = elapsed.TotalSeconds / Math.Max(1, index - 1);
            if (secondsPerItem <= 0 || double.IsNaN(secondsPerItem) || double.IsInfinity(secondsPerItem)) return null;
            return TimeSpan.FromSeconds(Math.Max(0, secondsPerItem * Math.Max(0, total - index + 1)));
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return string.Format("{0:00}:{1:00}:{2:00}", (int)value.TotalHours, value.Minutes, value.Seconds);
            return string.Format("{0:00}:{1:00}", (int)value.TotalMinutes, value.Seconds);
        }

        private void LaserPrepForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_busy)
            {
                try { _eDrawingsPreview?.Shutdown(); } catch { }
                return;
            }

            // Üretim sürerken pencerenin bir native owner/z-order olayı yüzünden görünmez olup
            // erişilemez hale gelmesine izin verme. Yalnız kullanıcı kapatma isteğinde iptal başlatılır;
            // gerçek Close() üretimin finally bloğunda güvenli noktada yapılır.
            e.Cancel = true;
            if (e.CloseReason == CloseReason.UserClosing)
                RequestCloseWhileBusy();
            else
                Logger.Info("Lazer Hazırla üretim sırasında otomatik kapanma isteği engellendi. CloseReason=" + e.CloseReason);
        }

        private void RequestCloseWhileBusy()
        {
            RequestCancel(true);
            // Pencereyi artık Hide etmiyoruz. Kullanıcı işlem durana kadar durum mesajını görür;
            // bu aynı zamanda 'kendi kapandı ve tekrar açılmıyor' durumunu önler.
            try { if (!Visible) Show(); } catch { }
        }

        private void RequestCancel(bool closeAfter)
        {
            _cancelRequested = true;
            _pauseRequested = false;
            if (closeAfter) _closeAfterCancel = true;
            if (_status != null) _status.Text = "İptal isteniyor... Geçerli parça güvenli şekilde tamamlanınca duracak.";
            if (_close != null) _close.Text = "İptal isteniyor";
            if (_generate != null) _generate.Enabled = false;
            if (_rescan != null) _rescan.Enabled = false;
            Application.DoEvents();
        }

        private void EnsureReadOnlyNoSavePrompt()
        {
            try
            {
                _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave, true);
            }
            catch (Exception ex)
            {
                Logger.Info("Salt-okunur kaydetme uyarısı ayarı uygulanamadı: " + ex.Message);
            }
        }

        private static string EnsureLaserFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            string clean = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(clean), "LAZER", StringComparison.OrdinalIgnoreCase)) return clean;
            return Path.Combine(clean, "LAZER");
        }

        private string FindTemplate()
        {
            string local = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "templates", "LAZER FORMU.xlsx");
            if (File.Exists(local)) return local;
            string dll = typeof(LaserPrepForm).Assembly.Location;
            string packaged = Path.Combine(Path.GetDirectoryName(dll), "templates", "LAZER FORMU.xlsx");
            return File.Exists(packaged) ? packaged : local;
        }

        private string FindLogo()
        {
            string dll = typeof(LaserPrepForm).Assembly.Location;
            string baseDir = Path.GetDirectoryName(dll) ?? "";
            string main = Path.Combine(baseDir, "assets", "SrcCadWorks_Logo_Main.png");
            if (File.Exists(main)) return main;
            string localMain = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "assets", "SrcCadWorks_Logo_Main.png");
            if (File.Exists(localMain)) return localMain;
            string packaged = Path.Combine(baseDir, "assets", "SrcCadWorks_Logo.png");
            if (File.Exists(packaged)) return packaged;
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "assets", "SrcCadWorks_Logo.png");
        }

        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            var row = _rows[e.RowIndex];
            var style = _grid.Rows[e.RowIndex].DefaultCellStyle;
            if (row.HasBlockingError)
            {
                style.BackColor = Color.FromArgb(255, 225, 228);
                style.ForeColor = Color.FromArgb(128, 20, 26);
            }
            else if (!row.HasDrawing)
            {
                style.BackColor = Color.FromArgb(255, 247, 220);
                style.ForeColor = Color.FromArgb(115, 82, 12);
            }
            else
            {
                style.BackColor = CPanel;
                style.ForeColor = CText;
            }
            style.SelectionBackColor = CSelection;
            style.SelectionForeColor = CText;
        }
    }
}
