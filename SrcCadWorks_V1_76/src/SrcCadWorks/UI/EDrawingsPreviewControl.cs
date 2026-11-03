using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    /// <summary>
    /// Embedded eDrawings ActiveX preview. The viewer itself owns zoom/pan/rotate/fit
    /// through its native mouse/right-click interface; SrcCadWorks only switches between
    /// 3D and drawing documents. This keeps the UI close to the user's preferred
    /// earlier eDrawings workflow.
    /// </summary>
    internal sealed class EDrawingsPreviewControl : UserControl
    {
        private const string EDrawingsViewClsid = "22945A69-1191-4DCF-9E6F-409BDE94D101";
        private readonly Panel _viewerPanel;
        private readonly Label _status;
        private readonly Button _partButton;
        private readonly Button _drawingButton;
        private readonly Button _backButton;
        private readonly Button _measureButton;
        private readonly Button _reloadButton;
        private readonly ToolTip _fileHint = new ToolTip();
        private EDrawingsAxHost _host;
        private dynamic _viewer;
        private dynamic _markup;
        private LaserPartRow _row;
        private bool _initializationFailed;
        private bool _expanded;
        private bool _showingPart = true;
        private bool _measureMode;
        private readonly Timer _measureProbeTimer;
        private int _measureProbeAttempts;
        private bool _shutdown;

        public event EventHandler PreviewExpandRequested;
        public event EventHandler BackRequested;

        public EDrawingsPreviewControl()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(248, 249, 251);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.White,
                Margin = Padding.Empty,
                Padding = new Padding(3, 3, 3, 2)
            };

            _backButton = MakeToolButton("← GERİ", 78);
            _partButton = MakeToolButton("3D", 62);
            _drawingButton = MakeToolButton("TEKNİK RESİM", 112);
            _measureButton = MakeToolButton("ÖLÇÜM", 82);
            _reloadButton = MakeToolButton("YENİLE", 72);
            _reloadButton.Enabled = false;
            _fileHint.SetToolTip(_reloadButton, "Kayıtlı dosyayı yeniden açar. SOLIDWORKS değişikliklerini önce kaydedin.");
            _reloadButton.Click += (s, e) => { if (_showingPart) ShowPart(); else ShowDrawing(); };
            _backButton.Visible = false;
            _measureButton.Visible = true;
            _measureButton.Enabled = false;

            // Only these two view selectors are shown in normal mode. eDrawings'
            // own right-click/mouse controls handle zoom, pan, rotate and fit.
            _backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
            _partButton.Click += (s, e) =>
            {
                PreviewModePreference.SelectDrawing(false);
                ShowPart();
                PreviewExpandRequested?.Invoke(this, EventArgs.Empty);
            };
            _drawingButton.Click += (s, e) =>
            {
                PreviewModePreference.SelectDrawing(true);
                ShowDrawing();
                PreviewExpandRequested?.Invoke(this, EventArgs.Empty);
            };
            _measureButton.Click += (s, e) => ToggleMeasure();

            _partButton.Image = CadDocumentIcons.Part(18); _partButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _drawingButton.Width = 142; _drawingButton.Image = CadDocumentIcons.Drawing(18); _drawingButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            toolbar.Controls.Add(_backButton);
            toolbar.Controls.Add(_partButton);
            toolbar.Controls.Add(_drawingButton);
            toolbar.Controls.Add(_measureButton);
            toolbar.Controls.Add(_reloadButton);
            toolbar.AutoScroll = true;

            _viewerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(243, 245, 248),
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 2, 0, 2)
            };

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(94, 103, 113),
                BackColor = BackColor,
                Font = new Font("Segoe UI", 8F),
                AutoEllipsis = true,
                Text = "eDrawings önizleme hazırlanıyor..."
            };

            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(_viewerPanel, 0, 1);
            root.Controls.Add(_status, 0, 2);
            Controls.Add(root);

            _drawingButton.Enabled = false;
            _measureProbeTimer = new Timer { Interval = 250 };
            _measureProbeTimer.Tick += (s, e) =>
            {
                _measureProbeAttempts++;
                UpdateMeasureAvailability();
                if (_measureButton.Enabled || _measureProbeAttempts >= 8) _measureProbeTimer.Stop();
            };
            HandleCreated += (s, e) => EnsureViewer();
        }

        private Button MakeToolButton(string text, int width)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(31, 36, 42),
                Margin = new Padding(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8F)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(205, 211, 218);
            return b;
        }

        public void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            _backButton.Visible = expanded;
            _measureButton.Visible = true;

            // Büyük eDrawings ekranında yapılan seçim/gölgelendirme kompakt görünüme taşınmasın.
            // Ölçüm sırasında seçimleri koruruz; GERİ veya görünüm değişiminde temizleriz.
            if (!expanded) ResetViewerInteractionState(true);

            UpdateMeasureAvailability();
            UpdateToolbarState();
            _status.Text = expanded
                ? "eDrawings • sağ tık/fare ile yakınlaştır, kaydır, döndür ve sığdır"
                : "eDrawings • 3D / TEKNİK RESİM / ÖLÇÜM araçları doğrudan kullanılabilir";
        }

        private bool EnsureViewer()
        {
            if (_shutdown) return false;
            if (_viewer != null) return true;
            if (_initializationFailed) return false;
            try
            {
                _host = new EDrawingsAxHost(EDrawingsViewClsid)
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(243, 245, 248)
                };

                var init = _host as ISupportInitialize;
                init?.BeginInit();
                _viewerPanel.Controls.Add(_host);
                init?.EndInit();
                _host.CreateControl();
                _viewer = _host.OcxObject;

                try { _viewer.BackgroundColorOverride = true; } catch { }
                try { _viewer.BackgroundColorGradient = false; } catch { }
                try { _viewer.BackgroundColor = ColorTranslator.ToOle(Color.FromArgb(243, 245, 248)); } catch { }
                try { _viewer.ShowShadedEdges = true; } catch { }
                _status.Text = "eDrawings hazır • 3D veya teknik resme tıklayın";
                return true;
            }
            catch (Exception ex)
            {
                _initializationFailed = true;
                _status.Text = "eDrawings ActiveX başlatılamadı. eDrawings 2024 kurulumunu kontrol edin.";
                Logger.Error("eDrawings önizleme başlatılamadı", ex);
                return false;
            }
        }

        public void LoadRow(LaserPartRow row)
        {
            if (_shutdown) return;
            ExitMeasureMode();
            _row = row;
            _reloadButton.Enabled = row != null;
            _fileHint.SetToolTip(_viewerPanel, row == null ? "" :
                "Dosya: " + row.SourcePath + "\nMontaj konfigürasyonu: " + row.Configuration +
                "\neDrawings kayıtlı dosya görünümünü gösterir.");
            _drawingButton.Enabled = row != null && row.HasDrawing && File.Exists(row.DrawingPath);
            _partButton.Enabled = row != null && File.Exists(row.SourcePath);
            if (row == null)
            {
                CloseDocument();
                _status.Text = "Önizleme için bir parça seçin.";
                return;
            }

            bool preferDrawing = PreviewModePreference.PreferDrawing;
            if (preferDrawing && _drawingButton.Enabled)
                ShowDrawing();
            else if (_partButton.Enabled)
            {
                ShowPart();
                if (preferDrawing) _status.Text = "Teknik resim bulunamadı • 3D gösteriliyor.";
            }
            else if (_drawingButton.Enabled)
                ShowDrawing();
            else
            {
                CloseDocument();
                _status.Text = "Önizleme için dosya bulunamadı.";
            }
        }

        public void ShowPart()
        {
            if (_row == null || !File.Exists(_row.SourcePath)) return;
            _showingPart = true;
            OpenDocument(_row.SourcePath, true);
        }

        public void ShowDrawing()
        {
            if (_row == null || !_row.HasDrawing || !File.Exists(_row.DrawingPath)) return;
            _showingPart = false;
            OpenDocument(_row.DrawingPath, false);
        }

        private void OpenDocument(string path, bool isPart)
        {
            var form = FindForm();
            Control keyboardOwner = form?.ActiveControl;
            while (keyboardOwner is ContainerControl container && container.ActiveControl != null)
                keyboardOwner = container.ActiveControl;
            bool restoreKeyboard = keyboardOwner != null && keyboardOwner.ContainsFocus && !ContainsFocus;
            if (!EnsureViewer()) return;
            try
            {
                ExitMeasureMode();
                ResetViewerInteractionState(false);
                CloseDocument();
                _status.Text = (isPart ? "3D" : "Teknik resim") + " yükleniyor: " + Path.GetFileName(path);


                // OpenDoc(file, isTemp, promptToSave, readOnly, commandString)
                _viewer.OpenDoc(path, false, false, true, "");
                try { _viewer.BackgroundColorOverride = true; } catch { }
                try { _viewer.BackgroundColorGradient = false; } catch { }
                try { _viewer.BackgroundColor = ColorTranslator.ToOle(Color.FromArgb(243, 245, 248)); } catch { }
                try { _viewer.ShowShadedEdges = true; } catch { }
                if (isPart)
                {
                    try { _viewer.ViewOrientation = 6; } catch { } // Isometric
                }
                FitToWindow();
                try { _viewer.Refresh(); } catch { }
                EnsureMarkupControl();
                StartMeasureProbe();
                UpdateToolbarState();
                _status.Text = _expanded
                    ? (isPart ? "3D parça" : "Teknik resim") + " • sağ tık/fare ile görüntüyü kontrol edin"
                    : (isPart ? "3D parça" : "Teknik resim") + " • ÖLÇÜM doğrudan kullanılabilir; büyütmek için 3D/TEKNİK RESİM düğmesine basın";
            }
            catch (Exception ex)
            {
                _status.Text = "eDrawings dosyayı açamadı: " + Path.GetFileName(path);
                Logger.Error("eDrawings dosya açma hatası: " + path, ex);
            }
            finally
            {
                // Loading a native ActiveX document must not take keyboard navigation
                // away from the table/editor that initiated the preview.
                if (restoreKeyboard && keyboardOwner != null && !keyboardOwner.IsDisposed && keyboardOwner.CanFocus)
                    keyboardOwner.Focus();
            }
        }

        public void FitToWindow()
        {
            if (_viewer == null) return;
            try { _viewer.ViewOrientation = 7; } catch { } // ZoomToFit
            try { _viewer.Refresh(); } catch { }
        }

        private bool EnsureMarkupControl()
        {
            if (_markup != null) return true;
            if (_viewer == null) return false;
            try
            {
                // Official eDrawings markup/measure add-in ProgID. Kept dynamic so
                // SrcCadWorks does not require a compile-time eDrawings Interop reference.
                _markup = _viewer.CoCreateInstance("EModelViewMarkup.EmodelMarkupControl");
                return _markup != null;
            }
            catch (Exception ex)
            {
                Logger.Info("eDrawings ölçüm denetimi kullanılamıyor: " + ex.Message);
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
            _measureButton.Enabled = enabled && _row != null;
            if (!enabled)
            {
                _measureMode = false;
                if (_row != null && _measureProbeAttempts >= 8)
                    _status.Text = "eDrawings • bu belge ölçüme izin vermiyor; 3D/TEKNİK görüntüleme kullanılabilir";
            }
        }

        private void ToggleMeasure()
        {
            if (_measureMode)
            {
                ExitMeasureMode();
                _status.Text = (_showingPart ? "3D parça" : "Teknik resim") + " • ölçüm kapatıldı";
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

                // EMVMarkupOperators.eMVOperatorMeasure = 11.
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
                _status.Text = "Ölçüm modu başlatılamadı.";
                Logger.Error("eDrawings ölçüm modu açılamadı", ex);
            }
        }

        private void ExitMeasureMode()
        {
            if (!_measureMode) return;
            try { if (_viewer != null) _viewer.ViewOperator = 0; } catch { } // Select
            _measureMode = false;
            try { if (_viewer != null) _viewer.ClearSelections(); } catch { }
            UpdateToolbarState();
        }

        private void ResetViewerInteractionState(bool restoreShadedView)
        {
            if (_viewer == null) return;
            try { _viewer.ClearSelections(); } catch { }
            try { _viewer.ViewOperator = 0; } catch { } // Select

            if (restoreShadedView && _showingPart)
            {
                // eMVShaded = 2. Sağ tık menüsünden farklı display style seçildiyse GERİ'de
                // varsayılan gölgeli + kenarlı görünüme dön.
                try { _viewer.set_ViewState(2, true); } catch { }
                try { _viewer.ShowShadedEdges = true; } catch { }
                try { _viewer.ViewOrientation = 6; } catch { } // Isometric
                FitToWindow();
            }

            try { _viewer.Refresh(); } catch { }
        }

        private void UpdateToolbarState()
        {
            SetActiveButton(_partButton, _showingPart && !_measureMode);
            SetActiveButton(_drawingButton, !_showingPart && !_measureMode);
            SetActiveButton(_measureButton, _measureMode);
        }

        private static void SetActiveButton(Button button, bool active)
        {
            if (button == null) return;
            button.BackColor = active ? Color.FromArgb(202, 28, 36) : Color.White;
            button.FlatAppearance.BorderColor = active ? Color.FromArgb(202, 28, 36) : Color.FromArgb(205, 211, 218);
            button.ForeColor = active ? Color.White : Color.FromArgb(31, 36, 42);
        }

        public void CloseDocument()
        {
            try { _measureProbeTimer.Stop(); } catch { }
            ExitMeasureMode();
            if (_viewer == null) return;
            try { _viewer.ClearSelections(); } catch { }
            try { _viewer.CloseActiveDoc(""); } catch { }
            _measureButton.Enabled = false;
            UpdateToolbarState();
        }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            try { _measureProbeTimer.Stop(); } catch { }
            try { CloseDocument(); } catch { }

            // AxHost, OcxObject RCW'sinin sahibidir. OcxObject/markup nesnelerine burada
            // FinalReleaseComObject uygulamak ve hemen ardından AxHost.Dispose çağırmak,
            // eDrawings kapanışında aynı COM nesnesinin ikinci kez serbest bırakılmasına ve
            // SOLIDWORKS prosesinde 0xc0000005 / AccessViolation oluşmasına yol açabiliyor.
            // RCW referanslarını bırakıp gerçek COM ömrünü AxHost/WinForms'a devrediyoruz.
            _markup = null;
            _viewer = null;
            try { if (_host != null) _host.Enabled = false; } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
                _measureProbeTimer?.Dispose();
                _fileHint.Dispose();
                _partButton.Image?.Dispose(); _drawingButton.Image?.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class EDrawingsAxHost : AxHost
        {
            public EDrawingsAxHost(string clsid) : base(clsid) { }
            public object OcxObject => GetOcx();
        }
    }
}
