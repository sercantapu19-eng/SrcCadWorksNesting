using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    /// <summary>SMARTNEST is a standalone SrcCadWorks module. It reuses the production flat-pattern exporter.</summary>
    internal sealed class SmartNestForm : Form
    {
        private sealed class AssemblyItem { public LaserPartRow Row; public int Quantity; }
        private readonly SldWorks _sw;
        private readonly DataGridView _grid = new DataGridView();
        private readonly NumericUpDown _width = new NumericUpDown(), _height = new NumericUpDown(), _gap = new NumericUpDown(), _margin = new NumericUpDown(), _kerf = new NumericUpDown();
        private readonly Label _status = new Label();
        private readonly ProgressBar _nestProgress = new ProgressBar();
        private CheckBox _keepOnTop;
        private SolidWorksOwnedWindow.Tracker _ownedWindowTracker;
        private readonly List<SmartNestPart> _parts = new List<SmartNestPart>();
        private readonly List<AssemblyItem> _assemblyItems = new List<AssemblyItem>();
        private SmartNestResult _result;
        private string _sourceDxfDirectory;
        public SmartNestForm(SldWorks sw) { _sw = sw; SolidWorksOwnedWindow.Initialize(_sw); Text = "SMARTNEST | SrcCadWorks"; Width = 1120; Height = 760; MinimumSize = new Size(900, 600); StartPosition = FormStartPosition.CenterScreen; Build(); _ownedWindowTracker = SolidWorksOwnedWindow.Track(this, _sw, _keepOnTop.Checked); FormClosed += (s,e) => { try { _ownedWindowTracker?.Dispose(); } catch { } }; }
        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12), BackColor = Color.FromArgb(244,246,249) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); Controls.Add(root);
            root.Controls.Add(new Label { Text = "SMARTNEST  |  Sac levha tarama ve otomatik yerleşim", Font = new Font("Segoe UI",17,FontStyle.Bold), ForeColor = Color.FromArgb(40,48,58), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft },0,0);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Color.White, Padding = new Padding(8) };
            AddButton(bar,"Montajı Tara + Açınımları Al",ScanAndExportAssembly); AddButton(bar,"DXF Ekle",AddDxf); _keepOnTop = new CheckBox { Text = "Pencereyi üstte tut", AutoSize = true, Checked = true, Margin = new Padding(8, 10, 4, 0) }; _keepOnTop.CheckedChanged += (s,e) => { SolidWorksOwnedWindow.Apply(this, _sw, _keepOnTop.Checked, true); _ownedWindowTracker?.SetEnabled(_keepOnTop.Checked); }; bar.Controls.Add(_keepOnTop);
            AddNumeric(bar,"Sac genişliği",_width,1000,1,10000); AddNumeric(bar,"Sac yüksekliği",_height,2000,1,10000); AddNumeric(bar,"Parça aralığı",_gap,5,0,1000); AddNumeric(bar,"Kenar payı",_margin,5,0,1000); AddNumeric(bar,"Kerf",_kerf,0,0,100);
            AddButton(bar,"NESTING YAP",RunNest); AddButton(bar,"Önizleme",ShowPreview); AddButton(bar,"Yerleşim DXF Kaydet",SaveDxf); root.Controls.Add(bar,0,1);
            _grid.Dock=DockStyle.Fill; _grid.BackgroundColor=Color.White; _grid.AllowUserToAddRows=false; _grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill; _grid.RowHeadersVisible=false;
            _grid.Columns.Add("No","Sıra No"); _grid.Columns.Add("Name","Parça / DXF"); _grid.Columns.Add("Qty","Adet"); _grid.Columns.Add("Material","Malzeme / Kalınlık"); _grid.Columns.Add("State","Durum"); root.Controls.Add(_grid,0,2);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            _status.Dock=DockStyle.Fill; _status.Text="Hazır. Yalnız SOLIDWORKS sac levha parçaları taranır; standart bağlantı elemanları elenir."; _status.TextAlign=ContentAlignment.MiddleLeft; _status.ForeColor=Color.FromArgb(70,80,90);
            _nestProgress.Dock=DockStyle.Fill; _nestProgress.Minimum=0; _nestProgress.Maximum=100; _nestProgress.Value=0; _nestProgress.Style=ProgressBarStyle.Continuous;
            footer.Controls.Add(_status,0,0); footer.Controls.Add(_nestProgress,1,0); root.Controls.Add(footer,0,3);
        }
        private static void AddButton(Control parent,string text,Action action){var b=new Button{Text=text,AutoSize=true,Height=34,Margin=new Padding(4),BackColor=Color.White};b.Click+=(s,e)=>action();parent.Controls.Add(b);}
        private static void AddNumeric(Control parent,string label,NumericUpDown n,decimal value,decimal min,decimal max){var p=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,Width=110,Margin=new Padding(4,0,4,0)};p.Controls.Add(new Label{Text=label,AutoSize=true});n.Minimum=min;n.Maximum=max;n.Value=value;n.DecimalPlaces=2;n.Width=100;p.Controls.Add(n);parent.Controls.Add(p);}

        private void ScanAndExportAssembly()
        {
            if (!string.IsNullOrWhiteSpace(_sourceDxfDirectory))
            {
                try { if (Directory.Exists(_sourceDxfDirectory)) Directory.Delete(_sourceDxfDirectory, true); } catch { }
            }
            string temp = Path.Combine(Path.GetTempPath(), "SrcCadWorks", "SMARTNEST_" + Guid.NewGuid().ToString("N"));
            _sourceDxfDirectory = temp;
            try
            {
                var doc = _sw.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    MessageBox.Show("Önce SOLIDWORKS'te bir montaj açın.");
                    return;
                }

                _parts.Clear();
                _assemblyItems.Clear();
                _grid.Rows.Clear();
                Directory.CreateDirectory(temp);

                // Kritik: SMARTNEST, Lazer Hazırla ile aynı montaj hiyerarşisi ve parça
                // sınıflandırma hattını kullanır. GetComponents(false) + IsSuppressed()
                // ile özel tarama yapılmaz; AssemblyScanner hafif/suppressed durumunu,
                // alt montajları, SLDPRT filtresini, dosya+konfigürasyon miktarını yönetir.
                ScanResult scan;
                using (var dialogGuard = new ModalDialogGuard(null))
                using (var automationScope = new SolidWorksAutomationScope(_sw, true, false, false, false))
                {
                    var scanWatch = System.Diagnostics.Stopwatch.StartNew();
                    scan = new AssemblyScanner(_sw).ScanActiveAssembly(p =>
                    {
                        _status.Text = string.Format(CultureInfo.CurrentCulture,
                            "Montaj taranıyor: {0}/{1} | {2} | {3:0.0} sn",
                            p.Index, p.Total, p.PartName, scanWatch.Elapsed.TotalSeconds);
                        try { automationScope.CleanupTransientDocuments(); } catch { }
                        Application.DoEvents();
                    }, null, false, true, false); // Hash doğrulamalı ortak cache; değişen dosyalar yeniden analiz edilir.
                    scanWatch.Stop();
                }

                // AssemblyScanner yalnız PartAnalyzer tarafından sac levha olarak tanınan
                // gerçek SLDPRT kayıtlarını döndürür. Kaynak occurrence'lar parça koduna
                // göre birleştirilmiş satırdan tekrar ayrıştırılır; konfigürasyon/adet kaybolmaz.
                var occurrences = new Dictionary<string, LaserPartRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var groupedRow in scan.Rows)
                {
                    var sourceRows = groupedRow.SourceOccurrences != null && groupedRow.SourceOccurrences.Count > 0
                        ? groupedRow.SourceOccurrences
                        : new List<LaserPartRow> { groupedRow };
                    foreach (var source in sourceRows)
                    {
                        if (source == null || string.IsNullOrWhiteSpace(source.SourcePath)) continue;
                        string occurrenceKey = source.SourcePath + "|" + (source.Configuration ?? "");
                        if (!occurrences.ContainsKey(occurrenceKey)) occurrences.Add(occurrenceKey, source);
                    }
                }

                foreach (var source in occurrences.Values)
                {
                    var row = new LaserPartRow
                    {
                        Selected = true,
                        PartCode = string.IsNullOrWhiteSpace(source.PartCode)
                            ? Path.GetFileNameWithoutExtension(source.SourcePath) : source.PartCode,
                        BaseQuantity = Math.Max(1, source.BaseQuantity),
                        Quantity = Math.Max(1, source.Quantity),
                        Material = source.Material,
                        ThicknessMm = source.ThicknessMm,
                        SourcePath = source.SourcePath,
                        Configuration = source.Configuration,
                        Error = source.Error,
                        FlatPatternCount = source.FlatPatternCount
                    };
                    _assemblyItems.Add(new AssemblyItem { Row = row, Quantity = row.Quantity });
                }

                var exporter = new DxfExporter(_sw);
                int ok = 0, failed = 0;
                using (var dialogGuard = new ModalDialogGuard(null))
                {
                    foreach (var item in _assemblyItems)
                    {
                        string dxf = Path.Combine(temp, SafeName(item.Row.PartCode) + "_" + (ok + failed + 1).ToString(CultureInfo.InvariantCulture) + ".dxf");
                        try
                        {
                            // Row, AssemblyScanner/PartAnalyzer'dan gelir; yeniden ve farklı
                            // bir filtreyle analiz edilmez. DxfExporter mevcut Lazer Hazırla
                            // üretiminde kullanılan SOLIDWORKS ExportToDWG2 yoludur.
                            exporter.Export(item.Row, dxf, false);
                            var contour = ReadClosedOuterContour(dxf);
                            if (contour.Count < 3) throw new InvalidDataException("DXF dış konturu 3 noktadan az.");
                            _parts.Add(new SmartNestPart
                            {
                                Name = item.Row.PartCode,
                                SourceDxfPath = dxf,
                                Quantity = item.Quantity,
                                OuterContour = contour,
                                AllowRotation = true,
                                RotationStepDegrees = 10.0
                            });
                            string mat = string.IsNullOrWhiteSpace(item.Row.Material) ? "Malzeme belirtilmemiş" : item.Row.Material;
                            string thk = item.Row.ThicknessMm.HasValue
                                ? item.Row.ThicknessMm.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm"
                                : "Kalınlık belirtilmemiş";
                            AddPartRow(item.Row.PartCode, item.Quantity, mat + " / " + thk, "Sac açınımı alındı; nesting'e hazır");
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            AddPartRow(item.Row.PartCode, item.Quantity, "-", "Açınım/kontur hatası: " + ex.Message);
                            failed++;
                            Logger.Error("SMARTNEST sheet metal flat pattern export: " + item.Row.PartCode, ex);
                        }
                    }
                }

                string warningText = scan.Warnings == null || scan.Warnings.Count == 0
                    ? ""
                    : " Tarama uyarısı: " + string.Join(" | ", scan.Warnings.Take(3));
                _status.Text = string.Format(CultureInfo.CurrentCulture,
                    "Montaj tarandı. {0} bileşen / {1} tekil parça-konfigürasyon; {2} sac adayından {3} açınım+kontur hazır, {4} hata. Suppressed bileşenler hariçtir.{5}",
                    scan.ComponentOccurrences, scan.UniquePartConfigurations, _assemblyItems.Count, ok, failed, warningText);
                if (_parts.Count == 0)
                {
                    Logger.Info("SMARTNEST diagnostics: ComponentOccurrences=" + scan.ComponentOccurrences +
                        ", UniquePartConfigurations=" + scan.UniquePartConfigurations +
                        ", SheetMetalRows=" + scan.Rows.Count + ", Warnings=" + (scan.Warnings == null ? 0 : scan.Warnings.Count));
                }
            }
            catch (Exception ex)
            {
                _status.Text = "Montaj tarama/açınım hatası: " + ex.Message;
                Logger.Error("SMARTNEST montaj tarama", ex);
            }
            finally
            {
                // Keep the exact Laser Prep flat-pattern DXFs alive until the user saves
                // the nesting output. The production DXF writer reads these source entities.
            }
        }
        private void AddPartRow(string name, int quantity, string material, string state)
        {
            int sequence = _grid.Rows.Count + 1;
            _grid.Rows.Add(sequence.ToString(CultureInfo.InvariantCulture), name, quantity, material, state);
        }
        private static string SafeName(string s){foreach(char c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_');return string.IsNullOrWhiteSpace(s)?"PARCA":s;}
        private void AddDxf()
        {
            using(var d=new OpenFileDialog{Filter="DXF dosyaları (*.dxf)|*.dxf",Multiselect=true,Title="Nesting için DXF parçaları seçin"})
            {if(d.ShowDialog(this)!=DialogResult.OK)return;foreach(string f in d.FileNames){try{var contour=ReadClosedOuterContour(f);if(contour.Count<3)throw new InvalidDataException("Kapalı dış kontur bulunamadı.");_parts.Add(new SmartNestPart{Name=Path.GetFileNameWithoutExtension(f),SourceDxfPath=f,Quantity=1,OuterContour=contour});AddPartRow(Path.GetFileName(f),1,"DXF içinden","Geometri alındı");}catch(Exception ex){AddPartRow(Path.GetFileName(f),0,"-","Atlandı: "+ex.Message);}}_status.Text="Geometri alınan parça türü: "+_parts.Count;}
        }
        private static IList<SmartNestPoint> ReadClosedOuterContour(string path)
        {
            return DxfContourReader.ReadLargestClosedContour(path);
        }

        private static IList<SmartNestPoint> RemoveDuplicateClosingPoint(IList<SmartNestPoint> points)
        {
            var result = points.ToList();
            if (result.Count > 1)
            {
                var a = result[0]; var b = result[result.Count - 1];
                if (Math.Abs(a.X - b.X) < 1e-7 && Math.Abs(a.Y - b.Y) < 1e-7) result.RemoveAt(result.Count - 1);
            }
            return result;
        }
        private static double PolygonArea(IList<SmartNestPoint> p){double a=0;for(int i=0;i<p.Count;i++){var q=p[i];var n=p[(i+1)%p.Count];a+=q.X*n.Y-n.X*q.Y;}return a/2.0;}
        private void RunNest()
        {
            try
            {
                if (_parts.Count == 0)
                {
                    var failures = _grid.Rows.Cast<DataGridViewRow>()
                        .Where(r => !r.IsNewRow)
                        .Select(r => Convert.ToString(r.Cells[0].Value) + ": " + Convert.ToString(r.Cells[3].Value))
                        .Take(6).ToList();
                    string detail = failures.Count == 0 ? "Henüz montaj taraması veya DXF aktarımı yapılmadı." : string.Join("\n", failures);
                    MessageBox.Show("Nesting için doğrulanmış kapalı dış kontur bulunamadı.\n\n" + detail +
                        "\n\nÖnce satırlardaki açınım/kontur hatalarını giderin.",
                        "SMARTNEST - Geometri bulunamadı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var cfg = new SmartNestSettings
                {
                    SheetWidth = (double)_width.Value,
                    SheetHeight = (double)_height.Value,
                    PartGap = (double)_gap.Value,
                    EdgeMargin = (double)_margin.Value,
                    Kerf = (double)_kerf.Value
                };
                _nestProgress.Value = 0;
                var nestWatch = System.Diagnostics.Stopwatch.StartNew();
                _status.Text = "Nesting başlatıldı; yerleşim hesaplanıyor...";
                _result = SmartNestEngine.Nest(_parts, cfg, (completed, total, checks) =>
                {
                    int percent = total <= 0 ? 100 : (int)Math.Round(100.0 * completed / total);
                    percent = Math.Max(0, Math.Min(100, percent));
                    _nestProgress.Value = percent;
                    string remaining = completed > 0
                        ? TimeSpan.FromSeconds(Math.Max(0, nestWatch.Elapsed.TotalSeconds / completed * (total - completed))).ToString(@"hh\:mm\:ss")
                        : "hesaplanıyor";
                    _status.Text = string.Format(CultureInfo.CurrentCulture,
                        "Nesting: %{0} | {1}/{2} parça yerleşti | {3} konum denendi | Kalan: {4}",
                        percent, completed, total, checks, remaining);
                    Application.DoEvents();
                });
                nestWatch.Stop();
                _nestProgress.Value = 100;
                _status.Text = string.Format(CultureInfo.CurrentCulture,
                    "Yerleşim tamamlandı: {0} sac, {1} adet parça, kullanım %{2:0.0} | Süre: {3:0.0} sn",
                    _result.SheetCount, _result.Placements.Count, _result.UtilizationPercent, nestWatch.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                _status.Text = "Nesting başarısız: " + ex.Message;
                Logger.Error("SMARTNEST yerleşim", ex);
            }
        }
        private void ShowPreview()
        {
            if (_result == null || _result.Placements == null || _result.Placements.Count == 0)
            {
                MessageBox.Show(this, "Önce NESTING YAP ile geçerli bir yerleşim oluşturun.", "SMARTNEST");
                return;
            }

            var preview = new Form
            {
                Text = "SMARTNEST | Sac Yerleşim Önizlemesi",
                Width = 1000, Height = 720, MinimumSize = new Size(640, 480),
                StartPosition = FormStartPosition.CenterParent, BackColor = Color.FromArgb(244, 246, 249)
            };
            int selectedSheet = 0;
            var top = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.White };
            var info = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                Text = string.Format(CultureInfo.CurrentCulture,
                    "{0} sac | {1} adet yerleşti | Kullanım %{2:0.0}",
                    _result.SheetCount, _result.Placements.Count, _result.UtilizationPercent)
            };
            var sheetPicker = new ComboBox { Dock = DockStyle.Right, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            for (int i = 0; i < _result.SheetCount; i++) sheetPicker.Items.Add("Sac " + (i + 1).ToString(CultureInfo.InvariantCulture));
            if (sheetPicker.Items.Count > 0) sheetPicker.SelectedIndex = 0;
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(235, 239, 243) };
            sheetPicker.SelectedIndexChanged += (s, e) => { selectedSheet = Math.Max(0, sheetPicker.SelectedIndex); canvas.Invalidate(); };
            top.Controls.Add(info); top.Controls.Add(sheetPicker);
            canvas.Paint += (sender, e) =>
            {
                var placements = _result.Placements.Where(p => p.SheetIndex == selectedSheet && p.Contour != null && p.Contour.Count >= 3).ToList();
                if (placements.Count == 0) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float pad = 36f;
                float sx = (canvas.ClientSize.Width - 2 * pad) / (float)_width.Value;
                float sy = (canvas.ClientSize.Height - 2 * pad) / (float)_height.Value;
                float scale = Math.Max(0.0001f, Math.Min(sx, sy));
                float sheetW = (float)_width.Value * scale, sheetH = (float)_height.Value * scale;
                float ox = (canvas.ClientSize.Width - sheetW) / 2f;
                float oy = (canvas.ClientSize.Height - sheetH) / 2f;
                using (var sheetPen = new Pen(Color.FromArgb(55, 65, 75), 2f))
                using (var partPen = new Pen(Color.FromArgb(20, 105, 160), 1.5f))
                using (var fill = new SolidBrush(Color.FromArgb(105, 75, 160, 210)))
                using (var font = new Font("Segoe UI", 8f))
                using (var textBrush = new SolidBrush(Color.FromArgb(35, 45, 55)))
                {
                    e.Graphics.DrawRectangle(sheetPen, ox, oy, sheetW, sheetH);
                    foreach (var placement in placements)
                    {
                        var pts = placement.Contour.Select(p => new PointF(
                            ox + (float)p.X * scale, oy + sheetH - (float)p.Y * scale)).ToArray();
                        if (pts.Length >= 3)
                        {
                            e.Graphics.FillPolygon(fill, pts);
                            e.Graphics.DrawPolygon(partPen, pts);
                            var center = new PointF(pts.Average(p => p.X), pts.Average(p => p.Y));
                            e.Graphics.DrawString(placement.PartName ?? "Parça", font, textBrush, center);
                        }
                    }
                }
            };
            preview.Controls.Add(canvas);
            preview.Controls.Add(top);
            preview.ShowDialog(this);
            preview.Dispose();
        }

        private void SaveDxf()
        {
            if (_result == null || _result.Placements == null || _result.Placements.Count == 0)
            {
                MessageBox.Show(this, "Önce geçerli bir nesting yerleşimi oluşturun.");
                return;
            }
            using (var d = new SaveFileDialog { Filter = "DXF (*.dxf)|*.dxf", FileName = "SMARTNEST_YERLESIM.dxf" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SmartNestEngine.WriteDxf(d.FileName, _result);
                    var fi = new FileInfo(d.FileName);
                    if (!fi.Exists || fi.Length < 128)
                        throw new InvalidDataException("DXF çıktı dosyası boş veya beklenenden küçük.");
                    _status.Text = "DXF doğrulandı ve kaydedildi: " + d.FileName;
                }
                catch (Exception ex)
                {
                    _status.Text = "DXF kaydedilemedi: " + ex.Message;
                    MessageBox.Show(this, ex.Message, "SMARTNEST DXF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Error("SMARTNEST DXF export", ex);
                }
            }
        }
    }
}
