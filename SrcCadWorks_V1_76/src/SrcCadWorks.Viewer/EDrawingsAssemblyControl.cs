using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SrcCadWorks.Viewer
{
    internal sealed class EDrawingsAssemblyControl : UserControl
    {
        private const string Clsid = "22945A69-1191-4DCF-9E6F-409BDE94D101";
        private static readonly Color Accent = Color.FromArgb(202, 28, 36);
        private static readonly Color Border = Color.FromArgb(205, 211, 218);
        private static readonly Color TextColor = Color.FromArgb(31, 36, 42);
        private static readonly Color Muted = Color.FromArgb(94, 103, 113);
        private static readonly Color ViewerBackground = Color.FromArgb(243, 245, 248);

        private Panel _panel;
        private Label _status;
        private Button _modelButton;
        private Button _drawingButton;
        private Button _measureButton;
        private Button _fitButton;
        private Host _host;
        private dynamic _viewer;
        private dynamic _markup;
        private string _sourcePath = "";
        private string _drawingPath = "";
        private string _currentPath = "";
        private bool _showingPart = true;
        private bool _measureMode;
        private readonly Timer _measureProbeTimer;
        private int _measureProbeAttempts;
        private bool _shutdown;

        public EDrawingsAssemblyControl()
        {
            Dock = DockStyle.Fill;
            BackColor = ViewerBackground;
            Font = new Font("Segoe UI", 9F);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = ViewerBackground
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            root.Controls.Add(BuildToolbar(), 0, 0);
            _panel = new Panel { Dock = DockStyle.Fill, BackColor = ViewerBackground, BorderStyle = BorderStyle.FixedSingle };
            root.Controls.Add(_panel, 0, 1);
            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                Text = "eDrawings hazırlanıyor...",
                Padding = new Padding(8, 0, 8, 0),
                AutoEllipsis = true
            };
            root.Controls.Add(_status, 0, 2);
            Controls.Add(root);

            _measureProbeTimer = new Timer { Interval = 350 };
            _measureProbeTimer.Tick += (s, e) =>
            {
                _measureProbeAttempts++;
                UpdateMeasureAvailability();
                if (_measureButton.Enabled || _measureProbeAttempts >= 8) _measureProbeTimer.Stop();
            };
            HandleCreated += (s, e) => EnsureViewer();
        }

        private Control BuildToolbar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                BackColor = Color.White,
                Padding = new Padding(6, 5, 6, 5),
                Margin = Padding.Empty
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _modelButton = MakeButton("3D");
            _modelButton.Image = SrcCadWorks.Services.CadDocumentIcons.Part(18);
            _modelButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _modelButton.Click += (s, e) => { SrcCadWorks.Services.PreviewModePreference.SelectDrawing(false); LoadDocument(_sourcePath, true); };
            _drawingButton = MakeButton("TEKNİK");
            _drawingButton.Image = SrcCadWorks.Services.CadDocumentIcons.Drawing(18); _drawingButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _drawingButton.Click += (s, e) => { SrcCadWorks.Services.PreviewModePreference.SelectDrawing(true); LoadDocument(_drawingPath, false); };
            _measureButton = MakeButton("ÖLÇÜM");
            _measureButton.Enabled = false;
            _measureButton.Click += (s, e) => ToggleMeasure();
            _fitButton = MakeButton("SIĞDIR");
            _fitButton.Click += (s, e) => FitToWindow();

            bar.Controls.Add(_modelButton, 0, 0);
            bar.Controls.Add(_drawingButton, 1, 0);
            bar.Controls.Add(_measureButton, 2, 0);
            bar.Controls.Add(_fitButton, 3, 0);
            bar.Controls.Add(new Label
            {
                Text = "eDrawings: döndür • yakınlaştır • kaydır • ölçüm",
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(10, 0, 0, 0)
            }, 4, 0);
            UpdateToolbarState();
            return bar;
        }

        private static Button MakeButton(string text)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                Margin = new Padding(2),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Border;
            return button;
        }

        public void LoadPath(string path)
        {
            LoadPaths(path, "");
        }

        public void LoadPaths(string sourcePath, string drawingPath)
        {
            _sourcePath = File.Exists(sourcePath ?? "") ? sourcePath : "";
            _drawingPath = File.Exists(drawingPath ?? "") ? drawingPath : "";
            if (string.IsNullOrWhiteSpace(_drawingPath) && !string.IsNullOrWhiteSpace(_sourcePath))
            {
                string besideModel = Path.ChangeExtension(_sourcePath, ".SLDDRW");
                if (File.Exists(besideModel)) _drawingPath = besideModel;
            }
            _modelButton.Enabled = !string.IsNullOrWhiteSpace(_sourcePath);
            _drawingButton.Enabled = !string.IsNullOrWhiteSpace(_drawingPath);

            bool preferDrawing = SrcCadWorks.Services.PreviewModePreference.PreferDrawing;
            if (preferDrawing && !string.IsNullOrWhiteSpace(_drawingPath))
                LoadDocument(_drawingPath, false);
            else if (!string.IsNullOrWhiteSpace(_sourcePath))
            {
                LoadDocument(_sourcePath, true);
                if (preferDrawing) _status.Text = "Teknik resim bulunamadı • 3D gösteriliyor.";
            }
            else if (!string.IsNullOrWhiteSpace(_drawingPath))
                LoadDocument(_drawingPath, false);
            else
            {
                CloseDocument();
                _status.Text = "Önizleme için dosya bulunamadı.";
            }
        }

        private void LoadDocument(string path, bool isPart)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _status.Text = isPart ? "3D dosyası bulunamadı." : "Teknik resim bulunamadı.";
                return;
            }
            if (!EnsureViewer()) return;

            try
            {
                ExitMeasureMode();
                if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    try { _viewer.CloseActiveDoc(""); } catch { }
                    _status.Text = "Yükleniyor: " + Path.GetFileName(path);
                    Application.DoEvents();
                    _viewer.OpenDoc(path, false, false, true, "");
                    _currentPath = path;
                }

                _showingPart = isPart;
                ApplyViewerAppearance();
                if (isPart)
                {
                    try { _viewer.ViewOrientation = 6; } catch { }
                    try { _viewer.ShowShadedEdges = true; } catch { }
                }
                FitToWindow();
                EnsureMarkupControl();
                StartMeasureProbe();
                UpdateToolbarState();
                _status.Text = (isPart ? "3D" : "Teknik resim") + " • " + Path.GetFileName(path) + " • fare/sağ tık ile döndür, zoom ve pan";
            }
            catch (Exception ex)
            {
                _status.Text = "eDrawings açılamadı: " + ex.Message;
            }
        }

        private bool EnsureViewer()
        {
            if (_shutdown) return false;
            if (_viewer != null) return true;
            try
            {
                _host = new Host(Clsid) { Dock = DockStyle.Fill, BackColor = BackColor };
                var init = _host as ISupportInitialize;
                init?.BeginInit();
                _panel.Controls.Add(_host);
                init?.EndInit();
                _host.CreateControl();
                _viewer = _host.OcxObject;
                ApplyViewerAppearance();
                _status.Text = "eDrawings hazır";
                return true;
            }
            catch (Exception ex)
            {
                _status.Text = "eDrawings ActiveX başlatılamadı: " + ex.Message;
                return false;
            }
        }

        private void ApplyViewerAppearance()
        {
            if (_viewer == null) return;
            try { _viewer.BackgroundColorOverride = true; } catch { }
            try { _viewer.BackgroundColorGradient = false; } catch { }
            try { _viewer.BackgroundColor = ColorTranslator.ToOle(ViewerBackground); } catch { }
            try { _viewer.ShowShadedEdges = true; } catch { }
            try { _viewer.Refresh(); } catch { }
        }

        public void FitToWindow()
        {
            if (_viewer == null) return;
            try { _viewer.ViewOrientation = 7; }
            catch
            {
                try { _viewer.FitToWindow(); }
                catch { try { _viewer.ZoomToFit(); } catch { } }
            }
            try { _viewer.Refresh(); } catch { }
        }

        private bool EnsureMarkupControl()
        {
            if (_markup != null) return true;
            if (_viewer == null) return false;
            try
            {
                _markup = _viewer.CoCreateInstance("EModelViewMarkup.EmodelMarkupControl");
                return _markup != null;
            }
            catch
            {
                _markup = null;
                return false;
            }
        }

        private void StartMeasureProbe()
        {
            try { _measureProbeTimer.Stop(); } catch { }
            _measureProbeAttempts = 0;
            UpdateMeasureAvailability();
            if (!_measureButton.Enabled) _measureProbeTimer.Start();
        }

        private void UpdateMeasureAvailability()
        {
            bool enabled = false;
            try
            {
                if (EnsureMarkupControl()) enabled = Convert.ToBoolean(_markup.IsMeasureEnabled);
                else if (_viewer != null) enabled = Convert.ToBoolean(_viewer.IsMeasureEnabled);
            }
            catch { enabled = false; }
            _measureButton.Enabled = enabled && !string.IsNullOrWhiteSpace(_currentPath);
            if (!enabled) _measureMode = false;
            UpdateToolbarState();
        }

        private void ToggleMeasure()
        {
            if (_measureMode)
            {
                ExitMeasureMode();
                _status.Text = (_showingPart ? "3D" : "Teknik resim") + " • ölçüm kapatıldı";
                return;
            }
            if (!EnsureMarkupControl())
            {
                _status.Text = "Bu sistemde eDrawings ölçüm bileşeni kullanılamıyor.";
                return;
            }
            try
            {
                if (!Convert.ToBoolean(_markup.IsMeasureEnabled))
                {
                    _measureButton.Enabled = false;
                    _status.Text = "Bu eDrawings belgesinde ölçüm etkin değil.";
                    return;
                }
                _markup.ViewOperator = 11;
                _measureMode = true;
                try { _viewer.Refresh(); } catch { }
                UpdateToolbarState();
                _status.Text = "ÖLÇÜM aktif • kenar, nokta veya yüzey seçin; sonuç eDrawings üzerinde gösterilir";
            }
            catch (Exception ex)
            {
                _measureMode = false;
                UpdateToolbarState();
                _status.Text = "Ölçüm modu başlatılamadı: " + ex.Message;
            }
        }

        private void ExitMeasureMode()
        {
            if (!_measureMode) return;
            try { if (_viewer != null) _viewer.ViewOperator = 0; } catch { }
            _measureMode = false;
            try { if (_viewer != null) _viewer.ClearSelections(); } catch { }
            UpdateToolbarState();
        }

        private void UpdateToolbarState()
        {
            SetActiveButton(_modelButton, _showingPart && !_measureMode);
            SetActiveButton(_drawingButton, !_showingPart && !_measureMode);
            SetActiveButton(_measureButton, _measureMode);
        }

        private static void SetActiveButton(Button button, bool active)
        {
            if (button == null) return;
            button.BackColor = active ? Accent : Color.White;
            button.FlatAppearance.BorderColor = active ? Accent : Border;
            button.ForeColor = active ? Color.White : TextColor;
        }

        public void CloseDocument()
        {
            try { _measureProbeTimer.Stop(); } catch { }
            ExitMeasureMode();
            if (_viewer != null)
            {
                try { _viewer.ClearSelections(); } catch { }
                try { _viewer.CloseActiveDoc(""); } catch { }
            }
            _currentPath = "";
            _measureButton.Enabled = false;
            UpdateToolbarState();
        }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            try { _measureProbeTimer.Stop(); } catch { }

            // AxHost, eDrawings ActiveX nesnesinin COM ömrünü kendi yönetir.
            // GetOcx() ile alınan _viewer/_markup RCW'lerini FinalReleaseComObject ile
            // ayrıca serbest bırakmak AxHost'un sonlandırmasıyla çakışıp native
            // 0xc0000005 / AccessViolation hatasına yol açabiliyordu. Kapanışta artık
            // ActiveX'e ek COM çağrısı yapmadan referansları bırakıp Host'u tek kez Dispose ediyoruz.
            _measureMode = false;
            _markup = null;
            _viewer = null;

            try
            {
                if (_host != null)
                {
                    try { _panel?.Controls.Remove(_host); } catch { }
                    _host.Dispose();
                }
            }
            catch { }
            _host = null;
            _currentPath = "";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
                _measureProbeTimer?.Dispose();
                _modelButton.Image?.Dispose();
                _drawingButton.Image?.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class Host : AxHost
        {
            public Host(string clsid) : base(clsid) { }
            public object OcxObject => GetOcx();
        }
    }
}
