using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Services;


namespace SrcCadWorks.Viewer
{
    internal sealed class ViewerMainForm : Form
    {
        private enum ViewerListMode { Parts, Assembly, AllAssemblies }

        private readonly string _snapshotPath; 
        private ResumeLedger _sessionLedger;
        private Timer _sessionTimer;
        private bool _exporting;
        private string _lastSession = "", _savedSelection = "";
        private AssemblyViewerSnapshot _snapshot;
        private readonly List<AssemblyViewerItem> _all = new List<AssemblyViewerItem>();
        private readonly List<AssemblyViewerItem> _treeAll = new List<AssemblyViewerItem>();
        private readonly Dictionary<string, AssemblyViewerItem> _treeById =
            new Dictionary<string, AssemblyViewerItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<AssemblyViewerItem>> _treeChildren =
            new Dictionary<string, List<AssemblyViewerItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expandedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly BindingList<AssemblyViewerItem> _view = new BindingList<AssemblyViewerItem>();
        private DataGridView _grid;
        private TextBox _search;
        private NumericUpDown _assemblyQuantity;
        private EDrawingsAssemblyControl _preview;
        private Panel _contentHost;
        private Panel _leftPanel;
        private Splitter _divider;
        private Label _summary;
        private Label _loadProgress;
        private ProgressBar _loadProgressBar;
        private ViewerPreferences _prefs;
        private ContextMenuStrip _rowMenu;
        private ToolStripButton _partsButton;
        private ToolStripButton _assemblyButton;
        private ToolStripButton _allAssembliesButton;
        private readonly Timer _searchTimer = new Timer { Interval = 180 };
        private readonly Timer _previewTimer = new Timer { Interval = 120 };
        private string _lastPreviewKey = "";
        private bool _loadingPreview;
        private readonly Dictionary<AssemblyViewerItem,string> _searchIndex = new Dictionary<AssemblyViewerItem,string>();
        private ViewerListMode _mode = ViewerListMode.Assembly;
        private string _sortProperty = "";
        private bool _sortAscending = true;
        private int _columnInsertAnchor = -1;
        private readonly Image _partIcon = CadDocumentIcons.Part(32);
        private readonly Image _assemblyIcon = CadDocumentIcons.Assembly(32);
        private readonly Image _allAssembliesIcon = CadDocumentIcons.AllAssemblies(32);
        private bool _closingPrepared;
        private bool _refreshingGrid;
        private readonly Timer _liveSnapshotTimer;
        private readonly Timer _hostProcessTimer;
        private readonly int _parentProcessId;
        private string _snapshotJson;
        private bool _assemblyPreviewLoaded;

        private static readonly Color Accent = Color.FromArgb(202, 28, 36);
        private static readonly Color Border = Color.FromArgb(205, 211, 218);
        private static readonly Color Muted = Color.FromArgb(94, 103, 113);
        private static readonly Color Surface = Color.White;
        private static readonly Color SurfaceAlt = Color.FromArgb(249, 250, 252);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public ViewerMainForm(string snapshotPath, int parentProcessId)
        {
            _snapshotPath = snapshotPath;
            _parentProcessId = parentProcessId;
            _snapshotJson = File.ReadAllText(snapshotPath);
            _snapshot = ReadSnapshot(_snapshotJson);
            _all.AddRange(_snapshot?.Items ?? new List<AssemblyViewerItem>());
            _treeAll.AddRange(_snapshot?.TreeItems ?? new List<AssemblyViewerItem>());
            if (_treeAll.Count == 0)
            {
                int fallbackOrder = 0;
                foreach (var x in _all.Where(a => a.TopLevelQuantity > 0))
                {
                    fallbackOrder++;
                    var fallback = CloneForQuantity(x, x.TopLevelQuantity);
                    fallback.NodeId = "F." + fallbackOrder.ToString();
                    fallback.ParentNodeId = "";
                    fallback.Depth = 0;
                    fallback.HasChildren = false;
                    fallback.HierarchyOrder = fallbackOrder;
                    _treeAll.Add(fallback);
                }
            }
            BuildTreeIndexes();

            _prefs = LoadPreferences();
            NormalizePreferences();

            Text = "SrcCadWorks — Montaj Görüntüleme " + AppVersion.Display;
            BrandIcon.Apply(this);
            Width = 1600;
            Height = 940;
            MinimumSize = new Size(1280, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = Color.FromArgb(31, 36, 42);
            BackColor = Color.FromArgb(245, 247, 250);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            _sessionLedger = new ResumeLedger("Viewer|" + (_snapshot.AssemblyPath ?? "").ToUpperInvariant() + "|" + (_snapshot.AssemblyConfiguration ?? ""));
            BuildUi();
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); SafeApplyFilterFromSearch(); };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); LoadSelectedPreview(); };
            _sessionTimer = new Timer { Interval = 1200 };
            _sessionTimer.Tick += (s, e) =>
            {
                if (!_exporting && !_snapshot.IsLoading) SaveViewerSession();
            };
            _sessionTimer.Start();
            ApplyTextRenderingQuality(this);

            _liveSnapshotTimer = new Timer { Interval = 350 };
            _liveSnapshotTimer.Tick += (s, e) => ReloadLiveSnapshotIfChanged();

            _hostProcessTimer = new Timer { Interval = 1000 };
            _hostProcessTimer.Tick += (s, e) => CloseIfHostExited();

            Shown += (s, e) =>
            {
                WindowState = FormWindowState.Maximized;
                if (_parentProcessId > 0) _hostProcessTimer.Start();
                BeginInvoke(new Action(() =>
                {
                    ApplyStoredDividerWidth();
                    SetViewMode(ViewerListMode.Assembly, !_snapshot.IsLoading);
                    RestoreViewerSession();
                    _liveSnapshotTimer.Start();
                    UpdateLoadProgress();
                    if (_snapshot.IsLoading)
                    {
                        if (_summary != null)
                            _summary.Text = (_snapshot.AssemblyTitle ?? "Montaj") + " • montaj okunuyor...";
                    }
                }));
            };

            FormClosing += (s, e) => PrepareForClose();
            FormClosed += (s, e) =>
            {
                try { _liveSnapshotTimer?.Stop(); } catch { }
                try { _liveSnapshotTimer?.Dispose(); } catch { }
                try { _hostProcessTimer?.Stop(); } catch { }
                try { _hostProcessTimer?.Dispose(); } catch { }
                _sessionTimer.Stop(); _sessionTimer.Dispose();
                _searchTimer.Dispose(); _previewTimer.Dispose();
                _partIcon.Dispose(); _assemblyIcon.Dispose(); _allAssembliesIcon.Dispose();
                SaveViewerSession();
                SavePreferences();
                try { File.Delete(_snapshotPath); } catch { }
            };
            Resize += (s, e) => ClampDividerWidth();
        }

        private void CloseIfHostExited()
        {
            if (_parentProcessId <= 0 || IsDisposed || Disposing) return;
            try
            {
                using (var process = Process.GetProcessById(_parentProcessId))
                {
                    if (!process.HasExited) return;
                }
            }
            catch
            {
                // SOLIDWORKS süreci artık yok. Viewer eski sürüm klasörünü kilitlemesin.
            }

            try { _hostProcessTimer.Stop(); } catch { }
            try { Close(); } catch { try { Application.ExitThread(); } catch { } }
        }

        internal static AssemblyViewerSnapshot ReadSnapshot(string json)
        {
            return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<AssemblyViewerSnapshot>(json);
        }

        private void ReloadLiveSnapshotIfChanged()
        {
            if (IsDisposed || Disposing || string.IsNullOrWhiteSpace(_snapshotPath)) return;
            try
            {
                if (!File.Exists(_snapshotPath)) return;
                string json = null;
                try { json = File.ReadAllText(_snapshotPath); } catch { return; }
                if (string.IsNullOrWhiteSpace(json) || string.Equals(json, _snapshotJson, StringComparison.Ordinal)) return;

                var next = ReadSnapshot(json);
                if (next == null) return;
                _snapshotJson = json;
                ApplyLiveSnapshot(next);
            }
            catch
            {
                // Tarayıcı dosyayı atomik olarak yenilerken kısa süreli erişim hatası olabilir.
                // Bir sonraki 350 ms turunda tekrar denenir; kullanıcıya hata gösterilmez.
            }
        }

        private void ApplyLiveSnapshot(AssemblyViewerSnapshot next)
        {
            if (next == null || IsDisposed || Disposing) return;

            string selectedKey = "";
            try { selectedKey = SelectionKey(Selected); } catch { }

            _snapshot = next;
            _all.Clear();
            _all.AddRange(next.Items ?? new List<AssemblyViewerItem>());
            _treeAll.Clear();
            _treeAll.AddRange(next.TreeItems ?? new List<AssemblyViewerItem>());
            BuildTreeIndexes();
            ApplyFilter();
            UpdateLoadProgress();

            if (!string.IsNullOrWhiteSpace(selectedKey))
                RestoreSelection(selectedKey);

            if (_summary != null && next.IsLoading)
            {
                string status = string.IsNullOrWhiteSpace(next.StatusText)
                    ? "Montaj okunuyor..."
                    : next.StatusText;
                _summary.Text = (next.AssemblyTitle ?? "Montaj") + " • " + status +
                    " • " + _treeAll.Count + " satır hazır";
            }

            if (!next.IsLoading)
            {
                if (!_assemblyPreviewLoaded && _mode == ViewerListMode.Assembly && string.IsNullOrWhiteSpace(_search?.Text) &&
                    !string.IsNullOrWhiteSpace(next.AssemblyPath))
                {
                    _assemblyPreviewLoaded = true;
                    BeginInvoke(new Action(() =>
                    {
                        try { if (string.IsNullOrWhiteSpace(_search?.Text)) { _previewTimer.Stop(); _lastPreviewKey = "ASSEMBLY|" + next.AssemblyPath; _preview?.LoadPaths(next.AssemblyPath, ""); } } catch { }
                    }));
                }
            }
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(12),
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(16, 9, 16, 8) };
            var title = new Label
            {
                Text = "SrcCadWorks  •  MONTAJ GÖRÜNTÜLEME",
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 34,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _summary = new Label
            {
                Text = SummaryText(),
                ForeColor = Muted,
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            var line = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = Accent };
            header.Controls.Add(line);
            header.Controls.Add(_summary);
            header.Controls.Add(title);
            root.Controls.Add(header, 0, 0);

            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Border,
                Margin = Padding.Empty
            };
            root.Controls.Add(_contentHost, 0, 1);

            _preview = new EDrawingsAssemblyControl { Dock = DockStyle.Fill };
            _divider = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 7,
                BackColor = Border,
                Cursor = Cursors.VSplit,
                MinSize = 760,
                MinExtra = 390
            };
            _divider.SplitterMoved += (s, e) =>
            {
                ClampDividerWidth();
                if (_prefs != null && _leftPanel != null) _prefs.SplitterDistance = _leftPanel.Width;
            };

            _leftPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = Math.Max(760, _prefs.SplitterDistance),
                BackColor = Surface,
                MinimumSize = new Size(760, 0)
            };
            _leftPanel.Controls.Add(BuildLeftArea());

            // Panel + standart Splitter kullanımı: kullanıcı liste/önizleme genişliğini fareyle
            // serbestçe ayarlayabilir, ancak önceki SplitContainer açılış hatası geri gelmez.
            _contentHost.Controls.Add(_preview);
            _contentHost.Controls.Add(_divider);
            _contentHost.Controls.Add(_leftPanel);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = BackColor,
                Padding = new Padding(4, 7, 4, 3),
                Margin = Padding.Empty
            };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            var footerInfo = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = Padding.Empty, Padding = Padding.Empty
            };
            footerInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            footerInfo.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var progressLine = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, WrapContents = false,
                Margin = Padding.Empty, Padding = new Padding(4, 0, 0, 0)
            };
            _loadProgress = new Label
            {
                AutoSize = true, ForeColor = Accent, Margin = new Padding(0, 2, 12, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold), Text = "Montaj yükleniyor • %0"
            };
            _loadProgressBar = new ProgressBar { Width = 180, Height = 14, Margin = new Padding(0, 3, 0, 0), Maximum = 100 };
            progressLine.Controls.Add(_loadProgress);
            progressLine.Controls.Add(_loadProgressBar);
            footerInfo.Controls.Add(progressLine, 0, 0);
            footerInfo.Controls.Add(new Label
            {
                Text = "Tür sütunundaki +/− ile alt montajları aç/kapat. Başlıklardaki ▾ menüsü: sıralama. Sağ tık: Bileşene Git / sütun ekle.",
                Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, Padding = new Padding(4, 0, 8, 0)
            }, 0, 1);
            footer.Controls.Add(footerInfo, 0, 0);
            UpdateLoadProgress();
            var closeButton = MakeButton("KAPAT", false);
            closeButton.Dock = DockStyle.None;
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.Size = new Size(120, 36);
            closeButton.MinimumSize = new Size(120, 36);
            closeButton.Margin = new Padding(6, 5, 0, 5);
            closeButton.Click += (s, e) => Close();
            footer.Controls.Add(closeButton, 1, 0);
            root.Controls.Add(footer, 0, 2);
        }

        private void UpdateLoadProgress()
        {
            if (_loadProgress == null || _loadProgressBar == null || _snapshot == null) return;
            int percent = Math.Max(0, Math.Min(100, _snapshot.ProgressPercent));
            bool completed = !_snapshot.IsLoading && percent == 100;
            string count = _snapshot.TotalComponents > 0
                ? " • " + _snapshot.ProcessedComponents + " / " + _snapshot.TotalComponents + " bileşen"
                : " • " + _snapshot.ProcessedComponents + " bileşen";
            string state = _snapshot.IsLoading ? "Montaj yükleniyor" : completed ? "Yükleme tamamlandı" : "Yükleme durdu";
            _loadProgress.Text = state + " • %" + percent + count;
            _loadProgress.ForeColor = completed ? Color.FromArgb(38, 122, 64) : Accent;
            bool counting = _snapshot.IsLoading && _snapshot.TotalComponents == 0 && _snapshot.ProcessedComponents == 0;
            _loadProgressBar.Style = counting ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            _loadProgressBar.Value = percent;
        }

        private Control BuildLeftArea()
        {
            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Surface,
                Margin = Padding.Empty
            };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // SOLIDWORKS içindeki yüksek-DPI/tema ortamında Label ve Button tabanlı
            // araç kutularının arka planı çizilip metni kaybolabiliyordu. ToolStrip kendi
            // metin yerleşimini yaptığı için üst barı tamamen ToolStrip'e taşıyoruz.
            var tools = BuildViewerToolbar();
            left.Controls.Add(tools, 0, 0);

            _grid = new DataGridView
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
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 36,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 28 }
            };
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _grid.DefaultCellStyle.ForeColor = Color.FromArgb(31, 36, 42);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(242, 244, 247);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(31, 36, 42);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(242, 244, 247);
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(31, 36, 42);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "KindIcon",
                HeaderText = "Tür",
                Width = Math.Min(220, 62 + Math.Max(0, _treeAll.Select(x => x.Depth).DefaultIfEmpty(0).Max()) * 16),
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Frozen = true
            });
            AddColumn("FileName", "Dosya Adı", 190, true);
            AddColumn("Quantity", "Miktar", 76, true);
            AddColumn("OperationType", "İşlem Tipi", 132, true);
            AddColumn("PartCode", "Parça Kodu", 150, false);
            AddColumn("ProductDescription", "Ürün Tanımı", 210, false);
            AddColumn("Material", "Malzeme", 160, false);
            AddColumn("Thickness", "Sac Kalınlığı", 110, false);
            AddColumn("MassText", "Kütle / Ağırlık", 115, false);
            AddColumn("WeldStatus", "Kaynak Var/Yok", 110, false);
            AddColumn("WeldProcess", "Kaynak Prosesi", 120, false);
            AddColumn("ColorCode", "Renk Kodu", 130, false);
            AddColumn("Configuration", "Konfigürasyon", 120, false);
            AddColumn("DocumentType", "Belge Tipi", 100, false);
            AddColumn("SourcePath", "Dosya Yolu", 300, false);

            _grid.DataSource = _view;
            _grid.SelectionChanged += (s, e) => QueueSelectedPreview();
            _grid.CellMouseDown += GridCellMouseDown;
            _grid.CellMouseClick += GridCellMouseClick;
            _grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            _grid.CellPainting += GridCellPainting;
            BuildContextMenu();
            left.Controls.Add(_grid, 0, 1);
            return left;
        }

        private void AddColumn(string prop, string header, int width, bool defaultVisible)
        {
            bool visible = defaultVisible || (_prefs?.VisibleOptionalColumns?.Contains(prop) ?? false);
            int saved;
            if (_prefs?.Widths != null && _prefs.Widths.TryGetValue(prop, out saved))
                width = Math.Max(45, saved);

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = prop,
                DataPropertyName = prop,
                HeaderText = header + "  ▾",
                Width = width,
                Visible = visible,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Programmatic
            });
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

        private Control BuildViewerToolbar()
        {
            var bar = new ToolStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden,
                AutoSize = false,
                Height = 46,
                BackColor = SurfaceAlt,
                Padding = new Padding(6, 6, 6, 5),
                Stretch = true,
                CanOverflow = false
            };
            bar.Renderer = new SrcToolStripRenderer();

            var searchIcon = new ToolStripLabel
            {
                AutoSize = false,
                Width = 26,
                Height = 30,
                Image = CreateSearchBitmap(),
                ImageScaling = ToolStripItemImageScaling.None,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Margin = new Padding(1, 1, 0, 1),
                ToolTipText = "Ara"
            };
            bar.Items.Add(searchIcon);

            var searchItem = new ToolStripTextBox
            {
                AutoSize = false,
                Width = 310,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Margin = new Padding(0, 1, 8, 1),
                BorderStyle = BorderStyle.FixedSingle,
                ToolTipText = "Parça, montaj, işlem tipi veya özelliklerde ara"
            };
            _search = searchItem.TextBox;
            _search.TextChanged += (s, e) => { _searchTimer.Stop(); _previewTimer.Stop(); _searchTimer.Start(); };
            bar.Items.Add(searchItem);

            var viewBar = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, BackColor = SurfaceAlt, Padding = new Padding(6,4,6,4), CanOverflow = false, AutoSize = false };
            viewBar.Renderer = new SrcToolStripRenderer();
            _partsButton = MakeToolbarButton("PARÇA GÖRÜNÜMÜ", false, 160);
            _partsButton.Image = _partIcon; _partsButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _partsButton.Click += (s, e) => SetViewMode(ViewerListMode.Parts, true);
            viewBar.Items.Add(_partsButton);

            _assemblyButton = MakeToolbarButton("MONTAJ GÖRÜNÜMÜ", true, 160);
            _assemblyButton.Image = _assemblyIcon; _assemblyButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _assemblyButton.Click += (s, e) => SetViewMode(ViewerListMode.Assembly, true);
            viewBar.Items.Add(_assemblyButton);
            _allAssembliesButton = MakeToolbarButton("TÜM MONTAJLAR", false, 160);
            _allAssembliesButton.Image = _allAssembliesIcon; _allAssembliesButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _allAssembliesButton.Click += (s,e) => SetViewMode(ViewerListMode.AllAssemblies,true);
            viewBar.Items.Add(_allAssembliesButton);

            string rootName = _snapshot?.AssemblyTitle ?? "Montaj";
            var rootLabel = new ToolStripLabel
            {
                Text = rootName, ToolTipText = "Ana montaj: " + rootName,
                AutoSize = false, Width = 160, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Accent, Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Margin = new Padding(10, 1, 4, 1)
            };
            viewBar.Items.Add(rootLabel);
            viewBar.Items.Add(new ToolStripLabel { Text = "Montaj Adedi:", AutoSize = false, Width = 86, Margin = new Padding(0, 1, 2, 1) });
            _assemblyQuantity = new NumericUpDown
            {
                Minimum = 1, Maximum = 10000, Value = 1, Width = 58,
                DecimalPlaces = 0, TextAlign = HorizontalAlignment.Center
            };
            _assemblyQuantity.ValueChanged += (s, e) => { if (_grid != null) ApplyFilter(); };
            var quantityHost = new ToolStripControlHost(_assemblyQuantity)
            {
                AutoSize = false, Width = 58, Height = 26, Margin = new Padding(0, 2, 2, 2)
            };
            viewBar.Items.Add(quantityHost);
            viewBar.SizeChanged += (s, e) =>
            {
                rootLabel.Width = Math.Max(50, viewBar.ClientSize.Width - 480 - 86 - 58 - 52);
            };


            var excel = MakeToolbarButton("EXCEL AKTAR", false, 118);
            excel.Click += (s, e) => ExportCurrentViewToExcel();
            bar.Items.Add(excel);

            // Arama alanı kalan genişliği kullanır; butonların metin alanı asla ezilmez.
            bar.SizeChanged += (s, e) =>
            {
                try
                {
                    int fixedWidth = 26 + 118 + 54;
                    searchItem.Width = Math.Max(220, bar.ClientSize.Width - fixedWidth);
                }
                catch { }
            };

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            tools.RowStyles.Add(new RowStyle(SizeType.Percent,50)); tools.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            tools.Controls.Add(bar,0,0); tools.Controls.Add(viewBar,0,1);
            return tools;
        }

        private void SafeApplyFilterFromSearch()
        {
            if (IsDisposed || Disposing) return;
            try { ApplyFilter(); }
            catch (ArgumentOutOfRangeException)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!IsDisposed && !Disposing) ApplyFilter();
                    }));
                }
                catch { }
            }
        }

        private static Bitmap CreateSearchBitmap()
        {
            var bmp = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(65, 74, 84), 2F))
                {
                    g.DrawEllipse(pen, 3, 3, 9, 9);
                    g.DrawLine(pen, 11, 11, 17, 17);
                }
            }
            return bmp;
        }

        private ToolStripButton MakeToolbarButton(string text, bool accent, int width)
        {
            var button = new ToolStripButton
            {
                Text = text,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = false,
                Width = width,
                Height = 31,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = accent ? Color.White : Color.FromArgb(31, 36, 42),
                BackColor = accent ? Accent : Surface,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(3, 1, 3, 1),
                Overflow = ToolStripItemOverflow.Never,
                AutoToolTip = false,
                CheckOnClick = false,
                Checked = accent,
                ToolTipText = text
            };
            return button;
        }

        private Button MakeButton(string text, bool accent)
        {
            // Standart WinForms Button kullanıyoruz. Önceki owner-draw düğmeler bazı
            // DPI/tema kombinasyonlarında arka planı çizip metni boş bırakabiliyordu.
            var b = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                BackColor = accent ? Accent : Surface,
                ForeColor = accent ? Color.White : Color.FromArgb(31, 36, 42),
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(3),
                Cursor = Cursors.Hand,
                MinimumSize = new Size(92, 34),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = accent ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(182, 24, 32) : Color.FromArgb(243, 245, 248);
            b.FlatAppearance.MouseDownBackColor = accent ? Color.FromArgb(160, 21, 28) : Color.FromArgb(232, 236, 241);
            return b;
        }

        private void SetViewMode(ViewerListMode mode, bool loadPreview)
        {
            _mode = mode;
            _hidden.Clear();
            _sortProperty = "";
            _sortAscending = true;
            ResetSortGlyphs();
            UpdateViewButtons();
            ApplyFilter();

            if (loadPreview)
            {
                if (mode == ViewerListMode.Assembly && string.IsNullOrWhiteSpace(_search?.Text) && !string.IsNullOrWhiteSpace(_snapshot?.AssemblyPath))
                {
                    _previewTimer.Stop(); _lastPreviewKey = "ASSEMBLY|" + _snapshot.AssemblyPath;
                    _preview.LoadPaths(_snapshot.AssemblyPath, "");
                }
                else if (_grid.Rows.Count > 0)
                {
                    try { _grid.CurrentCell = _grid.Rows[0].Cells[Math.Min(1, _grid.Columns.Count - 1)]; } catch { }
                    LoadSelectedPreview();
                }
            }
        }

        private void UpdateViewButtons()
        {
            SetButtonActive(_partsButton, _mode == ViewerListMode.Parts);
            SetButtonActive(_assemblyButton, _mode == ViewerListMode.Assembly);
            SetButtonActive(_allAssembliesButton, _mode == ViewerListMode.AllAssemblies);
        }

        private static void SetButtonActive(ToolStripButton b, bool active)
        {
            if (b == null) return;
            b.BackColor = active ? Accent : Surface;
            b.ForeColor = active ? Color.White : Color.FromArgb(31, 36, 42);
            b.Checked = active;
            b.Invalidate();
        }

        private void BuildContextMenu()
        {
            _rowMenu = new ContextMenuStrip();
            _rowMenu.Items.Add("Bileşene Git (SOLIDWORKS)", null, (s, e) => GoToComponent());
            _rowMenu.Items.Add("SOLIDWORKS'te Aç", null, (s, e) => OpenInSolidWorks(false));
            _rowMenu.Items.Add("Teknik Resmi Aç", null, (s, e) => OpenInSolidWorks(true));
            _rowMenu.Items.Add(new ToolStripSeparator());
            _rowMenu.Items.Add("Yalnız Bu Satırı Göster", null, (s, e) => IsolateSelected());
            _rowMenu.Items.Add("Satırı Gizle", null, (s, e) => HideSelected());
            _rowMenu.Items.Add("Gizlenen Satırları Göster", null, (s, e) => { _hidden.Clear(); ApplyFilter(); });
            _rowMenu.Items.Add("Parça Bilgileri", null, (s, e) => ShowInfo());
            _rowMenu.Items.Add(new ToolStripSeparator());
            _rowMenu.Items.Add(BuildColumnMenu(-1));
        }

        private ToolStripMenuItem BuildColumnMenu(int anchorDisplayIndex)
        {
            var cols = new ToolStripMenuItem("Yeni Sütun Ekle / Kaldır");
            foreach (DataGridViewColumn c in _grid.Columns)
            {
                if (!IsOptionalColumn(c)) continue;
                var item = new ToolStripMenuItem(CleanHeader(c.HeaderText))
                {
                    CheckOnClick = true,
                    Checked = c.Visible,
                    Tag = c.Name
                };
                item.CheckedChanged += (s, e) =>
                {
                    var mi = (ToolStripMenuItem)s;
                    var column = _grid.Columns[(string)mi.Tag];
                    column.Visible = mi.Checked;
                    if (mi.Checked && anchorDisplayIndex >= 0)
                    {
                        try
                        {
                            int visibleCount = _grid.Columns.Cast<DataGridViewColumn>().Count(x => x.Visible);
                            column.DisplayIndex = Math.Max(0, Math.Min(anchorDisplayIndex + 1, visibleCount - 1));
                        }
                        catch { }
                    }
                };
                cols.DropDownItems.Add(item);
            }
            return cols;
        }

        private static bool IsOptionalColumn(DataGridViewColumn c)
        {
            if (c == null) return false;
            return c.Name != "KindIcon" && c.Name != "FileName" && c.Name != "Quantity" && c.Name != "OperationType";
        }

        private void GridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex == -1) return;
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            if (e.ColumnIndex >= 0) _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            _columnInsertAnchor = e.ColumnIndex >= 0 ? _grid.Columns[e.ColumnIndex].DisplayIndex : -1;

            // Sütun alt menüsünü seçili hücrenin hemen sağına ekleme mantığıyla yenile.
            if (_rowMenu.Items.Count > 0)
            {
                var last = _rowMenu.Items[_rowMenu.Items.Count - 1];
                _rowMenu.Items.RemoveAt(_rowMenu.Items.Count - 1);
                try { last.Dispose(); } catch { }
                _rowMenu.Items.Add(BuildColumnMenu(_columnInsertAnchor));
            }
            _rowMenu.Show(_grid, _grid.PointToClient(Cursor.Position));
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            ShowHeaderMenu(e.ColumnIndex);
        }

        private void ShowHeaderMenu(int columnIndex)
        {
            if (_grid == null || columnIndex < 0 || columnIndex >= _grid.Columns.Count) return;
            var column = _grid.Columns[columnIndex];
            var menu = new ContextMenuStrip();

            string property = column.DataPropertyName;
            if (!string.IsNullOrWhiteSpace(property))
            {
                bool numeric = IsNumericSortProperty(property);
                menu.Items.Add(numeric ? "Küçükten Büyüğe" : "A'dan Z'ye", null, (s, e) => ApplySort(property, true));
                menu.Items.Add(numeric ? "Büyükten Küçüğe" : "Z'den A'ya", null, (s, e) => ApplySort(property, false));
                menu.Items.Add("Sıralamayı Temizle", null, (s, e) => ResetSort());
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add(BuildColumnMenu(column.DisplayIndex));
            var rect = _grid.GetCellDisplayRectangle(columnIndex, -1, true);
            menu.Show(_grid, new Point(rect.Left, Math.Max(0, rect.Bottom)));
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (_refreshingGrid || _grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (e.ColumnIndex >= _grid.Columns.Count || _grid.Columns[e.ColumnIndex].Name != "KindIcon") return;
            if (e.RowIndex >= _grid.Rows.Count) return;

            try
            {
                var item = _grid.Rows[e.RowIndex].DataBoundItem as AssemblyViewerItem;
                if (item == null) return;

                e.PaintBackground(e.CellBounds, true);
                int depth = _mode == ViewerListMode.Assembly ? Math.Max(0, item.Depth) : 0;
                int x = e.CellBounds.Left + 6 + Math.Min(depth, 12) * 16;
                int cy = e.CellBounds.Top + (e.CellBounds.Height / 2);

                if (_mode == ViewerListMode.Assembly && item.HasChildren)
                {
                    var box = new Rectangle(x, cy - 6, 13, 13);
                    using (var fill = new SolidBrush(Color.White)) e.Graphics.FillRectangle(fill, box);
                    using (var pen = new Pen(Color.FromArgb(102, 111, 122), 1F))
                    {
                        e.Graphics.DrawRectangle(pen, box);
                        e.Graphics.DrawLine(pen, box.Left + 3, cy, box.Right - 3, cy);
                        if (!_expandedNodes.Contains(item.NodeId ?? ""))
                            e.Graphics.DrawLine(pen, box.Left + 6, box.Top + 3, box.Left + 6, box.Bottom - 3);
                    }
                    x += 18;
                }

                Image icon = string.Equals(item.DocumentType, "MONTAJ", StringComparison.OrdinalIgnoreCase)
                    ? (_mode == ViewerListMode.AllAssemblies ? _allAssembliesIcon : _assemblyIcon) : _partIcon;
                if (icon != null)
                {
                    var iconRect = new Rectangle(x, cy - 10, 20, 20);
                    e.Graphics.DrawImage(icon, iconRect);
                }
                e.Handled = true;
            }
            catch { }
        }

        private void GridCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _mode != ViewerListMode.Assembly || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "KindIcon") return;
            var item = _grid.Rows[e.RowIndex].DataBoundItem as AssemblyViewerItem;
            if (item == null || !item.HasChildren || string.IsNullOrWhiteSpace(item.NodeId)) return;

            if (_expandedNodes.Contains(item.NodeId)) _expandedNodes.Remove(item.NodeId);
            else _expandedNodes.Add(item.NodeId);
            ApplyFilter();
        }

        private AssemblyViewerItem Selected
        {
            get
            {
                if (_grid == null || _refreshingGrid) return null;
                try { return _grid.CurrentRow?.DataBoundItem as AssemblyViewerItem; }
                catch { return null; }
            }
        }

        private void QueueSelectedPreview()
        {
            if (_refreshingGrid || IsDisposed || Disposing) return;
            _previewTimer.Stop(); _previewTimer.Start();
        }

        private void LoadSelectedPreview()
        {
            if (_refreshingGrid) return;
            if (_loadingPreview) { QueueSelectedPreview(); return; }
            var item = Selected;
            if (_preview == null) return;
            string sourcePath = item?.SourcePath ?? "";
            string drawingPath = item?.DrawingPath ?? "";
            if (item == null && string.IsNullOrWhiteSpace(_search?.Text)) sourcePath = _snapshot?.AssemblyPath ?? "";
            string key = sourcePath + "|" + drawingPath + "|" + PreviewModePreference.PreferDrawing;
            if (key == _lastPreviewKey) return;
            _loadingPreview = true;
            try { _preview.LoadPaths(sourcePath, drawingPath); _lastPreviewKey = key; }
            finally { _loadingPreview = false; }
        }

        private void ApplyFilter()
        {
            if (_assemblyQuantity != null)
            {
                int largest = _all.Concat(_treeAll).Select(row => Math.Max(1, row.Quantity)).DefaultIfEmpty(1).Max();
                _assemblyQuantity.Maximum = Math.Max(1, Math.Min(10000, int.MaxValue / largest));
            }
            string q = (_search?.Text ?? "").Trim();
            List<AssemblyViewerItem> filtered;

            if (_mode != ViewerListMode.Assembly)
            {
                filtered = _all.Where(x => _mode == ViewerListMode.Parts ? IsPartDocument(x) : IsAssemblyDocument(x))
                    .Where(x => !_hidden.Contains(VisibilityKey(x)) && (string.IsNullOrWhiteSpace(q) || Contains(x, q)))
                    .ToList();
                SortFlatList(filtered);
            }
            else
            {
                filtered = BuildAssemblyTreeRows(q);
            }

            string selectedKey = "";
            try { selectedKey = SelectionKey(Selected); } catch { }

            _refreshingGrid = true;
            try
            {
                try { _grid?.SuspendLayout(); } catch { }
                _view.RaiseListChangedEvents = false;
                _view.Clear();
                int assemblyCount = (int)(_assemblyQuantity?.Value ?? 1);
                foreach (var x in filtered) _view.Add(CloneForQuantity(x, checked(x.Quantity * assemblyCount)));
                _view.RaiseListChangedEvents = true;
                _view.ResetBindings();
            }
            finally
            {
                _view.RaiseListChangedEvents = true;
                try { _grid?.ResumeLayout(false); } catch { }
                _refreshingGrid = false;
            }

            if (filtered.Count > 0)
            {
                RestoreSelection(string.IsNullOrEmpty(selectedKey) ? _savedSelection : selectedKey);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var preferred = filtered.FirstOrDefault(x => Contains(x,q) && string.Equals(x.PartCode,q,StringComparison.OrdinalIgnoreCase)) ?? filtered.FirstOrDefault(x => Contains(x,q));
                    if (preferred != null) RestoreSelection(SelectionKey(preferred));
                }
                if (_grid.CurrentRow == null) _grid.CurrentCell = _grid.Rows[0].Cells[Math.Min(1,_grid.Columns.Count-1)];
            }
            else
            {
                try
                {
                    _grid?.ClearSelection();
                    if (_grid != null) _grid.CurrentCell = null;
                }
                catch { }
            }
            if (_summary != null)
            {
                long qty = filtered.Sum(x => (long)Math.Max(0, x.Quantity)) * (long)(_assemblyQuantity?.Value ?? 1);
                string modeText = _mode == ViewerListMode.Parts ? "Parça görünümü" : _mode == ViewerListMode.AllAssemblies ? "Tüm montajlar" : "Montaj görünümü";
                if (_snapshot != null && _snapshot.IsLoading)
                {
                    string status = string.IsNullOrWhiteSpace(_snapshot.StatusText) ? "Montaj okunuyor..." : _snapshot.StatusText;
                    _summary.Text = (_snapshot.AssemblyTitle ?? "Montaj") + " • " + status +
                        " • " + filtered.Count + " satır hazır";
                }
                else
                {
                    string status = !string.IsNullOrWhiteSpace(_snapshot?.StatusText) && _snapshot.StatusText != "Hazır" ? " • " + _snapshot.StatusText : "";
                    _summary.Text = (_snapshot?.AssemblyTitle ?? "Montaj") + " • " + modeText + " • " + filtered.Count + " satır • " + qty + " adet" + status;
                }
            }
            try { _grid?.InvalidateColumn(_grid.Columns["KindIcon"].Index); } catch { }
            QueueSelectedPreview();
        }

        private void BuildTreeIndexes()
        {
            _searchIndex.Clear();
            foreach (var item in _all.Concat(_treeAll)) if (item != null) _searchIndex[item] = string.Join(" | ", item.FileName,item.PartCode,item.DocumentType,item.OperationType,item.ProductDescription,item.Material,item.Thickness,item.MassText,item.ColorCode,item.WeldStatus,item.WeldProcess,item.Configuration,item.SourcePath);
            _treeById.Clear();
            _treeChildren.Clear();

            foreach (var item in _treeAll)
            {
                if (item == null) continue;

                if (!string.IsNullOrWhiteSpace(item.NodeId) && !_treeById.ContainsKey(item.NodeId))
                    _treeById[item.NodeId] = item;

                string parent = item.ParentNodeId ?? "";
                List<AssemblyViewerItem> list;
                if (!_treeChildren.TryGetValue(parent, out list))
                {
                    list = new List<AssemblyViewerItem>();
                    _treeChildren[parent] = list;
                }
                list.Add(item);
            }

            foreach (var list in _treeChildren.Values)
            {
                list.Sort((a, b) =>
                {
                    int cmp = (a?.HierarchyOrder ?? 0).CompareTo(b?.HierarchyOrder ?? 0);
                    if (cmp != 0) return cmp;
                    return StringComparer.CurrentCultureIgnoreCase.Compare(a?.FileName ?? "", b?.FileName ?? "");
                });
            }
        }

        private List<AssemblyViewerItem> BuildAssemblyTreeRows(string query)
        {
            var result = new List<AssemblyViewerItem>();
            if (_treeAll.Count == 0) return result;

            HashSet<string> required = null;
            if (!string.IsNullOrWhiteSpace(query))
            {
                required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var match in _treeAll.Where(x => Contains(x, query)))
                {
                    var current = match;
                    while (current != null && !string.IsNullOrWhiteSpace(current.NodeId) && required.Add(current.NodeId))
                    {
                        if (string.IsNullOrWhiteSpace(current.ParentNodeId)) break;
                        AssemblyViewerItem parent;
                        if (!_treeById.TryGetValue(current.ParentNodeId, out parent)) break;
                        current = parent;
                    }
                }
            }

            AppendTreeChildren("", _treeChildren, required, result);
            return result;
        }

        private void AppendTreeChildren(string parentNodeId,
            IDictionary<string, List<AssemblyViewerItem>> children,
            HashSet<string> required,
            IList<AssemblyViewerItem> output)
        {
            List<AssemblyViewerItem> siblings;
            if (!children.TryGetValue(parentNodeId ?? "", out siblings)) return;

            IEnumerable<AssemblyViewerItem> ordered = siblings;
            if (!string.IsNullOrWhiteSpace(_sortProperty))
            {
                ordered = _sortAscending
                    ? siblings.OrderBy(x => GetSortValue(x, _sortProperty), new ViewerSortComparer(_sortProperty))
                    : siblings.OrderByDescending(x => GetSortValue(x, _sortProperty), new ViewerSortComparer(_sortProperty));
            }
            else
            {
                ordered = siblings;
            }

            foreach (var item in ordered)
            {
                if (required != null && !required.Contains(item.NodeId ?? "")) continue;
                if (_hidden.Contains(VisibilityKey(item))) continue;
                output.Add(item);

                bool showChildren = required != null || _expandedNodes.Contains(item.NodeId ?? "");
                if (item.HasChildren && showChildren)
                    AppendTreeChildren(item.NodeId, children, required, output);
            }
        }

        private void SortFlatList(List<AssemblyViewerItem> list)
        {
            if (list == null || string.IsNullOrWhiteSpace(_sortProperty)) return;
            list.Sort((a, b) =>
            {
                int cmp = CompareValues(GetSortValue(a, _sortProperty), GetSortValue(b, _sortProperty), _sortProperty);
                if (cmp == 0) cmp = StringComparer.CurrentCultureIgnoreCase.Compare(a?.FileName ?? "", b?.FileName ?? "");
                return _sortAscending ? cmp : -cmp;
            });
        }

        private static AssemblyViewerItem CloneForQuantity(AssemblyViewerItem x, int quantity)
        {
            if (x == null) return null;
            return new AssemblyViewerItem
            {
                FileName = x.FileName,
                PartCode = x.PartCode,
                Quantity = quantity,
                TopLevelQuantity = x.TopLevelQuantity,
                DocumentType = x.DocumentType,
                OperationType = x.OperationType,
                ProductDescription = x.ProductDescription,
                Material = x.Material,
                Thickness = x.Thickness,
                ColorCode = x.ColorCode,
                WeldProcess = x.WeldProcess,
                WeldStatus = x.WeldStatus,
                MassText = x.MassText,
                Configuration = x.Configuration,
                SourcePath = x.SourcePath,
                DrawingPath = x.DrawingPath,
                SelectId = x.SelectId,
                OccurrenceSelectIds = x.OccurrenceSelectIds == null ? new List<string>() : new List<string>(x.OccurrenceSelectIds),
                TopLevelOccurrenceSelectIds = x.TopLevelOccurrenceSelectIds == null ? new List<string>() : new List<string>(x.TopLevelOccurrenceSelectIds),
                NodeId = x.NodeId,
                ParentNodeId = x.ParentNodeId,
                Depth = x.Depth,
                HasChildren = x.HasChildren,
                HierarchyOrder = x.HierarchyOrder
            };
        }

        private static bool IsPartDocument(AssemblyViewerItem x)
        {
            if (x == null) return false;
            if (string.Equals(x.DocumentType, "PARÇA", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(Path.GetExtension(x.SourcePath ?? ""), ".SLDPRT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAssemblyDocument(AssemblyViewerItem x) => x != null && (string.Equals(x.DocumentType,"MONTAJ",StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(x.SourcePath ?? ""),".SLDASM",StringComparison.OrdinalIgnoreCase));

        private bool Contains(AssemblyViewerItem x, string q)
        {
            string s; if (!_searchIndex.TryGetValue(x, out s)) s = (x.FileName ?? "") + " | " + (x.PartCode ?? "");
            return s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Key(AssemblyViewerItem x)
        {
            if (x == null) return "";
            return (x.SourcePath ?? "") + "|" + (x.Configuration ?? "");
        }

        private string VisibilityKey(AssemblyViewerItem x)
        {
            if (_mode == ViewerListMode.Assembly && x != null && !string.IsNullOrWhiteSpace(x.NodeId)) return "NODE:" + x.NodeId;
            return Key(x);
        }

        private string SelectionKey(AssemblyViewerItem x)
        {
            if (_mode == ViewerListMode.Assembly && x != null && !string.IsNullOrWhiteSpace(x.NodeId)) return "NODE:" + x.NodeId;
            return Key(x);
        }

        private void HideSelected()
        {
            var x = Selected;
            if (x == null) return;
            _hidden.Add(VisibilityKey(x));
            ApplyFilter();
        }

        private void IsolateSelected()
        {
            var x = Selected;
            if (x == null) return;
            string keep = VisibilityKey(x);
            _hidden.Clear();
            IEnumerable<AssemblyViewerItem> source = _mode == ViewerListMode.Assembly ? _treeAll : _all.Where(candidate => _mode == ViewerListMode.Parts ? IsPartDocument(candidate) : IsAssemblyDocument(candidate));
            foreach (var a in source)
                if (!string.Equals(VisibilityKey(a), keep, StringComparison.OrdinalIgnoreCase)) _hidden.Add(VisibilityKey(a));
            ApplyFilter();
        }

        private void ShowInfo()
        {
            var x = Selected;
            if (x == null) return;
            MessageBox.Show(
                "Tür: " + x.DocumentType + "\r\nDosya: " + x.FileName + "\r\nParça Kodu: " + x.PartCode + "\r\nMiktar: " + x.Quantity +
                "\r\nİşlem Tipi: " + x.OperationType + "\r\nÜrün Tanımı: " + x.ProductDescription +
                "\r\nMalzeme: " + x.Material + "\r\nSac Kalınlığı: " + x.Thickness + "\r\nKütle / Ağırlık: " + x.MassText +
                "\r\nKaynak Var/Yok: " + x.WeldStatus + "\r\nKaynak Prosesi: " + x.WeldProcess +
                "\r\nRenk: " + x.ColorCode + "\r\nKonfigürasyon: " + x.Configuration + "\r\n\r\n" + x.SourcePath,
                "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void GoToComponent()
        {
            var x = Selected;
            if (x == null) return;
            try
            {
                var sw = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                ModelDoc2 model = null;
                try { model = sw.GetOpenDocumentByName(_snapshot.AssemblyPath) as ModelDoc2; } catch { }
                if (model == null)
                {
                    try
                    {
                        int er = 0, wr = 0;
                        model = sw.OpenDoc6(_snapshot.AssemblyPath, (int)swDocumentTypes_e.swDocASSEMBLY, 0,
                            _snapshot.AssemblyConfiguration ?? "", ref er, ref wr) as ModelDoc2;
                    }
                    catch { }
                }
                if (model == null)
                {
                    MessageBox.Show("SOLIDWORKS ana montajı bulunamadı.", "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try { int err = 0; sw.ActivateDoc3(model.GetTitle(), false, 0, ref err); } catch { }
                try { model.ClearSelection2(true); } catch { }

                var ids = new List<string>();
                if (_mode == ViewerListMode.Assembly && x.Depth == 0 && x.TopLevelOccurrenceSelectIds != null)
                    ids.AddRange(x.TopLevelOccurrenceSelectIds.Where(id => !string.IsNullOrWhiteSpace(id)));
                if (x.OccurrenceSelectIds != null)
                    ids.AddRange(x.OccurrenceSelectIds.Where(id => !string.IsNullOrWhiteSpace(id)));
                if (!string.IsNullOrWhiteSpace(x.SelectId)) ids.Add(x.SelectId);
                ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                bool ok = false;
                foreach (string id in ids)
                {
                    try { ok = model.Extension.SelectByID2(id, "COMPONENT", 0, 0, 0, false, 0, null, 0); } catch { ok = false; }
                    if (!ok)
                    {
                        try { ok = model.Extension.SelectByID2(id, "", 0, 0, 0, false, 0, null, 0); } catch { ok = false; }
                    }
                    if (ok) break;
                }

                if (ok)
                {
                    try { dynamic d = model; d.ViewZoomToSelection(); } catch { }
                    try { model.GraphicsRedraw2(); } catch { }
                    try { sw.Visible = true; } catch { }
                    BringSolidWorksToFront();
                }
                else
                {
                    MessageBox.Show("Bileşen ana montaj ağacında seçilemedi. Montaj yenilendiyse Montaj Görüntüleme'yi kapatıp tekrar açın.",
                        "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("SOLIDWORKS bağlantısı kurulamadı: " + ex.Message, "SrcCadWorks");
            }
        }

        private static void BringSolidWorksToFront()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("SLDWORKS"))
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void OpenInSolidWorks(bool drawing)
        {
            var x = Selected;
            if (x == null) return;
            string path = drawing ? x.DrawingPath : x.SourcePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(drawing ? "Teknik resim bulunamadı." : "Dosya bulunamadı.", "SrcCadWorks");
                return;
            }
            try
            {
                var sw = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                int type = path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)
                    ? (int)swDocumentTypes_e.swDocDRAWING
                    : path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)
                        ? (int)swDocumentTypes_e.swDocASSEMBLY
                        : (int)swDocumentTypes_e.swDocPART;
                int er = 0, wr = 0;
                var doc = sw.GetOpenDocumentByName(path) as ModelDoc2;
                if (doc == null) doc = sw.OpenDoc6(path, type, 0, drawing ? "" : (x.Configuration ?? ""), ref er, ref wr) as ModelDoc2;
                if (doc == null) { MessageBox.Show("Dosya açılamadı. Hata=" + er, "SrcCadWorks"); return; }
                try { int a = 0; sw.ActivateDoc3(doc.GetTitle(), false, 0, ref a); } catch { }
                try { sw.Visible = true; } catch { }
                BringSolidWorksToFront();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void ApplySort(string property, bool ascending)
        {
            _sortProperty = property ?? "";
            _sortAscending = ascending;
            UpdateSortGlyphs();
            ApplyFilter();
        }

        private void ResetSort()
        {
            _sortProperty = "";
            _sortAscending = true;
            ResetSortGlyphs();
            ApplyFilter();
        }

        private void UpdateSortGlyphs()
        {
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
        }

        private static bool IsNumericSortProperty(string property)
        {
            return string.Equals(property, "Quantity", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(property, "MassText", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(property, "Thickness", StringComparison.OrdinalIgnoreCase);
        }

        private static object GetSortValue(AssemblyViewerItem x, string property)
        {
            if (x == null) return null;
            switch (property ?? "")
            {
                case "FileName": return x.FileName;
                case "Quantity": return x.Quantity;
                case "OperationType": return x.OperationType;
                case "PartCode": return x.PartCode;
                case "ProductDescription": return x.ProductDescription;
                case "Material": return x.Material;
                case "Thickness": return ParseNumber(x.Thickness);
                case "MassText": return ParseNumber(x.MassText);
                case "WeldStatus": return x.WeldStatus;
                case "WeldProcess": return x.WeldProcess;
                case "ColorCode": return x.ColorCode;
                case "Configuration": return x.Configuration;
                case "DocumentType": return x.DocumentType;
                case "SourcePath": return x.SourcePath;
                default: return "";
            }
        }

        private static double ParseNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            string cleaned = new string(value.Where(c => char.IsDigit(c) || c == ',' || c == '.' || c == '-').ToArray());
            double d;
            if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.GetCultureInfo("tr-TR"), out d)) return d;
            if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }

        private static int CompareValues(object left, object right, string property)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            if (left is int && right is int) return ((int)left).CompareTo((int)right);
            if (left is double && right is double) return ((double)left).CompareTo((double)right);
            return StringComparer.CurrentCultureIgnoreCase.Compare(Convert.ToString(left), Convert.ToString(right));
        }

        private void RestoreSelection(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || _grid == null) return;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var item = row.DataBoundItem as AssemblyViewerItem;
                if (item == null || !string.Equals(SelectionKey(item), key, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    _grid.ClearSelection();
                    row.Selected = true;
                    if (row.Cells.Count > 1) _grid.CurrentCell = row.Cells[1];
                }
                catch { }
                return;
            }
        }

        private void ExportCurrentViewToExcel()
        {
            if (_exporting) return;
            if (_view.Count == 0)
            {
                MessageBox.Show("Excel'e aktarılacak satır yok.", "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Excel Çalışma Kitabı (*.xlsx)|*.xlsx";
                dialog.FileName = SafeFileName((_snapshot?.AssemblyTitle ?? "Montaj") + "_" + (_mode == ViewerListMode.Parts ? "Parca_Listesi" : _mode == ViewerListMode.AllAssemblies ? "Tum_Montajlar" : "Montaj_Listesi") + ".xlsx");
                dialog.OverwritePrompt = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                Type excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null)
                {
                    MessageBox.Show("Microsoft Excel masaüstü uygulaması bulunamadı.", "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _exporting = true;
                dynamic excel = null, book = null, sheet = null, range = null;
                try
                {
                    excel = Activator.CreateInstance(excelType);
                    excel.Visible = false;
                    excel.DisplayAlerts = false;
                    book = excel.Workbooks.Add();
                    sheet = book.Worksheets[1];
                    sheet.Name = _mode == ViewerListMode.Parts ? "Parçalar" : _mode == ViewerListMode.AllAssemblies ? "Tüm Montajlar" : "Montaj";

                    var columns = _grid.Columns.Cast<DataGridViewColumn>()
                        .Where(c => c.Visible)
                        .OrderBy(c => c.DisplayIndex)
                        .ToList();

                    object[,] data = new object[_view.Count + 1, columns.Count];
                    for (int c = 0; c < columns.Count; c++) data[0, c] = CleanHeader(columns[c].HeaderText);
                    for (int r = 0; r < _view.Count; r++)
                    {
                        for (int c = 0; c < columns.Count; c++)
                            data[r + 1, c] = GetExportValue(_view[r], columns[c]);
                    }

                    range = sheet.Range[sheet.Cells[1, 1], sheet.Cells[_view.Count + 1, columns.Count]];
                    range.Value2 = data;
                    dynamic header = sheet.Range[sheet.Cells[1, 1], sheet.Cells[1, columns.Count]];
                    header.Font.Bold = true;
                    header.Interior.Color = ColorTranslator.ToOle(Color.FromArgb(232, 235, 239));
                    header.AutoFilter();
                    ReleaseCom(header);
                    range.Columns.AutoFit();
                    sheet.Rows[1].RowHeight = 24;
                    book.SaveAs(dialog.FileName, 51);
                    book.Close(true);
                    book = null;
                    excel.Quit();
                    excel = null;

                    if (MessageBox.Show("Excel oluşturuldu.\r\n\r\n" + dialog.FileName + "\r\n\r\nDosyayı açmak ister misiniz?",
                        "SrcCadWorks", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        try { Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true }); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Excel oluşturulamadı: " + ex.Message, "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _exporting = false;
                    ReleaseCom(range);
                    ReleaseCom(sheet);
                    if (book != null) { try { book.Close(false); } catch { } }
                    ReleaseCom(book);
                    if (excel != null) { try { excel.Quit(); } catch { } }
                    ReleaseCom(excel);
                }
            }
        }

        private static object GetExportValue(AssemblyViewerItem x, DataGridViewColumn c)
        {
            if (x == null || c == null) return "";
            if (c.Name == "KindIcon") return x.DocumentType ?? "";
            switch (c.DataPropertyName ?? "")
            {
                case "FileName": return x.FileName ?? "";
                case "Quantity": return x.Quantity;
                case "OperationType": return x.OperationType ?? "";
                case "PartCode": return x.PartCode ?? "";
                case "ProductDescription": return x.ProductDescription ?? "";
                case "Material": return x.Material ?? "";
                case "Thickness": return x.Thickness ?? "";
                case "MassText": return x.MassText ?? "";
                case "WeldStatus": return x.WeldStatus ?? "";
                case "WeldProcess": return x.WeldProcess ?? "";
                case "ColorCode": return x.ColorCode ?? "";
                case "Configuration": return x.Configuration ?? "";
                case "DocumentType": return x.DocumentType ?? "";
                case "SourcePath": return x.SourcePath ?? "";
                default: return "";
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private static string CleanHeader(string header)
        {
            return (header ?? "").Replace("  ▾", "").Trim();
        }

        private static void ReleaseCom(object o)
        {
            if (o == null) return;
            try { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        private string SummaryText()
        {
            int occ = _all.Sum(x => Math.Max(0, x.Quantity));
            return (_snapshot?.AssemblyTitle ?? "Montaj") + " • " + _all.Count + " benzersiz dosya • " + occ + " occurrence";
        }

        private void ApplyStoredDividerWidth()
        {
            if (_leftPanel == null || _contentHost == null) return;
            int width = _prefs != null ? _prefs.SplitterDistance : 820;
            _leftPanel.Width = Math.Max(760, width);
            ClampDividerWidth();
        }

        private void ClampDividerWidth()
        {
            if (_leftPanel == null || _contentHost == null || _divider == null) return;
            try
            {
                int total = _contentHost.ClientSize.Width;
                if (total <= 0) return;
                int minLeft = 760;
                int minRight = 390;
                int available = Math.Max(0, total - _divider.Width);
                if (available < minLeft + minRight)
                {
                    // Form.MinimumSize normal kullanımda bu dala düşürmez. Yine de
                    // işletim sistemi ölçeklemesinde sağ önizlemeyi tamamen kaybetme.
                    minLeft = Math.Max(650, available - minRight);
                }
                int maxLeft = Math.Max(minLeft, available - minRight);
                if (_leftPanel.Width < minLeft) _leftPanel.Width = minLeft;
                if (_leftPanel.Width > maxLeft) _leftPanel.Width = maxLeft;
            }
            catch { }
        }

        private void PrepareForClose()
        {
            if (_closingPrepared) return;
            _closingPrepared = true;
            try { SavePreferences(); } catch { }
            // eDrawings ActiveX kapanışını tek yerde yapıyoruz.
            // Kontrol kendi Dispose akışında bir kez kapanır; bu, kapatırken görülen
            // native AccessViolation / 0xc0000005 hatasını önler.
        }

        private Dictionary<string, string> CaptureViewerSession()
        {
            return new Dictionary<string, string>
            {
                ["assemblyQuantity"] = (_assemblyQuantity?.Value ?? 1).ToString(CultureInfo.InvariantCulture),
                ["mode"] = _mode.ToString(), ["search"] = _search.Text ?? "", ["sort"] = _sortProperty ?? "",
                ["ascending"] = _sortAscending.ToString(), ["selected"] = SelectionKey(Selected),
                ["expanded"] = new JavaScriptSerializer().Serialize(_expandedNodes.ToList()),
                ["hidden"] = new JavaScriptSerializer().Serialize(_hidden.ToList())
            };
        }
        private void SaveViewerSession()
        {
            try
            {
                var choices = CaptureViewerSession(); string signature = new JavaScriptSerializer().Serialize(choices);
                if (signature == _lastSession) return;
                _sessionLedger.State.Controls = choices; _sessionLedger.Save(); _lastSession = signature;
            }
            catch (Exception ex) { if (_summary != null) _summary.Text = "Görünüm kaydı yazılamadı: " + ex.Message; }
        }
        private void RestoreViewerSession()
        {
            var choices = _sessionLedger.State.Controls; string value;
            if (choices.Count == 0) return;
            decimal assemblyCount;
            if (choices.TryGetValue("assemblyQuantity", out value) && decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out assemblyCount))
                _assemblyQuantity.Value = Math.Max(_assemblyQuantity.Minimum, Math.Min(_assemblyQuantity.Maximum, assemblyCount));
            ViewerListMode mode;
            if (choices.TryGetValue("mode", out value) && Enum.TryParse(value, out mode)) SetViewMode(mode, !_snapshot.IsLoading);
            if (choices.TryGetValue("expanded", out value)) { _expandedNodes.Clear(); foreach (string key in new JavaScriptSerializer().Deserialize<List<string>>(value)) _expandedNodes.Add(key); }
            if (choices.TryGetValue("hidden", out value)) { _hidden.Clear(); foreach (string key in new JavaScriptSerializer().Deserialize<List<string>>(value)) _hidden.Add(key); }
            if (choices.TryGetValue("sort", out value)) _sortProperty = value;
            if (choices.TryGetValue("ascending", out value)) _sortAscending = value == "True";
            if (choices.TryGetValue("search", out value)) _search.Text = value;
            if (choices.TryGetValue("selected", out value)) _savedSelection = value;
            ApplyFilter(); RestoreSelection(_savedSelection);
        }

        private static string PrefPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "assembly_viewer_prefs.json");

        private static ViewerPreferences LoadPreferences()
        {
            try
            {
                if (File.Exists(PrefPath))
                    return new JavaScriptSerializer().Deserialize<ViewerPreferences>(File.ReadAllText(PrefPath)) ?? new ViewerPreferences();
            }
            catch { }
            return new ViewerPreferences();
        }

        private void NormalizePreferences()
        {
            if (_prefs == null) _prefs = new ViewerPreferences();
            if (_prefs.VisibleOptionalColumns == null) _prefs.VisibleOptionalColumns = new List<string>();
            if (_prefs.Widths == null) _prefs.Widths = new Dictionary<string, int>();
            if (_prefs.SplitterDistance < 760 || _prefs.SplitterDistance > 5000) _prefs.SplitterDistance = 820;
        }

        private void SavePreferences()
        {
            try
            {
                NormalizePreferences();
                _prefs.VisibleOptionalColumns.Clear();
                _prefs.Widths.Clear();
                foreach (DataGridViewColumn c in _grid.Columns)
                {
                    if (c.Name != "KindIcon") _prefs.Widths[c.Name] = c.Width;
                    if (IsOptionalColumn(c) && c.Visible) _prefs.VisibleOptionalColumns.Add(c.Name);
                }
                if (_leftPanel != null && _leftPanel.Width > 0) _prefs.SplitterDistance = _leftPanel.Width;
                Directory.CreateDirectory(Path.GetDirectoryName(PrefPath));
                File.WriteAllText(PrefPath, new JavaScriptSerializer().Serialize(_prefs));
            }
            catch { }
        }

        private sealed class ViewerSortComparer : IComparer<object>
        {
            private readonly string _property;
            public ViewerSortComparer(string property) { _property = property ?? ""; }
            public int Compare(object x, object y) { return CompareValues(x, y, _property); }
        }

        private sealed class SrcToolStripRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                var button = e.Item as ToolStripButton;
                if (button == null)
                {
                    base.OnRenderButtonBackground(e);
                    return;
                }

                Color back = button.Checked ? Accent : Surface;
                if (!button.Checked && button.Selected) back = Color.FromArgb(243, 245, 248);

                using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, button.Size));
                using (var pen = new Pen(button.Checked ? Accent : Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, button.Width - 1), Math.Max(0, button.Height - 1));
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var button = e.Item as ToolStripButton;
                if (button == null)
                {
                    base.OnRenderItemText(e);
                    return;
                }

                Color textColor = button.Checked ? Color.White : Color.FromArgb(31, 36, 42);
                TextRenderer.DrawText(
                    e.Graphics,
                    e.Text,
                    e.TextFont,
                    e.TextRectangle,
                    textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

    }
}
