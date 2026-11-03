using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SrcCadWorks.Services;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.UI
{
    internal sealed class TaskPaneControl : UserControl
    {
        private readonly SldWorks _swApp;
        private readonly Action _openAssemblyViewer;
        private readonly Action _openAssemblyPrepare;
        private readonly Action _openAutomaticDrawing;
        private readonly Action _openLaserPrepare;
        private readonly Action _openWeldPrepare;
        private readonly Action _openManufacturingPrepare;
        private readonly Action _openMachiningPrepare;
        private readonly Action _openSmartNest;
        private readonly Timer _documentTimer;

        private Label _documentTitle;
        private Label _stateText;
        private Panel _stateDot;
        private Button _viewerButton;
        private Button _assemblyPrepareButton;
        private Button _automaticDrawingButton;
        private Button _laserButton;
        private Button _weldButton;
        private Button _manufacturingButton;
        private Button _machiningButton;
        private Button _smartNestButton;
        private string _lastDocumentKey = "";

        private static readonly Color CBackground = Color.FromArgb(244, 246, 249);
        private static readonly Color CSurface = Color.White;
        private static readonly Color CSurfaceAlt = Color.FromArgb(249, 250, 252);
        private static readonly Color CBorder = Color.FromArgb(205, 211, 218);
        private static readonly Color CText = Color.FromArgb(31, 36, 42);
        private static readonly Color CMuted = Color.FromArgb(94, 103, 113);
        private static readonly Color CAccent = Color.FromArgb(202, 28, 36);
        private static readonly Color CSuccess = Color.FromArgb(27, 143, 89);
        private static readonly Color CDisabled = Color.FromArgb(170, 177, 185);

        public TaskPaneControl(SldWorks swApp, Action openAssemblyViewer, Action openAssemblyPrepare, Action openAutomaticDrawing, Action openLaserPrepare, Action openWeldPrepare, Action openManufacturingPrepare, Action openMachiningPrepare, Action openSmartNest)
        {
            _swApp = swApp;
            _openAssemblyViewer = openAssemblyViewer;
            _openAssemblyPrepare = openAssemblyPrepare;
            _openAutomaticDrawing = openAutomaticDrawing;
            _openLaserPrepare = openLaserPrepare;
            _openWeldPrepare = openWeldPrepare;
            _openManufacturingPrepare = openManufacturingPrepare;
            _openMachiningPrepare = openMachiningPrepare;
            _openSmartNest = openSmartNest;
            Dock = DockStyle.Fill;
            BackColor = CBackground;
            ForeColor = CText;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(250, 500);

            BuildUi();
            ApplyTextRenderingQuality(this);
            RefreshDocumentStatus();

            _documentTimer = new Timer { Interval = 1200 };
            _documentTimer.Tick += (s, e) => RefreshDocumentStatus(false);
            _documentTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _documentTimer != null)
            {
                try { _documentTimer.Stop(); } catch { }
                try { _documentTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void BuildUi()
        {
            SuspendLayout();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = CBackground,
                Padding = new Padding(12, 10, 12, 10),
                AutoScroll = true
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 488));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = CBackground }, 0, 1);
            root.Controls.Add(BuildDocumentCard(), 0, 2);

            var modulePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 9,
                BackColor = CBackground,
                Margin = new Padding(0),
                Padding = new Padding(0, 4, 0, 4)
            };
            modulePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            for (int i = 0; i < 8; i++) modulePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            modulePanel.Controls.Add(new Label
            {
                Text = "MODÜLLER",
                Dock = DockStyle.Fill,
                ForeColor = CMuted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            _viewerButton = MakeButton("MONTAJ GÖRÜNTÜLE", true);
            _viewerButton.Image = CadDocumentIcons.Assembly(22); _viewerButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _viewerButton.Disposed += (sender,args) => _viewerButton.Image?.Dispose();
            _viewerButton.Margin = new Padding(0, 2, 0, 3);
            _viewerButton.Click += (s, e) => OpenViewer();
            modulePanel.Controls.Add(_viewerButton, 0, 1);
            _assemblyPrepareButton = MakeButton("MONTAJ DOSYASI HAZIRLA", true);
            _assemblyPrepareButton.Image = CadDocumentIcons.Assembly(22); _assemblyPrepareButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _assemblyPrepareButton.Disposed += (sender,args) => _assemblyPrepareButton.Image?.Dispose();
            _assemblyPrepareButton.Margin = new Padding(0, 2, 0, 3);
            _assemblyPrepareButton.Click += (s, e) => OpenAssemblyPrepare();
            modulePanel.Controls.Add(_assemblyPrepareButton, 0, 2);
            _automaticDrawingButton = MakeButton("OTOMATİK TEKNİK RESİM", true);
            _automaticDrawingButton.Image = CadDocumentIcons.Drawing(22); _automaticDrawingButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _automaticDrawingButton.Disposed += (sender,args) => _automaticDrawingButton.Image?.Dispose();
            _automaticDrawingButton.Margin = new Padding(0, 2, 0, 3);
            _automaticDrawingButton.Click += (s, e) => OpenAutomaticDrawing();
            modulePanel.Controls.Add(_automaticDrawingButton, 0, 3);
            _laserButton = MakeButton("LAZER DOSYASI HAZIRLA", true);
            _laserButton.Image = CadDocumentIcons.Part(22); _laserButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _laserButton.Disposed += (sender,args) => _laserButton.Image?.Dispose();
            _laserButton.Margin = new Padding(0, 2, 0, 3);
            _laserButton.Click += (s, e) => OpenLaser();
            modulePanel.Controls.Add(_laserButton, 0, 4);
            _weldButton = MakeButton("KAYNAK DOSYASI HAZIRLA", true);
            _weldButton.Image = CadDocumentIcons.Assembly(22); _weldButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _weldButton.Disposed += (sender,args) => _weldButton.Image?.Dispose();
            _weldButton.Margin = new Padding(0, 2, 0, 0);
            _weldButton.Click += (s, e) => OpenWeld();
            modulePanel.Controls.Add(_weldButton, 0, 5);
            _manufacturingButton = MakeButton("İMALAT DOSYASI HAZIRLA", true);
            _manufacturingButton.Image = CadDocumentIcons.Part(22); _manufacturingButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _manufacturingButton.Disposed += (sender,args) => _manufacturingButton.Image?.Dispose();
            _manufacturingButton.Margin = new Padding(0, 2, 0, 3);
            _manufacturingButton.Click += (s, e) => OpenManufacturing();
            modulePanel.Controls.Add(_manufacturingButton, 0, 6);
            _machiningButton = MakeButton("TALAŞLI İMALAT DOSYASI HAZIRLA", true);
            _machiningButton.Image = CadDocumentIcons.Part(22); _machiningButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _machiningButton.Disposed += (sender,args) => _machiningButton.Image?.Dispose();
            _machiningButton.Margin = new Padding(0, 2, 0, 0);
            _machiningButton.Click += (s, e) => OpenMachining();
            modulePanel.Controls.Add(_machiningButton, 0, 7);
            _smartNestButton = MakeButton("SMARTNEST – SAC YERLEŞİMİ", true);
            _smartNestButton.Image = CadDocumentIcons.Part(22); _smartNestButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _smartNestButton.Margin = new Padding(0, 2, 0, 0);
            _smartNestButton.Click += (s, e) => _openSmartNest?.Invoke();
            modulePanel.Controls.Add(_smartNestButton, 0, 8);
            root.Controls.Add(modulePanel, 0, 3);

            root.Controls.Add(BuildGuidanceCard(), 0, 4);

            var version = new Label
            {
                Text = "SrcCadWorks  •  " + AppVersion.Display,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(110, 118, 128),
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 8F)
            };
            root.Controls.Add(version, 0, 5);

            ResumeLayout(true);
        }

        private Control BuildHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(10, 8, 10, 8)
            };

            var logo = new PictureBox
            {
                Dock = DockStyle.Left,
                Width = 76,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                Margin = new Padding(0)
            };
            try
            {
                string logoPath = FindTransparentLogo();
                if (File.Exists(logoPath)) logo.Image = Image.FromFile(logoPath);
            }
            catch { }

            var textPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10, 14, 0, 0) };
            var title = new Label
            {
                Text = "SrcCadWorks",
                Dock = DockStyle.Top,
                Height = 27,
                ForeColor = CText,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            textPanel.Controls.Add(title);

            var accentLine = new Panel { Dock = DockStyle.Bottom, Height = 2, BackColor = CAccent };
            header.Controls.Add(textPanel);
            header.Controls.Add(logo);
            header.Controls.Add(accentLine);
            return header;
        }

        private Control BuildDocumentCard()
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CSurface,
                Padding = new Padding(12, 10, 12, 8)
            };

            var caption = new Label
            {
                Text = "AKTİF BELGE",
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = CMuted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold)
            };

            _documentTitle = new Label
            {
                Text = "Belge açık değil",
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = CText,
                Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var statusPanel = new Panel { Dock = DockStyle.Fill, BackColor = CSurface };
            _stateDot = new Panel { Width = 8, Height = 8, Left = 1, Top = 10, BackColor = CDisabled };
            _stateText = new Label
            {
                Text = "Montaj bekleniyor",
                Left = 16,
                Top = 3,
                Width = 140,
                Height = 24,
                ForeColor = CMuted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusPanel.Controls.Add(_stateDot);
            statusPanel.Controls.Add(_stateText);

            card.Controls.Add(statusPanel);
            card.Controls.Add(_documentTitle);
            card.Controls.Add(caption);
            return card;
        }

        private Control BuildQuickActions()
        {
            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = CBackground,
                Padding = new Padding(0, 6, 0, 0)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var refresh = MakeButton("YENİLE", false);
            refresh.Margin = new Padding(0, 0, 4, 0);
            refresh.Click += (s, e) => RefreshDocumentStatus(true);
            actions.Controls.Add(refresh, 0, 0);

            var laser = MakeButton("AÇ", false);
            laser.Margin = new Padding(4, 0, 0, 0);
            laser.Click += (s, e) => OpenLaser();
            actions.Controls.Add(laser, 1, 0);
            return actions;
        }

        private Control BuildGuidanceCard()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = CBackground, Padding = new Padding(2, 12, 2, 4) };
            var title = new Label
            {
                Text = "GÜVENLİ ÇALIŞMA",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = CMuted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold)
            };
            var info = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(105, 113, 122),
                Text = "SrcCadWorks'in kendi açtığı kaynak belgeler sessiz ve salt-okunur işlenir. İşlem sonunda kaydedilmeden kapatılır. Kullanıcının açık belgesine otomatik kaydetme/kapatma uygulanmaz.",
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            panel.Controls.Add(info);
            panel.Controls.Add(title);
            return panel;
        }

        private Button MakeButton(string text, bool accent)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? CAccent : CSurface,
                ForeColor = accent ? Color.White : CText,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = accent ? CAccent : CBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(176, 22, 30) : Color.FromArgb(236, 239, 243);
            return button;
        }

        private void OpenViewer()
        {
            if (!RefreshDocumentStatus(true)) return;
            _openAssemblyViewer?.Invoke();
            RefreshDocumentStatus(true);
        }

        private void OpenAutomaticDrawing()
        {
            if (!IsActivePartReady())
            {
                RefreshDocumentStatus(true);
                return;
            }
            _openAutomaticDrawing?.Invoke();
            RefreshDocumentStatus(true);
        }

        private bool IsActivePartReady()
        {
            try
            {
                var doc = _swApp.ActiveDoc as ModelDoc2;
                return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART;
            }
            catch { return false; }
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

        private void OpenAssemblyPrepare()
        {
            if (!RefreshDocumentStatus(true)) return;
            _openAssemblyPrepare?.Invoke();
            RefreshDocumentStatus(true);
        }

        private void OpenWeld()
        {
            if (!RefreshDocumentStatus(true)) return;
            _openWeldPrepare?.Invoke();
            RefreshDocumentStatus(true);
        }

        private void OpenManufacturing()
        {
            if (!RefreshDocumentStatus(true)) return;
            _openManufacturingPrepare?.Invoke();
            RefreshDocumentStatus(true);
        }

        private void OpenMachining()
        {
            if (!RefreshDocumentStatus(true)) return;
            _openMachiningPrepare?.Invoke();
            RefreshDocumentStatus(true);
        }

        private void OpenLaser()
        {
            if (!RefreshDocumentStatus(true)) return;
            _openLaserPrepare?.Invoke();
            RefreshDocumentStatus(true);
        }

        public void RefreshDocumentStatus()
        {
            RefreshDocumentStatus(true);
        }

        private bool RefreshDocumentStatus(bool force)
        {
            try
            {
                var doc = _swApp.ActiveDoc as ModelDoc2;
                if (doc == null)
                {
                    UpdateDocumentUi("", "Belge açık değil", false, false, "Montaj veya parça bekleniyor", force);
                    return false;
                }

                int type = doc.GetType();
                string title = doc.GetTitle() ?? "(adsız)";
                string key = type + "|" + title;
                bool assemblyReady = type == (int)swDocumentTypes_e.swDocASSEMBLY;
                bool partReady = type == (int)swDocumentTypes_e.swDocPART;
                string state = assemblyReady ? "SrcCadWorks hazır"
                    : partReady ? "Teknik resim için hazır"
                    : "Montaj veya SLDPRT açın";
                UpdateDocumentUi(key, title, assemblyReady, partReady, state, force);
                return assemblyReady;
            }
            catch
            {
                UpdateDocumentUi("error", "Belge durumu okunamadı", false, false, "Yeniden deneyin", true);
                return false;
            }
        }

        private void UpdateDocumentUi(string key, string title, bool assemblyReady, bool partReady, string state, bool force)
        {
            if (!force && string.Equals(_lastDocumentKey, key, StringComparison.Ordinal)) return;
            _lastDocumentKey = key;

            bool anyReady = assemblyReady || partReady;
            _documentTitle.Text = title;
            _stateText.Text = state;
            _stateText.ForeColor = anyReady ? CSuccess : CMuted;
            _stateDot.BackColor = anyReady ? CSuccess : CDisabled;

            SetButtonReady(_viewerButton, assemblyReady);
            SetButtonReady(_assemblyPrepareButton, assemblyReady);
            SetButtonReady(_laserButton, assemblyReady);
            SetButtonReady(_weldButton, assemblyReady);
            SetButtonReady(_manufacturingButton, assemblyReady);
            SetButtonReady(_machiningButton, assemblyReady);
            SetButtonReady(_automaticDrawingButton, partReady);
        }

        private static void SetButtonReady(Button button, bool ready)
        {
            if (button == null) return;
            button.Enabled = ready;
            button.BackColor = ready ? CAccent : Color.FromArgb(228, 231, 235);
            button.ForeColor = ready ? Color.White : Color.FromArgb(145, 151, 158);
        }

        private static string FindTransparentLogo()
        {
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string small = Path.Combine(baseDir, "assets", "SrcCadWorks_Logo_Small.png");
            if (File.Exists(small)) return small;
            string localSmall = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "assets", "SrcCadWorks_Logo_Small.png");
            if (File.Exists(localSmall)) return localSmall;
            return Path.Combine(baseDir, "assets", "SrcCadWorks_Logo_Transparent.png");
        }
    }
}
