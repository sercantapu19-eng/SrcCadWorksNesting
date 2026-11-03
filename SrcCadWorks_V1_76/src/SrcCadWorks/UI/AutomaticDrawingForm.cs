using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    internal sealed class AutomaticDrawingForm : Form
    {
        private readonly SldWorks _swApp;
        private ResumeSession _resume;
        private bool _drawingBusy;
        private readonly AutomaticDrawingService _service;
        private readonly AutomaticDrawingPreferences _preferences;
        private readonly Label _partName;
        private readonly Label _partPath;
        private readonly TextBox _template;
        private readonly CheckBox _importAnnotations;
        private readonly CheckBox _autoDimension;
        private readonly CheckBox _keepAbove;
        private readonly Button _createButton;
        private readonly Button _openExistingButton;
        private readonly Label _status;
        private SolidWorksOwnedWindow.Tracker _ownerTracker;
        private string _lastDrawingPath = "";

        private static readonly Color Background = Color.FromArgb(244, 246, 249);
        private static readonly Color Surface = Color.White;
        private static readonly Color TextColor = Color.FromArgb(31, 36, 42);
        private static readonly Color Muted = Color.FromArgb(94, 103, 113);
        private static readonly Color Border = Color.FromArgb(205, 211, 218);
        private static readonly Color Accent = Color.FromArgb(202, 28, 36);
        private static readonly Color Success = Color.FromArgb(27, 143, 89);

        public AutomaticDrawingForm(SldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _service = new AutomaticDrawingService(_swApp);
            _preferences = AutomaticDrawingPreferencesStore.Load();

            Text = "SrcCadWorks — Otomatik Teknik Resim " + AppVersion.Display;
            SrcCadWorks.Services.BrandIcon.Apply(this);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 505);
            MinimumSize = new Size(650, 470);
            BackColor = Background;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(16),
                BackColor = Background
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);

            var partCard = Card();
            partCard.Padding = new Padding(12, 8, 12, 8);
            partCard.Controls.Add(new Label
            {
                Text = "AKTİF PARÇA",
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = Muted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold)
            });
            _partName = new Label
            {
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                AutoEllipsis = true
            };
            partCard.Controls.Add(_partName);
            _partName.BringToFront();
            _partPath = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            partCard.Controls.Add(_partPath);
            _partPath.BringToFront();
            root.Controls.Add(partCard, 0, 1);

            var templateCard = Card();
            templateCard.Padding = new Padding(12, 10, 12, 10);
            var templateLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Surface };
            templateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            templateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            templateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            templateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            templateLayout.Controls.Add(new Label
            {
                Text = "TEKNİK RESİM ŞABLONU (.DRWDOT)",
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            templateLayout.SetColumnSpan(templateLayout.GetControlFromPosition(0, 0), 2);
            _template = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = _preferences.DrawingTemplatePath ?? "",
                Margin = new Padding(0, 7, 8, 5)
            };
            _template.TextChanged += (s, e) => SavePreferences();
            var browse = MakeButton("GÖZAT", false);
            browse.Margin = new Padding(0, 5, 0, 3);
            browse.Click += BrowseTemplate;
            templateLayout.Controls.Add(_template, 0, 1);
            templateLayout.Controls.Add(browse, 1, 1);
            templateCard.Controls.Add(templateLayout);
            root.Controls.Add(templateCard, 0, 2);

            var optionsCard = Card();
            optionsCard.Padding = new Padding(12, 8, 12, 6);
            var optionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Surface
            };
            _importAnnotations = new CheckBox
            {
                Text = "İşaretli model ölçülerini/toleransları ana (ön) görünüşe aktar",
                AutoSize = true,
                Checked = _preferences.ImportModelAnnotations,
                Margin = new Padding(0, 2, 0, 5)
            };
            _autoDimension = new CheckBox
            {
                Text = "Ana görünüşte model ölçüsü yoksa otomatik ölçülendir",
                AutoSize = true,
                Checked = _preferences.AutoDimensionFallback,
                Margin = new Padding(0, 2, 0, 5)
            };
            _keepAbove = new CheckBox
            {
                Text = "Pencereyi SOLIDWORKS üzerinde tut",
                AutoSize = true,
                Checked = _preferences.KeepAboveSolidWorks,
                Margin = new Padding(0, 2, 0, 0)
            };
            _importAnnotations.CheckedChanged += (s, e) => SavePreferences();
            _autoDimension.CheckedChanged += (s, e) => SavePreferences();
            _keepAbove.CheckedChanged += KeepAboveChanged;
            optionsPanel.Controls.Add(_importAnnotations);
            optionsPanel.Controls.Add(_autoDimension);
            optionsPanel.Controls.Add(_keepAbove);
            optionsCard.Controls.Add(optionsPanel);
            root.Controls.Add(optionsCard, 0, 3);

            var info = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Background,
                ForeColor = Muted,
                Padding = new Padding(4, 12, 4, 4),
                Text = "V1.47: İlk açı kullanır. Ön görünüş sol üstte, yan görünüş sağında, üst görünüş altında yerleşir. Sac parçada Flat Pattern çevrilmeden (Flip kapalı) native UP/DOWN büküm notlarıyla; izometrik görünüş ayrı alanda oluşturulur. Görünüş eksik/çakışık ise dosya kaydedilmez.",
                AutoSize = false
            };
            root.Controls.Add(info, 0, 4);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Background };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            _status = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            _openExistingButton = MakeButton("TEKNİK RESMİ AÇ", false);
            _openExistingButton.Enabled = false;
            _openExistingButton.Margin = new Padding(8, 5, 8, 3);
            _openExistingButton.Click += (s, e) => OpenLastDrawing();
            _createButton = MakeButton("TEKNİK RESİM OLUŞTUR", true);
            _createButton.Margin = new Padding(0, 5, 0, 3);
            _createButton.Click += CreateDrawing;
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(_openExistingButton, 1, 0);
            footer.Controls.Add(_createButton, 2, 0);
            root.Controls.Add(footer, 0, 5);

            Shown += (s, e) =>
            {
                _ownerTracker = SolidWorksOwnedWindow.Track(this, _swApp, _preferences.KeepAboveSolidWorks);
                RefreshActivePart();
            };
            Activated += (s, e) => RefreshActivePart();
            FormClosed += (s, e) =>
            {
                try { _ownerTracker?.Dispose(); } catch { }
                _ownerTracker = null;
                SavePreferences();
            };

            _resume = new ResumeSession(this, _swApp, "AutomaticDrawing", () => new object[0], () => _drawingBusy,
                () => { RefreshActivePart(); _resume.AfterScan(); }, () => CreateDrawing(null, EventArgs.Empty));
            _resume.AfterScan();
            Height += 42;
            RefreshActivePart();
        }

        public bool KeepAboveSolidWorksRequested => _keepAbove != null && _keepAbove.Checked;

        private Control BuildHeader()
        {
            var panel = Card();
            panel.Padding = new Padding(12, 7, 12, 7);
            var title = new Label
            {
                Text = "OTOMATİK TEKNİK RESİM",
                Dock = DockStyle.Top,
                Height = 27,
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold)
            };
            var sub = new Label
            {
                Text = "SLDPRT → SLDDRW  •  " + AppVersion.Display,
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(sub);
            panel.Controls.Add(title);
            return panel;
        }

        private void RefreshActivePart()
        {
            try
            {
                var doc = _swApp.ActiveDoc as ModelDoc2;
                bool ready = doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                _createButton.Enabled = ready;
                if (!ready)
                {
                    _partName.Text = "SLDPRT açık değil";
                    _partPath.Text = "Otomatik teknik resim için bir parça belgesi açın.";
                    _status.Text = "Parça bekleniyor";
                    _status.ForeColor = Muted;
                    return;
                }

                _partName.Text = doc.GetTitle() ?? "(adsız parça)";
                _partPath.Text = doc.GetPathName() ?? "";
                if (string.IsNullOrWhiteSpace(_partPath.Text))
                {
                    _status.Text = "Önce parçayı kaydedin";
                    _status.ForeColor = Accent;
                }
                else
                {
                    string existing = Path.ChangeExtension(_partPath.Text, ".SLDDRW");
                    bool exists = File.Exists(existing);
                    _lastDrawingPath = exists ? existing : "";
                    _openExistingButton.Enabled = exists;
                    _status.Text = exists ? "Mevcut teknik resim bulundu; korunacak" : "Oluşturmaya hazır";
                    _status.ForeColor = exists ? Accent : Success;
                }
            }
            catch (Exception ex)
            {
                _createButton.Enabled = false;
                _status.Text = ex.Message;
                _status.ForeColor = Accent;
            }
        }

        private void BrowseTemplate(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "SOLIDWORKS Teknik Resim Şablonu Seç";
                dialog.Filter = "SOLIDWORKS Teknik Resim Şablonu (*.drwdot)|*.drwdot|Tüm dosyalar (*.*)|*.*";
                if (File.Exists(_template.Text))
                {
                    try
                    {
                        dialog.InitialDirectory = Path.GetDirectoryName(_template.Text);
                        dialog.FileName = Path.GetFileName(_template.Text);
                    }
                    catch { }
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _template.Text = dialog.FileName;
                    SavePreferences();
                }
            }
        }

        private void CreateDrawing(object sender, EventArgs e)
        {
            if (_drawingBusy) return;
            SavePreferences();
            var part = _swApp.ActiveDoc as ModelDoc2;
            string path = part == null ? "" : part.GetPathName() ?? "";
            if (string.IsNullOrEmpty(path)) { MessageBox.Show(this, "Parçayı önce diske kaydedin."); return; }
            try { _resume.Begin(Path.GetDirectoryName(path)); } catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }
            _drawingBusy = true;
            _createButton.Enabled = false;
            _openExistingButton.Enabled = false;
            Cursor = Cursors.WaitCursor;
            _status.Text = "Teknik resim oluşturuluyor...";
            _status.ForeColor = Muted;
            Application.DoEvents();

            try
            {
                var result = _service.CreateForActivePart(new AutomaticDrawingOptions
                {
                    DrawingTemplatePath = _template.Text,
                    ImportModelAnnotations = _importAnnotations.Checked,
                    AutoDimensionFallback = _autoDimension.Checked
                });

                if (result.Created || result.ExistingDrawing) { _resume.Commit("SLDDRW", null, result.DrawingPath); _resume.SaveProgress(new string[0], null); _resume.Finish(true); }
                _lastDrawingPath = result.DrawingPath ?? "";
                _openExistingButton.Enabled = File.Exists(_lastDrawingPath);
                _status.Text = result.Message;
                _status.ForeColor = result.Created ? Success : Accent;

                if (result.ExistingDrawing)
                {
                    var answer = MessageBox.Show(this,
                        result.Message + "\r\n\r\nMevcut teknik resmi açmak ister misiniz?",
                        "SrcCadWorks", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (answer == DialogResult.Yes) OpenLastDrawing();
                }
                else
                {
                    MessageBox.Show(this,
                        result.Message + "\r\n\r\nDosya: " + result.DrawingPath +
                        "\r\nGörünüş: " + result.ViewCount +
                        "\r\nYansıtma: " + (result.FirstAngleProjection ? "İlk açı" : "Doğrulanamadı") +
                        (result.IsSheetMetal ? "\r\nBüküm yönü koruması: " + (result.BendOrientationProtected ? "Aktif (Flat Flip kapalı)" : "Doğrulanamadı") : "") +
                        "\r\nAktarılan anotasyon: " + result.ImportedAnnotationCount +
                        "\r\nOtomatik ölçülendirilen görünüş: " + result.AutoDimensionedViewCount,
                        "SrcCadWorks — Otomatik Teknik Resim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                _resume.SaveProgress(new[] { "Teknik resim: " + ex.Message }, null);
                Logger.Error("Otomatik Teknik Resim oluşturulamadı", ex);
                _status.Text = ex.Message;
                _status.ForeColor = Accent;
                MessageBox.Show(this, ex.Message, "SrcCadWorks — Otomatik Teknik Resim", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _drawingBusy = false;
                _resume.Suspend();
                ComUtil.ManagedCleanup(true);
                Cursor = Cursors.Default;
                RefreshActivePart();
            }
        }

        private void OpenLastDrawing()
        {
            if (string.IsNullOrWhiteSpace(_lastDrawingPath) || !File.Exists(_lastDrawingPath)) return;
            if (!_service.OpenDrawing(_lastDrawingPath))
                MessageBox.Show(this, "Teknik resim açılamadı:\r\n" + _lastDrawingPath, "SrcCadWorks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void KeepAboveChanged(object sender, EventArgs e)
        {
            SavePreferences();
            try { _ownerTracker?.SetEnabled(_keepAbove.Checked); } catch { }
        }

        private void SavePreferences()
        {
            if (_preferences == null) return;
            _preferences.DrawingTemplatePath = _template?.Text ?? _preferences.DrawingTemplatePath;
            _preferences.ImportModelAnnotations = _importAnnotations?.Checked ?? _preferences.ImportModelAnnotations;
            _preferences.AutoDimensionFallback = _autoDimension?.Checked ?? _preferences.AutoDimensionFallback;
            _preferences.KeepAboveSolidWorks = _keepAbove?.Checked ?? _preferences.KeepAboveSolidWorks;
            AutomaticDrawingPreferencesStore.Save(_preferences);
        }

        private static Panel Card()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, 0, 8) };
        }

        private static Button MakeButton(string text, bool accent)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Accent : Surface,
                ForeColor = accent ? Color.White : TextColor,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = accent ? Accent : Border;
            button.FlatAppearance.BorderSize = 1;
            return button;
        }
    }
}
