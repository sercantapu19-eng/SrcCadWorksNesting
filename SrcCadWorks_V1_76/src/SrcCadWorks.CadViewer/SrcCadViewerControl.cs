using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using SolidWorks.Interop.sldworks;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;
using Wpf3D = System.Windows.Media.Media3D;

namespace SrcCadWorks.CadViewer
{
    public sealed class SrcCadViewerControl : UserControl
    {
        private enum DisplayMode { Shaded, ShadedEdges, Wireframe }

        private static readonly Color Accent = Color.FromArgb(202, 28, 36);
        private static readonly Color Border = Color.FromArgb(205, 211, 218);
        private static readonly Color TextColor = Color.FromArgb(31, 36, 42);
        private static readonly Color Muted = Color.FromArgb(94, 103, 113);
        private static readonly Color ViewerBackground = Color.FromArgb(243, 245, 248);

        private readonly SldWorks _swApp;
        private readonly SolidWorksSceneLoader _sceneLoader;
        private readonly DrawingBitmapLoader _drawingLoader;
        private readonly TableLayoutPanel _root;
        private readonly FlowLayoutPanel _toolbar;
        private readonly Button _backButton;
        private readonly Button _partButton;
        private readonly Button _drawingButton;
        private readonly Button _measureButton;
        private readonly Button _fitButton;
        private readonly Label _status;
        private readonly Panel _hostPanel;
        private readonly ElementHost _elementHost;
        private readonly DrawingCanvas _drawingCanvas;

        private readonly WpfControls.Grid _wpfRoot;
        private readonly WpfControls.Viewport3D _viewport;
        private readonly Wpf3D.Model3DGroup _modelGroup;
        private readonly Wpf3D.OrthographicCamera _camera;
        private readonly EdgeOverlay _edgeOverlay;
        private readonly Dictionary<Wpf3D.GeometryModel3D, CadSceneNode> _modelToNode = new Dictionary<Wpf3D.GeometryModel3D, CadSceneNode>();
        private readonly Dictionary<CadSceneNode, List<VisualEdge>> _visualEdges =
            new Dictionary<CadSceneNode, List<VisualEdge>>();

        private CadScene _scene;
        private CadSceneNode _selectedNode;
        private readonly List<Wpf3D.Point3D> _selectedFaceTriangles = new List<Wpf3D.Point3D>();
        private Wpf3D.Vector3D _selectedNormal;
        private Wpf3D.Point3D _selectedPoint;
        private bool _hasSelectedSurface;
        private bool _measureMode;
        private MeasurementPick _measureFirst;
        private DisplayMode _displayMode = DisplayMode.Shaded;
        private string _sourcePath = "";
        private string _drawingPath = "";
        private string _configuration = "";
        private bool _showing3D = true;
        private bool _expanded;
        private bool _shutdown;

        private Wpf.Point _lastMouse;
        private Wpf.Point _pointerDownMouse;
        private bool _pointerDown;
        private bool _pointerDragged;
        private WpfInput.MouseButton _activePointerButton = WpfInput.MouseButton.Left;
        private bool _rotating;
        private Wpf3D.Point3D _target = new Wpf3D.Point3D(0, 0, 0);
        private double _cameraDistance = 2.0;

        public event EventHandler PreviewExpandRequested;
        public event EventHandler BackRequested;

        public SrcCadViewerControl() : this(null) { }

        public SrcCadViewerControl(SldWorks swApp)
        {
            _swApp = swApp;
            _sceneLoader = new SolidWorksSceneLoader(swApp);
            _drawingLoader = new DrawingBitmapLoader(swApp);

            Dock = DockStyle.Fill;
            BackColor = ViewerBackground;
            Font = new Font("Segoe UI", 9F);

            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = ViewerBackground
            };
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            _toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.White,
                Margin = Padding.Empty,
                Padding = new Padding(4, 5, 4, 3)
            };
            _backButton = MakeButton("← GERİ", 78);
            _partButton = MakeButton("3D", 64);
            _drawingButton = MakeButton("TEKNİK", 84);
            _measureButton = MakeButton("ÖLÇÜM", 84);
            _fitButton = MakeButton("SIĞDIR", 78);
            _backButton.Visible = false;
            _toolbar.Controls.Add(_backButton);
            _toolbar.Controls.Add(_partButton);
            _toolbar.Controls.Add(_drawingButton);
            _toolbar.Controls.Add(_measureButton);
            _toolbar.Controls.Add(_fitButton);
            _toolbar.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Muted,
                Text = "SrcCadViewer • sol/orta sürükle: döndür • tekerlek: imleçten zoom",
                Margin = new Padding(12, 7, 0, 0)
            });

            _backButton.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
            _partButton.Click += (s, e) => { Show3D(); PreviewExpandRequested?.Invoke(this, EventArgs.Empty); };
            _drawingButton.Click += (s, e) => { ShowDrawing(); PreviewExpandRequested?.Invoke(this, EventArgs.Empty); };
            _measureButton.Click += (s, e) => ToggleMeasure();
            _fitButton.Click += (s, e) => FitToWindow();

            _hostPanel = new Panel { Dock = DockStyle.Fill, BackColor = ViewerBackground, BorderStyle = BorderStyle.FixedSingle };
            _drawingCanvas = new DrawingCanvas { Dock = DockStyle.Fill, Visible = false };
            _hostPanel.Controls.Add(_drawingCanvas);

            _modelGroup = new Wpf3D.Model3DGroup();
            _modelGroup.Children.Add(new Wpf3D.AmbientLight(WpfMedia.Color.FromRgb(152, 152, 152)));
            _modelGroup.Children.Add(new Wpf3D.DirectionalLight(WpfMedia.Color.FromRgb(205, 205, 205), new Wpf3D.Vector3D(-0.35, -0.45, -1)));
            _modelGroup.Children.Add(new Wpf3D.DirectionalLight(WpfMedia.Color.FromRgb(92, 92, 92), new Wpf3D.Vector3D(0.45, 0.25, 1)));

            _camera = new Wpf3D.OrthographicCamera
            {
                Position = new Wpf3D.Point3D(1.5, -1.5, 1.1),
                LookDirection = new Wpf3D.Vector3D(-1.5, 1.5, -1.1),
                UpDirection = new Wpf3D.Vector3D(0, 0, 1),
                Width = 1.0,
                NearPlaneDistance = 0.00001,
                FarPlaneDistance = 100000
            };
            _viewport = new WpfControls.Viewport3D
            {
                Camera = _camera,
                ClipToBounds = true,
                Focusable = true
            };
            _viewport.Children.Add(new Wpf3D.ModelVisual3D { Content = _modelGroup });
            _edgeOverlay = new EdgeOverlay { IsHitTestVisible = false };
            _wpfRoot = new WpfControls.Grid
            {
                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(243, 245, 248)),
                Focusable = true
            };
            _wpfRoot.Children.Add(_viewport);
            _wpfRoot.Children.Add(_edgeOverlay);
            _wpfRoot.Children.Add(new WpfControls.TextBlock
            {
                Text = "SrcCadViewer",
                HorizontalAlignment = Wpf.HorizontalAlignment.Right,
                VerticalAlignment = Wpf.VerticalAlignment.Top,
                Margin = new Wpf.Thickness(0, 8, 12, 0),
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(69, 75, 82)),
                FontFamily = new WpfMedia.FontFamily("Segoe UI Semibold"),
                FontSize = 13,
                IsHitTestVisible = false
            });
            _elementHost = new ElementHost { Dock = DockStyle.Fill, Child = _wpfRoot, BackColor = ViewerBackground };
            _hostPanel.Controls.Add(_elementHost);
            _elementHost.BringToFront();

            // Preview events make drag handling stable even when the pointer crosses
            // a 3D child visual. Lost capture is also handled explicitly.
            _wpfRoot.PreviewMouseLeftButtonDown += ViewportMouseLeftDown;
            _wpfRoot.PreviewMouseLeftButtonUp += ViewportMouseLeftUp;
            _wpfRoot.PreviewMouseMove += ViewportMouseMove;
            _wpfRoot.PreviewMouseDown += ViewportMouseDown;
            _wpfRoot.PreviewMouseUp += ViewportMouseUp;
            _wpfRoot.PreviewMouseWheel += ViewportMouseWheel;
            _wpfRoot.LostMouseCapture += (s, e) =>
            {
                _rotating = false;
                _pointerDown = false;
                _pointerDragged = false;
            };
            _wpfRoot.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == WpfInput.Key.Escape)
                {
                    CancelViewerInteraction();
                    e.Handled = true;
                }
            };
            _viewport.SizeChanged += (s, e) => _edgeOverlay.InvalidateVisual();
            BuildContextMenu();

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Muted,
                BackColor = ViewerBackground,
                Padding = new Padding(8, 0, 8, 0),
                AutoEllipsis = true,
                Text = "SrcCadViewer hazır"
            };

            _root.Controls.Add(_toolbar, 0, 0);
            _root.Controls.Add(_hostPanel, 0, 1);
            _root.Controls.Add(_status, 0, 2);
            Controls.Add(_root);
            SetActiveButton(_partButton, true);
        }

        private static Button MakeButton(string text, int width)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = TextColor,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Border;
            return b;
        }

        public void LoadRow(object row)
        {
            if (row == null)
            {
                CloseDocument();
                _status.Text = "Önizleme için bir parça seçin.";
                return;
            }
            try
            {
                dynamic r = row;
                string source = Convert.ToString(r.SourcePath) ?? "";
                string drawing = Convert.ToString(r.DrawingPath) ?? "";
                string cfg = Convert.ToString(r.Configuration) ?? "";
                LoadPaths(source, drawing, cfg);
            }
            catch (Exception ex)
            {
                _status.Text = "SrcCadViewer satır bilgisi okunamadı: " + ex.Message;
            }
        }

        public void LoadPath(string path) { LoadPaths(path, "", ""); }
        public void LoadPaths(string sourcePath, string drawingPath) { LoadPaths(sourcePath, drawingPath, ""); }

        public void LoadPaths(string sourcePath, string drawingPath, string configuration)
        {
            if (_shutdown) return;
            _sourcePath = File.Exists(sourcePath ?? "") ? sourcePath : "";
            _drawingPath = File.Exists(drawingPath ?? "") ? drawingPath : "";
            _configuration = configuration ?? "";
            _drawingButton.Enabled = !string.IsNullOrWhiteSpace(_drawingPath);
            _partButton.Enabled = !string.IsNullOrWhiteSpace(_sourcePath);
            if (!string.IsNullOrWhiteSpace(_sourcePath)) Show3D();
            else if (!string.IsNullOrWhiteSpace(_drawingPath)) ShowDrawing();
            else CloseDocument();
        }

        public void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            _backButton.Visible = expanded;
            if (!expanded) ExitMeasureMode();
        }

        private void Show3D()
        {
            if (string.IsNullOrWhiteSpace(_sourcePath) || !File.Exists(_sourcePath))
            {
                _status.Text = "3D dosyası bulunamadı.";
                return;
            }
            try
            {
                ExitMeasureMode();
                _drawingCanvas.Visible = false;
                _elementHost.Visible = true;
                _elementHost.BringToFront();
                _status.Text = "SrcCadViewer 3D hazırlanıyor: " + Path.GetFileName(_sourcePath);
                Application.DoEvents();
                _scene = _sceneLoader.Load(_sourcePath, _configuration, text => { _status.Text = text; Application.DoEvents(); });
                BuildSceneModels();
                _showing3D = true;
                SetActiveButton(_partButton, true);
                SetActiveButton(_drawingButton, false);
                FitToWindow();
                _status.Text = "3D • " + Path.GetFileName(_sourcePath) + " • Gölgeli • sol/orta sürükle döndür • tekerlek: imleçten zoom";
            }
            catch (Exception ex)
            {
                _scene = null;
                RebuildVisualScene();
                _status.Text = "SrcCadViewer 3D açılamadı: " + ex.Message;
            }
        }

        private void ShowDrawing()
        {
            if (string.IsNullOrWhiteSpace(_drawingPath) || !File.Exists(_drawingPath))
            {
                _status.Text = "Teknik resim bulunamadı.";
                return;
            }
            try
            {
                ExitMeasureMode();
                _status.Text = "Teknik resim hazırlanıyor: " + Path.GetFileName(_drawingPath);
                Application.DoEvents();
                Image img = _drawingLoader.Load(_drawingPath, Math.Max(900, _hostPanel.Width), Math.Max(600, _hostPanel.Height));
                if (img == null) throw new InvalidOperationException("Teknik resim görüntüsü alınamadı.");
                _drawingCanvas.SetImage(img);
                _elementHost.Visible = false;
                _drawingCanvas.Visible = true;
                _drawingCanvas.BringToFront();
                _showing3D = false;
                SetActiveButton(_partButton, false);
                SetActiveButton(_drawingButton, true);
                _status.Text = "TEKNİK • " + Path.GetFileName(_drawingPath) + " • sol/orta sürükle kaydır • tekerlek: imleçten zoom";
                BeginInvoke(new Action(() =>
                {
                    try { _drawingCanvas.Fit(); } catch { }
                }));
            }
            catch (Exception ex)
            {
                _status.Text = "Teknik resim açılamadı: " + ex.Message;
            }
        }

        public void FitToWindow()
        {
            if (!_showing3D)
            {
                _drawingCanvas.Fit();
                return;
            }
            if (_scene == null) return;
            Wpf3D.Rect3D b = _scene.Bounds;
            if (b.IsEmpty) return;

            _target = new Wpf3D.Point3D(
                b.X + b.SizeX / 2,
                b.Y + b.SizeY / 2,
                b.Z + b.SizeZ / 2);

            double diag = Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ);
            if (diag < 1e-9) diag = 1.0;
            _cameraDistance = diag * 3.0;

            SetOrientation(new Wpf3D.Vector3D(-1, 1, -0.72), new Wpf3D.Vector3D(0, 0, 1), true);
            FitBoundsToCurrentCamera(b, 1.16);
            UpdateCameraPosition();
        }

        private void FitBoundsToCurrentCamera(Wpf3D.Rect3D b, double margin)
        {
            if (b.IsEmpty) return;

            Wpf3D.Vector3D look = _camera.LookDirection;
            if (look.LengthSquared < 1e-20) look = new Wpf3D.Vector3D(-1, 1, -0.72);
            look.Normalize();

            Wpf3D.Vector3D up = _camera.UpDirection;
            if (up.LengthSquared < 1e-20) up = new Wpf3D.Vector3D(0, 0, 1);
            up.Normalize();

            Wpf3D.Vector3D right = Wpf3D.Vector3D.CrossProduct(look, up);
            if (right.LengthSquared < 1e-20) right = new Wpf3D.Vector3D(1, 0, 0);
            right.Normalize();
            up = Wpf3D.Vector3D.CrossProduct(right, look);
            up.Normalize();

            double maxX = 0, maxY = 0;
            foreach (var c in BoundsCorners(b))
            {
                Wpf3D.Vector3D v = c - _target;
                maxX = Math.Max(maxX, Math.Abs(Wpf3D.Vector3D.DotProduct(v, right)));
                maxY = Math.Max(maxY, Math.Abs(Wpf3D.Vector3D.DotProduct(v, up)));
            }

            double aspect = (_viewport.ActualWidth > 2 && _viewport.ActualHeight > 2)
                ? _viewport.ActualWidth / _viewport.ActualHeight
                : 1.6;
            double requiredWidth = Math.Max(maxX * 2.0, maxY * 2.0 * aspect);
            if (requiredWidth < 1e-9) requiredWidth = 1.0;
            _camera.Width = requiredWidth * Math.Max(1.02, margin);
        }

        private static IEnumerable<Wpf3D.Point3D> BoundsCorners(Wpf3D.Rect3D b)
        {
            double x0 = b.X, x1 = b.X + b.SizeX;
            double y0 = b.Y, y1 = b.Y + b.SizeY;
            double z0 = b.Z, z1 = b.Z + b.SizeZ;
            yield return new Wpf3D.Point3D(x0, y0, z0);
            yield return new Wpf3D.Point3D(x1, y0, z0);
            yield return new Wpf3D.Point3D(x0, y1, z0);
            yield return new Wpf3D.Point3D(x1, y1, z0);
            yield return new Wpf3D.Point3D(x0, y0, z1);
            yield return new Wpf3D.Point3D(x1, y0, z1);
            yield return new Wpf3D.Point3D(x0, y1, z1);
            yield return new Wpf3D.Point3D(x1, y1, z1);
        }

        private void BuildSceneModels()
        {
            _modelToNode.Clear();
            _visualEdges.Clear();
            _selectedNode = null;
            _selectedFaceTriangles.Clear();
            _hasSelectedSurface = false;
            if (_scene == null) { RebuildVisualScene(); return; }
            foreach (var node in _scene.Nodes)
            {
                if (node == null || node.Mesh == null) continue;
                node.Model = new Wpf3D.GeometryModel3D { Geometry = node.Mesh, BackMaterial = null };
                ApplyNodeMaterial(node, false);
                _modelToNode[node.Model] = node;
                _visualEdges[node] = BuildVisualEdges(node.Mesh);
            }
            RebuildVisualScene();
        }

        private void RebuildVisualScene()
        {
            while (_modelGroup.Children.Count > 3) _modelGroup.Children.RemoveAt(_modelGroup.Children.Count - 1);
            if (_scene != null)
            {
                foreach (var node in _scene.Nodes)
                {
                    if (node == null || node.Hidden || node.Model == null) continue;
                    ApplyNodeMaterial(node, false);
                    _modelGroup.Children.Add(node.Model);
                }
            }
            _edgeOverlay.Scene = _scene;
            _edgeOverlay.Camera = _camera;
            _edgeOverlay.Mode = _displayMode;
            _edgeOverlay.Edges = _visualEdges;
            _edgeOverlay.SelectedFaceTriangles = _selectedFaceTriangles;
            _edgeOverlay.InvalidateVisual();
        }

        private void ApplyNodeMaterial(CadSceneNode node, bool selected)
        {
            if (node == null || node.Model == null) return;
            WpfMedia.Color baseColor = WpfMedia.Color.FromRgb(node.ColorR, node.ColorG, node.ColorB);
            if (_displayMode == DisplayMode.Wireframe)
            {
                // Geometry remains present for hit-testing but faces are visually transparent.
                var transparent = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0, 0, 0, 0));
                transparent.Freeze();
                var wireHitMaterial = new Wpf3D.DiffuseMaterial(transparent);
                node.Model.Material = wireHitMaterial;
                node.Model.BackMaterial = wireHitMaterial;
                return;
            }

            var brush = new WpfMedia.SolidColorBrush(
                WpfMedia.Color.FromArgb(255, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();

            // Matte CAD material: eDrawings-like shaded appearance without
            // the glossy white highlights that made curved rollers look metallic.
            var material = new Wpf3D.DiffuseMaterial(brush);
            node.Model.Material = material;
            node.Model.BackMaterial = material;
        }

        private void BuildContextMenu()
        {
            var menu = new WpfControls.ContextMenu();
            var select = new WpfControls.MenuItem { Header = "Seç" };
            select.Click += (s, e) =>
            {
                _status.Text = "SEÇ • 3D model üzerinde bir parçaya/yüzeye sol tıklayın.";
            };
            menu.Items.Add(select);

            var state = new WpfControls.MenuItem { Header = "Görüntü Durumu" };
            state.Items.Add(Menu("Ön", () => StandardView("front")));
            state.Items.Add(Menu("Arka", () => StandardView("back")));
            state.Items.Add(Menu("Sağ", () => StandardView("right")));
            state.Items.Add(Menu("Sol", () => StandardView("left")));
            state.Items.Add(Menu("Üst", () => StandardView("top")));
            state.Items.Add(Menu("Alt", () => StandardView("bottom")));
            state.Items.Add(new WpfControls.Separator());
            state.Items.Add(Menu("İzometrik", () => StandardView("iso")));
            menu.Items.Add(state);

            var appearance = new WpfControls.MenuItem { Header = "Görünüm" };
            appearance.Items.Add(Menu("Gölgeli", () => SetDisplayMode(DisplayMode.Shaded)));
            appearance.Items.Add(Menu("Gölgeli + Kenarlar", () => SetDisplayMode(DisplayMode.ShadedEdges)));
            appearance.Items.Add(Menu("Tel Kafes", () => SetDisplayMode(DisplayMode.Wireframe)));
            menu.Items.Add(appearance);

            menu.Items.Add(Menu("Yüzeye Dik Bak", NormalToSelectedSurface));
            menu.Items.Add(Menu("Ölçüm", ToggleMeasure));

            var separator = new WpfControls.Separator();
            var hide = Menu("Seçileni Gizle", HideSelected);
            var isolate = Menu("Seçileni İzole Et", IsolateSelected);
            var showAll = Menu("Tümünü Göster", ShowAllNodes);
            menu.Items.Add(separator); menu.Items.Add(hide); menu.Items.Add(isolate); menu.Items.Add(showAll);
            menu.Opened += (s, e) =>
            {
                bool selected = _selectedNode != null;
                bool hidden = _scene != null && _scene.Nodes.Any(n => n.Hidden);
                separator.Visibility = (selected || hidden) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
                hide.Visibility = selected ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
                isolate.Visibility = selected ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
                showAll.Visibility = hidden ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            };
            _wpfRoot.ContextMenu = menu;
        }

        private static WpfControls.MenuItem Menu(string text, Action action)
        {
            var item = new WpfControls.MenuItem { Header = text };
            item.Click += (s, e) => action();
            return item;
        }

        private void StandardView(string name)
        {
            if (_scene == null) return;
            switch (name)
            {
                case "front": SetOrientation(new Wpf3D.Vector3D(0, 0, -1), new Wpf3D.Vector3D(0, 1, 0), true); break;
                case "back": SetOrientation(new Wpf3D.Vector3D(0, 0, 1), new Wpf3D.Vector3D(0, 1, 0), true); break;
                case "right": SetOrientation(new Wpf3D.Vector3D(-1, 0, 0), new Wpf3D.Vector3D(0, 0, 1), true); break;
                case "left": SetOrientation(new Wpf3D.Vector3D(1, 0, 0), new Wpf3D.Vector3D(0, 0, 1), true); break;
                case "top": SetOrientation(new Wpf3D.Vector3D(0, 1, 0), new Wpf3D.Vector3D(0, 0, 1), true); break;
                case "bottom": SetOrientation(new Wpf3D.Vector3D(0, -1, 0), new Wpf3D.Vector3D(0, 0, -1), true); break;
                default: SetOrientation(new Wpf3D.Vector3D(-1, 1, -0.72), new Wpf3D.Vector3D(0, 0, 1), true); break;
            }
        }

        private void SetOrientation(Wpf3D.Vector3D look, Wpf3D.Vector3D up, bool keepZoom)
        {
            if (look.LengthSquared < 1e-20) return;
            look.Normalize();
            if (up.LengthSquared < 1e-20) up = new Wpf3D.Vector3D(0, 0, 1);
            up.Normalize();
            if (Math.Abs(Wpf3D.Vector3D.DotProduct(look, up)) > 0.98)
                up = Math.Abs(look.Z) < 0.8 ? new Wpf3D.Vector3D(0, 0, 1) : new Wpf3D.Vector3D(0, 1, 0);
            Wpf3D.Vector3D right = Wpf3D.Vector3D.CrossProduct(look, up);
            right.Normalize();
            up = Wpf3D.Vector3D.CrossProduct(right, look);
            up.Normalize();
            _camera.LookDirection = look * _cameraDistance;
            _camera.UpDirection = up;
            UpdateCameraPosition();
        }

        private void UpdateCameraPosition()
        {
            Wpf3D.Vector3D look = _camera.LookDirection;
            if (look.LengthSquared < 1e-20) look = new Wpf3D.Vector3D(-1, 1, -0.7);
            look.Normalize();
            _camera.LookDirection = look * _cameraDistance;
            _camera.Position = _target - look * _cameraDistance;
            _edgeOverlay.InvalidateVisual();
        }

        private void SetDisplayMode(DisplayMode mode)
        {
            _displayMode = mode;
            RebuildVisualScene();
            _status.Text = mode == DisplayMode.Shaded ? "GÖRÜNÜM • Gölgeli" : mode == DisplayMode.ShadedEdges ? "GÖRÜNÜM • Gölgeli + Kenarlar" : "GÖRÜNÜM • Tel Kafes";
        }

        private void ViewportMouseLeftDown(object sender, WpfInput.MouseButtonEventArgs e)
        {
            BeginOrbitPointer(e, WpfInput.MouseButton.Left);
        }

        private void ViewportMouseLeftUp(object sender, WpfInput.MouseButtonEventArgs e)
        {
            EndOrbitPointer(e, WpfInput.MouseButton.Left);
        }

        private void ViewportMouseDown(object sender, WpfInput.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != WpfInput.MouseButton.Middle) return;
            BeginOrbitPointer(e, WpfInput.MouseButton.Middle);
        }

        private void ViewportMouseUp(object sender, WpfInput.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != WpfInput.MouseButton.Middle) return;
            EndOrbitPointer(e, WpfInput.MouseButton.Middle);
        }

        private void BeginOrbitPointer(WpfInput.MouseButtonEventArgs e, WpfInput.MouseButton button)
        {
            try { _wpfRoot.Focus(); } catch { }
            _lastMouse = e.GetPosition(_wpfRoot);
            _pointerDownMouse = _lastMouse;
            _pointerDown = true;
            _pointerDragged = false;
            _activePointerButton = button;
            _rotating = false;
            WpfInput.Mouse.Capture(_wpfRoot, WpfInput.CaptureMode.Element);
            e.Handled = true;
        }

        private void EndOrbitPointer(WpfInput.MouseButtonEventArgs e, WpfInput.MouseButton button)
        {
            if (!_pointerDown || _activePointerButton != button) return;

            Wpf.Point release = e.GetPosition(_wpfRoot);
            bool wasClick = !_pointerDragged;

            _pointerDown = false;
            _pointerDragged = false;
            _rotating = false;
            WpfInput.Mouse.Capture(null);

            if (wasClick)
            {
                if (_measureMode)
                    HandleMeasurePick(release);
                else
                    SelectAt(release);
            }

            e.Handled = true;
        }

        private void ViewportMouseMove(object sender, WpfInput.MouseEventArgs e)
        {
            if (_pointerDown)
            {
                bool stillPressed = _activePointerButton == WpfInput.MouseButton.Middle
                    ? e.MiddleButton == WpfInput.MouseButtonState.Pressed
                    : e.LeftButton == WpfInput.MouseButtonState.Pressed;

                if (!stillPressed)
                {
                    _pointerDown = false;
                    _pointerDragged = false;
                    _rotating = false;
                    try { WpfInput.Mouse.Capture(null); } catch { }
                }
            }

            Wpf.Point p = e.GetPosition(_wpfRoot);

            if (_pointerDown && !_pointerDragged)
            {
                double totalDx = p.X - _pointerDownMouse.X;
                double totalDy = p.Y - _pointerDownMouse.Y;
                if ((totalDx * totalDx + totalDy * totalDy) >= 16.0)
                {
                    _pointerDragged = true;
                    _rotating = true;
                }
            }

            double dx = p.X - _lastMouse.X;
            double dy = p.Y - _lastMouse.Y;
            _lastMouse = p;

            if (_rotating)
            {
                RotateCamera(dx, dy);
                e.Handled = true;
            }
        }

        private void ViewportMouseWheel(object sender, WpfInput.MouseWheelEventArgs e)
        {
            // eDrawings-like cursor anchored orthographic zoom.
            double oldWidth = Math.Max(1e-9, _camera.Width);
            double factor = e.Delta > 0 ? 1.16 : 0.86;
            double newWidth = Math.Max(1e-6, oldWidth * factor);

            double vw = _viewport.ActualWidth;
            double vh = _viewport.ActualHeight;
            if (vw > 2 && vh > 2)
            {
                Wpf.Point p = e.GetPosition(_wpfRoot);
                double oldHeight = oldWidth * vh / vw;
                double newHeight = newWidth * vh / vw;

                Wpf3D.Vector3D look = _camera.LookDirection;
                if (look.LengthSquared > 1e-20) look.Normalize();
                Wpf3D.Vector3D up = _camera.UpDirection;
                if (up.LengthSquared > 1e-20) up.Normalize();
                Wpf3D.Vector3D right = Wpf3D.Vector3D.CrossProduct(look, up);
                if (right.LengthSquared > 1e-20) right.Normalize();

                double nx = (p.X - vw * 0.5) / vw;
                double ny = (p.Y - vh * 0.5) / vh;

                _target += right * (nx * (oldWidth - newWidth))
                    + up * (-ny * (oldHeight - newHeight));
            }

            _camera.Width = newWidth;
            UpdateCameraPosition();
            _edgeOverlay.InvalidateVisual();
            e.Handled = true;
        }

        private void RotateCamera(double dx, double dy)
        {
            Wpf3D.Vector3D look = _camera.LookDirection; look.Normalize();
            Wpf3D.Vector3D up = _camera.UpDirection; up.Normalize();
            Wpf3D.Vector3D right = Wpf3D.Vector3D.CrossProduct(look, up); right.Normalize();
            look = Rotate(look, up, -dx * 0.007);
            right = Wpf3D.Vector3D.CrossProduct(look, up); if (right.LengthSquared > 1e-20) right.Normalize();
            look = Rotate(look, right, -dy * 0.007);
            up = Rotate(up, right, -dy * 0.007);
            SetOrientation(look, up, true);
        }

        private static Wpf3D.Vector3D Rotate(Wpf3D.Vector3D v, Wpf3D.Vector3D axis, double angle)
        {
            if (axis.LengthSquared < 1e-20) return v;
            axis.Normalize();
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c + Wpf3D.Vector3D.CrossProduct(axis, v) * s + axis * Wpf3D.Vector3D.DotProduct(axis, v) * (1 - c);
        }

        private Wpf3D.RayMeshGeometry3DHitTestResult HitTest(Wpf.Point point)
        {
            Wpf3D.RayMeshGeometry3DHitTestResult hit = null;
            WpfMedia.VisualTreeHelper.HitTest(_viewport, null,
                result =>
                {
                    var ray = result as Wpf3D.RayMeshGeometry3DHitTestResult;
                    if (ray != null && ray.ModelHit is Wpf3D.GeometryModel3D)
                    {
                        hit = ray;
                        return WpfMedia.HitTestResultBehavior.Stop;
                    }
                    return WpfMedia.HitTestResultBehavior.Continue;
                }, new WpfMedia.PointHitTestParameters(point));
            return hit;
        }

        private void SelectAt(Wpf.Point point)
        {
            var hit = HitTest(point);
            if (hit == null) { _status.Text = "Seçim bulunamadı."; return; }
            CadSceneNode node;
            if (!_modelToNode.TryGetValue(hit.ModelHit as Wpf3D.GeometryModel3D, out node)) return;
            _selectedNode = node;
            _selectedPoint = hit.PointHit;
            _selectedNormal = TriangleNormal(hit);
            _selectedFaceTriangles.Clear();
            foreach (var p in BuildSurfacePatch(hit)) _selectedFaceTriangles.Add(p);
            _hasSelectedSurface = true;
            RebuildVisualScene();
            _status.Text = "YÜZEY SEÇİLDİ • " + (node.Name ?? Path.GetFileNameWithoutExtension(node.SourcePath)) + " • sağ tık: gizle/izole/dik bak";
        }

        private Wpf3D.Vector3D TriangleNormal(Wpf3D.RayMeshGeometry3DHitTestResult hit)
        {
            try
            {
                var mesh = hit.MeshHit;
                Wpf3D.Point3D a = mesh.Positions[hit.VertexIndex1];
                Wpf3D.Point3D b = mesh.Positions[hit.VertexIndex2];
                Wpf3D.Point3D c = mesh.Positions[hit.VertexIndex3];
                Wpf3D.Vector3D n = Wpf3D.Vector3D.CrossProduct(b - a, c - a);
                if (n.LengthSquared > 1e-20) n.Normalize();
                return n;
            }
            catch { return new Wpf3D.Vector3D(0, 0, 1); }
        }

        private void NormalToSelectedSurface()
        {
            if (!_hasSelectedSurface)
            {
                _status.Text = "YÜZEYE DİK BAK • önce model üzerinde düz bir yüzeye sol tıklayın.";
                return;
            }
            Wpf3D.Vector3D n = _selectedNormal;
            if (n.LengthSquared < 1e-20) return;
            n.Normalize();
            Wpf3D.Vector3D current = _camera.LookDirection; current.Normalize();
            if (Wpf3D.Vector3D.DotProduct(current, n) > 0) n = -n;
            SetOrientation(n, Math.Abs(n.Z) < 0.92 ? new Wpf3D.Vector3D(0, 0, 1) : new Wpf3D.Vector3D(0, 1, 0), true);
            _target = _selectedPoint;
            UpdateCameraPosition();
            _status.Text = "YÜZEYE DİK BAK • seçilen yüzey normale alındı";
        }

        private void HideSelected()
        {
            if (_selectedNode == null) return;
            _selectedNode.Hidden = true;
            _selectedNode = null;
            _selectedFaceTriangles.Clear();
            _hasSelectedSurface = false;
            RebuildVisualScene();
            _status.Text = "Seçilen bileşen gizlendi.";
        }

        private void IsolateSelected()
        {
            if (_selectedNode == null || _scene == null) return;
            foreach (var n in _scene.Nodes) n.Hidden = !ReferenceEquals(n, _selectedNode);
            RebuildVisualScene();
            FitToWindow();
            _status.Text = "Seçilen bileşen izole edildi.";
        }

        private void ShowAllNodes()
        {
            if (_scene == null) return;
            foreach (var n in _scene.Nodes) n.Hidden = false;
            RebuildVisualScene();
            FitToWindow();
            _status.Text = "Tüm bileşenler gösteriliyor.";
        }

        private void ToggleMeasure()
        {
            if (!_showing3D)
            {
                _status.Text = "Ölçüm 3D görünümde kullanılabilir.";
                return;
            }
            _measureMode = !_measureMode;
            _measureFirst = null;
            _edgeOverlay.MeasureA = null;
            _edgeOverlay.MeasureB = null;
            _edgeOverlay.MeasureText = "";
            _edgeOverlay.InvalidateVisual();
            try { _viewport.Cursor = _measureMode ? WpfInput.Cursors.Cross : WpfInput.Cursors.Arrow; } catch { }
            _measureButton.Text = _measureMode ? "ÖLÇÜM ✓" : "ÖLÇÜM";
            SetActiveButton(_measureButton, _measureMode);
            _status.Text = _measureMode
                ? "ÖLÇÜM • iki nokta/yüzey seçin. Mesafe, paralel yüz mesafesi ve yüzey açısı hesaplanır."
                : "Ölçüm kapatıldı.";
        }

        private void ExitMeasureMode()
        {
            _measureMode = false;
            _measureFirst = null;
            _edgeOverlay.MeasureA = null;
            _edgeOverlay.MeasureB = null;
            _edgeOverlay.MeasureText = "";
            _edgeOverlay.InvalidateVisual();
            try { _viewport.Cursor = WpfInput.Cursors.Arrow; } catch { }
            _measureButton.Text = "ÖLÇÜM";
            SetActiveButton(_measureButton, false);
        }

        private void HandleMeasurePick(Wpf.Point point)
        {
            var hit = HitTest(point);
            if (hit == null) { _status.Text = "ÖLÇÜM • geometri seçilemedi."; return; }
            var pick = new MeasurementPick { Point = hit.PointHit, Normal = TriangleNormal(hit) };
            foreach (var p in BuildPlanarFacePatch(hit)) pick.FaceTriangles.Add(p);
            if (_measureFirst == null)
            {
                _measureFirst = pick;
                _edgeOverlay.MeasureA = pick;
                _edgeOverlay.MeasureB = null;
                _edgeOverlay.InvalidateVisual();
                _status.Text = "ÖLÇÜM • 1. seçim alındı. İkinci nokta/yüzeyi seçin.";
                return;
            }

            Wpf3D.Vector3D delta = pick.Point - _measureFirst.Point;
            double pointDistanceMm = delta.Length * 1000.0;
            double dot = Wpf3D.Vector3D.DotProduct(_measureFirst.Normal, pick.Normal);
            dot = Math.Max(-1, Math.Min(1, dot));
            double angle = Math.Acos(Math.Abs(dot)) * 180.0 / Math.PI;

            string bottomText;
            string overlayText;
            if (Math.Abs(dot) > 0.995)
            {
                Wpf3D.Vector3D n = _measureFirst.Normal;
                if (n.LengthSquared > 1e-20) n.Normalize();

                double signedPlaneDistance = Wpf3D.Vector3D.DotProduct(delta, n);
                double planeDistanceMm = Math.Abs(signedPlaneDistance) * 1000.0;

                // Draw the visible dimension perpendicular to the two parallel faces,
                // not merely between the arbitrary click locations.
                pick.Point = _measureFirst.Point + n * signedPlaneDistance;

                overlayText = planeDistanceMm.ToString("0.###") + " mm";
                bottomText = "ÖLÇÜM • Yüzeyler arası: " + overlayText +
                    " | Noktalar arası: " + pointDistanceMm.ToString("0.###") + " mm";
            }
            else
            {
                overlayText = angle.ToString("0.###") + "°";
                bottomText = "ÖLÇÜM • Mesafe: " + pointDistanceMm.ToString("0.###") +
                    " mm | Yüzey açısı: " + overlayText;
            }

            _status.Text = bottomText;
            _edgeOverlay.MeasureA = _measureFirst;
            _edgeOverlay.MeasureB = pick;
            _edgeOverlay.MeasureText = overlayText;
            _edgeOverlay.InvalidateVisual();
            _measureFirst = null;
        }

        private void CancelViewerInteraction()
        {
            _pointerDown = false;
            _pointerDragged = false;
            _rotating = false;
            try { WpfInput.Mouse.Capture(null); } catch { }

            _selectedNode = null;
            _selectedFaceTriangles.Clear();
            _hasSelectedSurface = false;
            _selectedPoint = new Wpf3D.Point3D();
            _selectedNormal = new Wpf3D.Vector3D();

            _measureMode = false;
            _measureFirst = null;
            _edgeOverlay.MeasureA = null;
            _edgeOverlay.MeasureB = null;
            _edgeOverlay.MeasureText = "";
            try { _viewport.Cursor = WpfInput.Cursors.Arrow; } catch { }
            _measureButton.Text = "ÖLÇÜM";
            SetActiveButton(_measureButton, false);

            RebuildVisualScene();
            _edgeOverlay.InvalidateVisual();
            _status.Text = "İşlem iptal edildi • seçim temizlendi";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape)
            {
                CancelViewerInteraction();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
            ExitMeasureMode();
            _scene = null;
            _selectedNode = null;
            _selectedFaceTriangles.Clear();
            _hasSelectedSurface = false;
            _sourcePath = "";
            _drawingPath = "";
            _drawingCanvas.ClearImage();
            RebuildVisualScene();
            _status.Text = "SrcCadViewer hazır";
        }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            CloseDocument();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Shutdown();
            base.Dispose(disposing);
        }

        private List<Wpf3D.Point3D> BuildSurfacePatch(Wpf3D.RayMeshGeometry3DHitTestResult hit)
        {
            var result = new List<Wpf3D.Point3D>();
            if (hit == null || hit.MeshHit == null) return result;

            var mesh = hit.MeshHit;
            int triCount = mesh.TriangleIndices.Count / 3;
            if (triCount <= 0) return result;

            int seed = -1;
            for (int t = 0; t < triCount; t++)
            {
                int o = t * 3;
                int i1 = mesh.TriangleIndices[o];
                int i2 = mesh.TriangleIndices[o + 1];
                int i3 = mesh.TriangleIndices[o + 2];
                if ((i1 == hit.VertexIndex1 || i1 == hit.VertexIndex2 || i1 == hit.VertexIndex3) &&
                    (i2 == hit.VertexIndex1 || i2 == hit.VertexIndex2 || i2 == hit.VertexIndex3) &&
                    (i3 == hit.VertexIndex1 || i3 == hit.VertexIndex2 || i3 == hit.VertexIndex3))
                {
                    seed = t;
                    break;
                }
            }
            if (seed < 0) return BuildPlanarFacePatch(hit);

            Wpf3D.Rect3D bounds = mesh.Bounds;
            double diag = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);
            double tol = Math.Max(1e-9, Math.Max(1.0, diag) * 1e-7);

            var normals = new Wpf3D.Vector3D[triCount];
            var adjacency = new List<int>[triCount];
            for (int i = 0; i < triCount; i++) adjacency[i] = new List<int>();
            var edgeOwners = new Dictionary<EdgeKey, int>();

            for (int t = 0; t < triCount; t++)
            {
                int o = t * 3;
                Wpf3D.Point3D a = mesh.Positions[mesh.TriangleIndices[o]];
                Wpf3D.Point3D b = mesh.Positions[mesh.TriangleIndices[o + 1]];
                Wpf3D.Point3D c = mesh.Positions[mesh.TriangleIndices[o + 2]];
                Wpf3D.Vector3D n = Wpf3D.Vector3D.CrossProduct(b - a, c - a);
                if (n.LengthSquared > 1e-20) n.Normalize();
                normals[t] = n;

                AddTriangleAdjacency(edgeOwners, adjacency, new EdgeKey(new VertexKey(a, tol), new VertexKey(b, tol)), t);
                AddTriangleAdjacency(edgeOwners, adjacency, new EdgeKey(new VertexKey(b, tol), new VertexKey(c, tol)), t);
                AddTriangleAdjacency(edgeOwners, adjacency, new EdgeKey(new VertexKey(c, tol), new VertexKey(a, tol)), t);
            }

            const double smoothCos = 0.9063077870366499; // cos(25°)
            var visited = new bool[triCount];
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            visited[seed] = true;
            int accepted = 0;
            const int maxTriangles = 24000;

            while (queue.Count > 0 && accepted < maxTriangles)
            {
                int t = queue.Dequeue();
                int o = t * 3;
                result.Add(mesh.Positions[mesh.TriangleIndices[o]]);
                result.Add(mesh.Positions[mesh.TriangleIndices[o + 1]]);
                result.Add(mesh.Positions[mesh.TriangleIndices[o + 2]]);
                accepted++;

                foreach (int next in adjacency[t])
                {
                    if (next < 0 || next >= triCount || visited[next]) continue;
                    double dot = Wpf3D.Vector3D.DotProduct(normals[t], normals[next]);
                    if (dot < smoothCos) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            return result.Count >= 3 ? result : BuildPlanarFacePatch(hit);
        }

        private static void AddTriangleAdjacency(Dictionary<EdgeKey, int> owners,
            List<int>[] adjacency, EdgeKey key, int triangle)
        {
            int other;
            if (!owners.TryGetValue(key, out other))
            {
                owners[key] = triangle;
                return;
            }
            if (other == triangle) return;
            if (!adjacency[triangle].Contains(other)) adjacency[triangle].Add(other);
            if (!adjacency[other].Contains(triangle)) adjacency[other].Add(triangle);
        }

        private List<Wpf3D.Point3D> BuildPlanarFacePatch(Wpf3D.RayMeshGeometry3DHitTestResult hit)
        {
            var result = new List<Wpf3D.Point3D>();
            if (hit == null || hit.MeshHit == null) return result;

            var mesh = hit.MeshHit;
            Wpf3D.Vector3D selectedNormal = TriangleNormal(hit);
            if (selectedNormal.LengthSquared < 1e-20) return result;
            selectedNormal.Normalize();

            Wpf3D.Point3D planePoint = hit.PointHit;
            Wpf3D.Rect3D bounds = mesh.Bounds;
            double diag = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);
            double planeTolerance = Math.Max(1e-8, diag * 2e-5);
            const double normalCos = 0.9994; // ~2 degrees

            int triangleLimit = 12000;
            int accepted = 0;
            for (int i = 0; i + 2 < mesh.TriangleIndices.Count && accepted < triangleLimit; i += 3)
            {
                Wpf3D.Point3D a = mesh.Positions[mesh.TriangleIndices[i]];
                Wpf3D.Point3D b = mesh.Positions[mesh.TriangleIndices[i + 1]];
                Wpf3D.Point3D c = mesh.Positions[mesh.TriangleIndices[i + 2]];

                Wpf3D.Vector3D n = Wpf3D.Vector3D.CrossProduct(b - a, c - a);
                if (n.LengthSquared < 1e-20) continue;
                n.Normalize();

                if (Math.Abs(Wpf3D.Vector3D.DotProduct(n, selectedNormal)) < normalCos) continue;

                double da = Math.Abs(Wpf3D.Vector3D.DotProduct(a - planePoint, selectedNormal));
                double db = Math.Abs(Wpf3D.Vector3D.DotProduct(b - planePoint, selectedNormal));
                double dc = Math.Abs(Wpf3D.Vector3D.DotProduct(c - planePoint, selectedNormal));
                if (da > planeTolerance || db > planeTolerance || dc > planeTolerance) continue;

                result.Add(a); result.Add(b); result.Add(c);
                accepted++;
            }

            return result;
        }

        private List<VisualEdge> BuildVisualEdges(Wpf3D.MeshGeometry3D mesh)
        {
            var result = new List<VisualEdge>();
            if (mesh == null || mesh.Positions == null || mesh.TriangleIndices == null) return result;

            Wpf3D.Rect3D bounds = mesh.Bounds;
            double diag = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);
            double tol = Math.Max(1e-9, Math.Max(1.0, diag) * 1e-7);

            var map = new Dictionary<EdgeKey, EdgeAccumulator>();
            for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
            {
                int ia = mesh.TriangleIndices[i];
                int ib = mesh.TriangleIndices[i + 1];
                int ic = mesh.TriangleIndices[i + 2];
                if (ia < 0 || ib < 0 || ic < 0 ||
                    ia >= mesh.Positions.Count || ib >= mesh.Positions.Count || ic >= mesh.Positions.Count)
                    continue;

                Wpf3D.Point3D a = mesh.Positions[ia];
                Wpf3D.Point3D b = mesh.Positions[ib];
                Wpf3D.Point3D c = mesh.Positions[ic];

                Wpf3D.Vector3D n = Wpf3D.Vector3D.CrossProduct(b - a, c - a);
                if (n.LengthSquared < 1e-20) continue;
                n.Normalize();

                AddEdge(map, a, b, n, tol);
                AddEdge(map, b, c, n, tol);
                AddEdge(map, c, a, n, tol);
            }

            const double sharpCos = 0.9510565162951535; // cos(18°)
            foreach (var acc in map.Values)
            {
                bool sharp = acc.HasSecond &&
                    Math.Abs(Wpf3D.Vector3D.DotProduct(acc.N1, acc.N2)) < sharpCos;

                result.Add(new VisualEdge
                {
                    A = acc.A,
                    B = acc.B,
                    N1 = acc.N1,
                    N2 = acc.N2,
                    HasSecond = acc.HasSecond,
                    Sharp = sharp
                });
            }
            return result;
        }

        private static void AddEdge(Dictionary<EdgeKey, EdgeAccumulator> map,
            Wpf3D.Point3D a, Wpf3D.Point3D b, Wpf3D.Vector3D normal, double tol)
        {
            var ka = new VertexKey(a, tol);
            var kb = new VertexKey(b, tol);
            var key = new EdgeKey(ka, kb);

            EdgeAccumulator acc;
            if (!map.TryGetValue(key, out acc))
            {
                acc = new EdgeAccumulator { A = a, B = b, N1 = normal };
                map[key] = acc;
                return;
            }

            if (!acc.HasSecond)
            {
                acc.N2 = normal;
                acc.HasSecond = true;
            }
        }

        private struct VertexKey : IEquatable<VertexKey>, IComparable<VertexKey>
        {
            public readonly long X, Y, Z;
            public VertexKey(Wpf3D.Point3D p, double tol)
            {
                X = (long)Math.Round(p.X / tol);
                Y = (long)Math.Round(p.Y / tol);
                Z = (long)Math.Round(p.Z / tol);
            }
            public int CompareTo(VertexKey other)
            {
                int c = X.CompareTo(other.X); if (c != 0) return c;
                c = Y.CompareTo(other.Y); if (c != 0) return c;
                return Z.CompareTo(other.Z);
            }
            public bool Equals(VertexKey other) { return X == other.X && Y == other.Y && Z == other.Z; }
            public override bool Equals(object obj) { return obj is VertexKey && Equals((VertexKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = X.GetHashCode();
                    h = (h * 397) ^ Y.GetHashCode();
                    h = (h * 397) ^ Z.GetHashCode();
                    return h;
                }
            }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly VertexKey A, B;
            public EdgeKey(VertexKey a, VertexKey b)
            {
                if (a.CompareTo(b) <= 0) { A = a; B = b; }
                else { A = b; B = a; }
            }
            public bool Equals(EdgeKey other) { return A.Equals(other.A) && B.Equals(other.B); }
            public override bool Equals(object obj) { return obj is EdgeKey && Equals((EdgeKey)obj); }
            public override int GetHashCode()
            {
                unchecked { return (A.GetHashCode() * 397) ^ B.GetHashCode(); }
            }
        }

        private sealed class EdgeAccumulator
        {
            public Wpf3D.Point3D A, B;
            public Wpf3D.Vector3D N1, N2;
            public bool HasSecond;
        }

        private sealed class VisualEdge
        {
            public Wpf3D.Point3D A, B;
            public Wpf3D.Vector3D N1, N2;
            public bool HasSecond;
            public bool Sharp;
        }

        private sealed class MeasurementPick
        {
            public Wpf3D.Point3D Point;
            public Wpf3D.Vector3D Normal;
            public readonly List<Wpf3D.Point3D> FaceTriangles = new List<Wpf3D.Point3D>();
        }

        private sealed class EdgeOverlay : Wpf.FrameworkElement
        {
            public CadScene Scene;
            public Wpf3D.OrthographicCamera Camera;
            public DisplayMode Mode;
            public List<Wpf3D.Point3D> SelectedFaceTriangles;
            public Dictionary<CadSceneNode, List<VisualEdge>> Edges;
            public MeasurementPick MeasureA;
            public MeasurementPick MeasureB;
            public string MeasureText;

            protected override void OnRender(WpfMedia.DrawingContext dc)
            {
                base.OnRender(dc);
                if (Camera == null) return;

                double width = ActualWidth, height = ActualHeight;
                if (width < 2 || height < 2) return;

                Wpf3D.Vector3D look = Camera.LookDirection;
                if (look.LengthSquared < 1e-20) return;
                look.Normalize();

                Wpf3D.Vector3D up = Camera.UpDirection;
                if (up.LengthSquared < 1e-20) up = new Wpf3D.Vector3D(0, 0, 1);
                up.Normalize();

                Wpf3D.Vector3D right = Wpf3D.Vector3D.CrossProduct(look, up);
                if (right.LengthSquared < 1e-20) return;
                right.Normalize();
                up = Wpf3D.Vector3D.CrossProduct(right, look);
                up.Normalize();

                double viewW = Math.Max(1e-9, Camera.Width);
                double viewH = viewW * height / width;

                if (Mode != DisplayMode.Shaded && Scene != null && Edges != null)
                {
                    var edgeColor = Mode == DisplayMode.Wireframe
                        ? WpfMedia.Color.FromRgb(54, 58, 64)
                        : WpfMedia.Color.FromRgb(40, 43, 48);
                    var normalPen = new WpfMedia.Pen(new WpfMedia.SolidColorBrush(edgeColor),
                        Mode == DisplayMode.Wireframe ? 0.95 : 0.72);
                    normalPen.Freeze();


                    foreach (var node in Scene.Nodes)
                    {
                        if (node == null || node.Hidden) continue;
                        List<VisualEdge> list;
                        if (!Edges.TryGetValue(node, out list) || list == null) continue;

                        var pen = normalPen;
                        foreach (var edge in list)
                        {
                            bool boundary = !edge.HasSecond;
                            double d1 = Wpf3D.Vector3D.DotProduct(edge.N1, look);
                            double d2 = edge.HasSecond
                                ? Wpf3D.Vector3D.DotProduct(edge.N2, look)
                                : d1;

                            // In this camera convention a negative dot means the face normal
                            // points toward the camera. Do not paint edges belonging only to
                            // back-facing faces; this removes the "see through / wire curtain"
                            // effect in Shaded + Edges.
                            bool face1Visible = d1 < -0.015;
                            bool face2Visible = edge.HasSecond && d2 < -0.015;
                            bool silhouette = edge.HasSecond &&
                                ((d1 <= 0 && d2 > 0) || (d2 <= 0 && d1 > 0));

                            bool draw;
                            if (Mode == DisplayMode.Wireframe)
                            {
                                draw = boundary || edge.Sharp || silhouette;
                            }
                            else
                            {
                                draw = (boundary && face1Visible)
                                    || (edge.Sharp && (face1Visible || face2Visible))
                                    || silhouette;
                            }

                            if (!draw) continue;

                            Wpf.Point a = Project(edge.A, Camera.Position, right, up, viewW, viewH, width, height);
                            Wpf.Point b = Project(edge.B, Camera.Position, right, up, viewW, viewH, width, height);
                            dc.DrawLine(pen, a, b);
                        }
                    }
                }

                if (SelectedFaceTriangles != null && SelectedFaceTriangles.Count >= 3)
                {
                    DrawFacePatch(dc, SelectedFaceTriangles,
                        WpfMedia.Color.FromArgb(84, 202, 28, 36),
                        Camera.Position, right, up, viewW, viewH, width, height);
                }

                DrawMeasurement(dc, right, up, viewW, viewH, width, height);
            }

            private void DrawMeasurement(WpfMedia.DrawingContext dc, Wpf3D.Vector3D right,
                Wpf3D.Vector3D up, double viewW, double viewH, double width, double height)
            {
                if (MeasureA == null) return;

                DrawFacePatch(dc, MeasureA.FaceTriangles,
                    WpfMedia.Color.FromArgb(62, 202, 28, 36),
                    Camera.Position, right, up, viewW, viewH, width, height);

                if (MeasureB != null)
                {
                    DrawFacePatch(dc, MeasureB.FaceTriangles,
                        WpfMedia.Color.FromArgb(62, 36, 112, 202),
                        Camera.Position, right, up, viewW, viewH, width, height);
                }

                var markerBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(202, 28, 36));
                markerBrush.Freeze();
                var measurePen = new WpfMedia.Pen(markerBrush, 1.8);
                measurePen.Freeze();

                Wpf.Point a = Project(MeasureA.Point, Camera.Position, right, up, viewW, viewH, width, height);
                dc.DrawEllipse(markerBrush, null, a, 4.5, 4.5);

                if (MeasureB == null) return;

                Wpf.Point b = Project(MeasureB.Point, Camera.Position, right, up, viewW, viewH, width, height);
                dc.DrawEllipse(markerBrush, null, b, 4.5, 4.5);
                dc.DrawLine(measurePen, a, b);

                // Small perpendicular end ticks make it read like a CAD dimension.
                Wpf.Vector d = b - a;
                if (d.Length > 0.001)
                {
                    d.Normalize();
                    Wpf.Vector n = new Wpf.Vector(-d.Y, d.X) * 7.0;
                    dc.DrawLine(measurePen, a - n, a + n);
                    dc.DrawLine(measurePen, b - n, b + n);
                }

                if (!string.IsNullOrWhiteSpace(MeasureText))
                {
                    Wpf.Point mid = new Wpf.Point((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                    var ft = new WpfMedia.FormattedText(
                        MeasureText,
                        System.Globalization.CultureInfo.CurrentCulture,
                        Wpf.FlowDirection.LeftToRight,
                        new WpfMedia.Typeface("Segoe UI Semibold"),
                        13,
                        WpfMedia.Brushes.White,
                        1.0);

                    var box = new Wpf.Rect(
                        mid.X - ft.Width * 0.5 - 6,
                        mid.Y - ft.Height * 0.5 - 3,
                        ft.Width + 12,
                        ft.Height + 6);
                    dc.DrawRoundedRectangle(
                        new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(220, 45, 48, 52)),
                        null, box, 4, 4);
                    dc.DrawText(ft, new Wpf.Point(mid.X - ft.Width * 0.5, mid.Y - ft.Height * 0.5));
                }
            }

            private static void DrawFacePatch(WpfMedia.DrawingContext dc,
                List<Wpf3D.Point3D> triangles, WpfMedia.Color color,
                Wpf3D.Point3D cameraPos, Wpf3D.Vector3D right, Wpf3D.Vector3D up,
                double viewW, double viewH, double width, double height)
            {
                if (triangles == null || triangles.Count < 3) return;
                var fill = new WpfMedia.SolidColorBrush(color);
                fill.Freeze();

                Wpf3D.Vector3D look = -Wpf3D.Vector3D.CrossProduct(right, up);
                if (look.LengthSquared > 1e-20) look.Normalize();

                int max = Math.Min(triangles.Count, 36000);
                for (int i = 0; i + 2 < max; i += 3)
                {
                    Wpf3D.Point3D p0 = triangles[i];
                    Wpf3D.Point3D p1 = triangles[i + 1];
                    Wpf3D.Point3D p2 = triangles[i + 2];
                    Wpf3D.Vector3D normal = Wpf3D.Vector3D.CrossProduct(p1 - p0, p2 - p0);
                    if (normal.LengthSquared > 1e-20)
                    {
                        normal.Normalize();
                        if (Wpf3D.Vector3D.DotProduct(normal, look) >= -0.002) continue;
                    }

                    Wpf.Point a = Project(p0, cameraPos, right, up, viewW, viewH, width, height);
                    Wpf.Point b = Project(p1, cameraPos, right, up, viewW, viewH, width, height);
                    Wpf.Point c = Project(p2, cameraPos, right, up, viewW, viewH, width, height);

                    var geo = new WpfMedia.StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(a, true, true);
                        ctx.LineTo(b, true, false);
                        ctx.LineTo(c, true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(fill, null, geo);
                }
            }

            private static Wpf.Point Project(Wpf3D.Point3D p, Wpf3D.Point3D cameraPos,
                Wpf3D.Vector3D right, Wpf3D.Vector3D up,
                double viewW, double viewH, double width, double height)
            {
                Wpf3D.Vector3D v = p - cameraPos;
                double x = Wpf3D.Vector3D.DotProduct(v, right);
                double y = Wpf3D.Vector3D.DotProduct(v, up);
                return new Wpf.Point(width * 0.5 + x / viewW * width,
                    height * 0.5 - y / viewH * height);
            }
        }

        private sealed class DrawingCanvas : Control
        {
            private Image _image;
            private double _scale = 1.0;
            private PointF _offset;
            private Point _last;
            private bool _dragging;
            private MouseButtons _dragButton = MouseButtons.None;
            private bool _autoFit = true;

            public DrawingCanvas()
            {
                DoubleBuffered = true;
                BackColor = Color.FromArgb(226, 230, 235);
                TabStop = true;

                MouseWheel += (s, e) => ZoomAt(e.Location, e.Delta);

                MouseDown += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Middle) return;
                    Focus();
                    _dragging = true;
                    _dragButton = e.Button;
                    _last = e.Location;
                    Capture = true;
                };

                MouseUp += (s, e) =>
                {
                    if (!_dragging || e.Button != _dragButton) return;
                    _dragging = false;
                    _dragButton = MouseButtons.None;
                    Capture = false;
                };

                MouseMove += (s, e) =>
                {
                    if (!_dragging) return;
                    bool pressed = _dragButton == MouseButtons.Middle
                        ? (e.Button & MouseButtons.Middle) == MouseButtons.Middle
                        : (e.Button & MouseButtons.Left) == MouseButtons.Left;

                    if (!pressed)
                    {
                        _dragging = false;
                        _dragButton = MouseButtons.None;
                        Capture = false;
                        return;
                    }

                    _autoFit = false;
                    _offset.X += e.X - _last.X;
                    _offset.Y += e.Y - _last.Y;
                    _last = e.Location;
                    Invalidate();
                };
            }

            public void SetImage(Image image)
            {
                ClearImage();
                _image = image;
                _autoFit = true;
                Fit();
            }

            public void ClearImage()
            {
                var old = _image;
                _image = null;
                try { old?.Dispose(); } catch { }
                Invalidate();
            }

            public void Fit()
            {
                if (_image == null || ClientSize.Width <= 4 || ClientSize.Height <= 4) return;

                // eDrawings-like "zoom to fit": use almost all available drawing area.
                double margin = 10.0;
                double sx = Math.Max(0.01, (ClientSize.Width - margin * 2) / _image.Width);
                double sy = Math.Max(0.01, (ClientSize.Height - margin * 2) / _image.Height);
                _scale = Math.Min(sx, sy);

                _offset = new PointF(
                    (float)((ClientSize.Width - _image.Width * _scale) * 0.5),
                    (float)((ClientSize.Height - _image.Height * _scale) * 0.5));

                _autoFit = true;
                Invalidate();
            }

            private void ZoomAt(Point cursor, int delta)
            {
                if (_image == null || _scale <= 0) return;

                double oldScale = _scale;
                // Keep the same direction as SrcCadViewer 3D.
                double factor = delta > 0 ? 1 / 1.15 : 1.15;
                double newScale = Math.Max(0.02, Math.Min(60.0, oldScale * factor));

                double imageX = (cursor.X - _offset.X) / oldScale;
                double imageY = (cursor.Y - _offset.Y) / oldScale;

                _scale = newScale;
                _offset.X = (float)(cursor.X - imageX * newScale);
                _offset.Y = (float)(cursor.Y - imageY * newScale);

                _autoFit = false;
                Invalidate();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (_autoFit && _image != null)
                    Fit();
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (!Capture)
                {
                    _dragging = false;
                    _dragButton = MouseButtons.None;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_image == null) return;

                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                float w = (float)(_image.Width * _scale);
                float h = (float)(_image.Height * _scale);

                using (var shadow = new SolidBrush(Color.FromArgb(42, 0, 0, 0)))
                    e.Graphics.FillRectangle(shadow, _offset.X + 5, _offset.Y + 5, w, h);
                using (var paper = new SolidBrush(Color.White))
                    e.Graphics.FillRectangle(paper, _offset.X, _offset.Y, w, h);

                e.Graphics.DrawImage(_image, _offset.X, _offset.Y, w, h);

                using (var border = new Pen(Color.FromArgb(185, 190, 196), 1F))
                    e.Graphics.DrawRectangle(border, _offset.X, _offset.Y, w, h);

                using (var font = new Font("Segoe UI Semibold", 10F))
                using (var brush = new SolidBrush(Color.FromArgb(74, 80, 88)))
                {
                    const string brand = "SrcCadViewer";
                    SizeF size = e.Graphics.MeasureString(brand, font);
                    e.Graphics.DrawString(
                        brand, font, brush,
                        Math.Max(6, ClientSize.Width - size.Width - 12),
                        8);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) ClearImage();
                base.Dispose(disposing);
            }
        }
    }
}
