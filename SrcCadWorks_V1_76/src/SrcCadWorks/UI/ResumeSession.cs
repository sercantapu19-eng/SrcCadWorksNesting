using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    internal sealed class ResumeSession : IDisposable
    {
        private readonly Form _form;
        private readonly SldWorks _sw;
        private readonly Func<IEnumerable> _rows;
        private readonly Func<bool> _busy;
        private readonly Action _scan, _generate;
        private readonly string _document, _configuration;
        private readonly bool _laser;
        private readonly ResumeLedger _ledger;
        private readonly Timer _timer;
        private readonly Button _button;
        private readonly Label _hint;
        private bool _ready, _restore = true, _running;
        private string _signature = "", _inputHash = "";
        private int _committed;
        private readonly Dictionary<string, string> _fileStamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Requested { get; private set; }
        public bool WantsResume => Requested && _ledger.State.Pending;
        public string Folder => WantsResume ? _ledger.State.OutputFolder : null;
        public ResumeSession(Form form, SldWorks sw, string module, Func<IEnumerable> rows, Func<bool> busy, Action scan, Action generate)
        {
            _laser = form is LaserPrepForm;
            _form = form; _sw = sw; _rows = rows; _busy = busy; _scan = scan; _generate = generate;
            var model = sw.ActiveDoc as ModelDoc2;
            _document = model == null ? "" : model.GetPathName() ?? "";
            _configuration = model == null ? "" : model.ConfigurationManager.ActiveConfiguration.Name ?? "";
            _ledger = new ResumeLedger(string.IsNullOrEmpty(_document) ? "" : module + "|" + Path.GetFullPath(_document).ToUpperInvariant() + "|" + _configuration);
            _button = new Button { Text = "KALDIĞIN YERDEN DEVAM ET", AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(202, 28, 36), Margin = new Padding(0, 2, 14, 0) };
            _hint = new Label { AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; panel.Controls.Add(_button); panel.Controls.Add(_hint);
            var root = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (root != null)
            {
                root.SuspendLayout();
                foreach (Control child in root.Controls.Cast<Control>().OrderByDescending(x => root.GetRow(x)).ToList()) root.SetRow(child, root.GetRow(child) + 1);
                root.RowCount++; root.RowStyles.Insert(0, new RowStyle(SizeType.Absolute, 42)); root.Controls.Add(panel, 0, 0); root.SetColumnSpan(panel, root.ColumnCount); root.ResumeLayout();
            }
            _button.Click += (s, e) =>
            {
                if (_busy() || !_ledger.State.Pending) return;
                try { _restore = true; ApplyControls(); _scan(); if (!_ready) return; Requested = true; _generate(); }
                catch (Exception ex) { MessageBox.Show(_form, ex.Message, "SrcCadWorks — Devam"); }
                finally { Requested = false; Update(); }
            };
            _timer = new Timer { Interval = 1000 }; _timer.Tick += (s, e) => { Update(); if (_ready && !_running && !_busy() && !_ledger.State.Pending) SaveChoices(); }; _timer.Start();
            form.FormClosing += (s, e) => { if (!e.Cancel && !_busy() && !_running && !_ledger.State.Pending) SaveChoices(); };
            form.FormClosed += (s, e) => Dispose();
            ConfigureProductionGrid();
            ApplyControls(); Update();
        }
        // Shared behavior for the production forms; edits affect output rows only.
        private void ConfigureProductionGrid()
        {
            if (!(_form is ManufacturingPrepForm) && !(_form is MachiningPrepForm) && !(_form is WeldPrepForm)) return;
            var grid = _form.GetType().GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_form) as DataGridView;
            if (grid == null) return;
            grid.ReadOnly = false;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            var editable = new HashSet<string>(new[] { "ProductDescription", "ProfileType", "ColorCode", "OperationType", "Material", "Description" });
            foreach (DataGridViewColumn column in grid.Columns)
                if (editable.Contains(column.DataPropertyName))
                {
                    column.ReadOnly = false;
                    column.DefaultCellStyle.BackColor = Color.FromArgb(255, 252, 235);
                    column.ToolTipText = "F2 veya yazarak düzenleyin. Düzenleme üretim çıktısına uygulanır.";
                }
            grid.CellBeginEdit += (s, e) => { if (_busy()) e.Cancel = true; };
            grid.CellDoubleClick += (s, e) =>
            {
                if (_busy() || e.RowIndex < 0 || e.ColumnIndex < 0 || !grid.Columns[e.ColumnIndex].ReadOnly) return;
                bool drawing = grid.Columns[e.ColumnIndex].DataPropertyName == "DrawingPath";
                OpenProductionRow(grid.Rows[e.RowIndex].DataBoundItem, drawing);
            };
            grid.CellMouseDown += (s, e) =>
            {
                if (_busy() || e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                grid.EndEdit();
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                object row = grid.Rows[e.RowIndex].DataBoundItem;
                if (row == null) return;
                var menu = new ContextMenuStrip();
                var open = menu.Items.Add("SOLIDWORKS'te Aç");
                open.Enabled = File.Exists(Value(row, "SourcePath"));
                open.Click += (sender, args) => OpenProductionRow(row, false);
                var drawing = menu.Items.Add("Teknik Resmi SOLIDWORKS'te Aç");
                drawing.Enabled = File.Exists(Value(row, "DrawingPath"));
                drawing.Click += (sender, args) => OpenProductionRow(row, true);
                if (!grid.Columns[e.ColumnIndex].ReadOnly)
                {
                    var edit = menu.Items.Add("Alanı Düzenle");
                    edit.Click += (sender, args) => { if (!_busy()) grid.BeginEdit(true); };
                }
                menu.Closed += (sender, args) =>
                {
                    if (!_form.IsDisposed && _form.IsHandleCreated)
                        _form.BeginInvoke(new Action(() => menu.Dispose()));
                };
                menu.Show(System.Windows.Forms.Cursor.Position);
            };
            grid.Leave += (s, e) => { grid.EndEdit(); (_form.BindingContext[grid.DataSource] as CurrencyManager)?.EndCurrentEdit(); };
        }

        private void OpenProductionRow(object row, bool drawing)
        {
            if (row == null || _busy() || _form.IsDisposed) return;
            string path = Value(row, drawing ? "DrawingPath" : "SourcePath");
            if (!File.Exists(path)) return;
            _form.BeginInvoke(new Action(() =>
            {
                if (_busy() || _form.IsDisposed) return;
                try
                {
                    int error = 0, warning = 0;
                    var doc = _sw.GetOpenDocumentByName(path) as ModelDoc2;
                    if (doc == null)
                    {
                        int type = drawing ? 3 : (path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
                        doc = _sw.OpenDoc6(path, type, 1, drawing ? "" : Value(row, "Configuration"), ref error, ref warning) as ModelDoc2;
                    }
                    if (doc == null) throw new IOException("Dosya açılamadı. Hata=" + error);
                    _sw.ActivateDoc3(doc.GetTitle(), true, 0, ref error);
                }
                catch (Exception ex) { MessageBox.Show(_form, ex.Message, "SrcCadWorks — SOLIDWORKS'te Aç"); }
            }));
        }

        private void Update()
        {
            _button.Enabled = _ready && !_running && !_busy() && _ledger.State.Pending;
            _hint.Text = _ledger.State.Pending ? "Yarım kalan işlem kayıtlı • Tamamlanan çıktı: " + _ledger.State.Files.Count :
                (string.IsNullOrEmpty(_document) ? "Seçimleri saklamak için kaynak belgeyi kaydedin." : "Seçimler saklanır • OLUŞTUR yeni üretim başlatır.");
        }
        private IEnumerable<object> Rows() => (_rows() ?? new object[0]).Cast<object>().Where(x => x != null);
        private static string Value(object row, string name) => row == null ? "" : Convert.ToString(row.GetType().GetProperty(name)?.GetValue(row, null), CultureInfo.InvariantCulture) ?? "";
        public static string Key(object row) => string.Join("|", Value(row, "SourcePath").ToUpperInvariant(), Value(row, "Configuration"), Value(row, "RootPartCode"), Value(row, "ParentPartCode"), Value(row, "FolderName"), Value(row, "PartCode"));
        private IEnumerable<KeyValuePair<string, Control>> Controls()
        {
            foreach (var field in _form.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                object value = field.GetValue(_form); var c = value as Control;
                if (c is TextBox || c is CheckBox || c is RadioButton || c is NumericUpDown || c is ComboBox) yield return new KeyValuePair<string, Control>(field.Name, c);
                if (field.Name == "_processCodes" && value is IDictionary) foreach (DictionaryEntry entry in (IDictionary)value) if (entry.Value is Control) yield return new KeyValuePair<string, Control>("code:" + entry.Key, (Control)entry.Value);
            }
        }
        private Dictionary<string, string> CaptureControls() => Controls().ToDictionary(x => x.Key, x => x.Value is CheckBox ? ((CheckBox)x.Value).Checked.ToString() : x.Value is RadioButton ? ((RadioButton)x.Value).Checked.ToString() : x.Value is NumericUpDown ? ((NumericUpDown)x.Value).Value.ToString(CultureInfo.InvariantCulture) : x.Value.Text);
        private Dictionary<string, Dictionary<string, string>> CaptureRows()
        {
            var manual = _form.GetType().GetField("_manualQuantities", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_form) as IDictionary;
            var choices = new Dictionary<string, Dictionary<string, string>>();
            foreach (var row in Rows())
            {
                var values = new Dictionary<string, string>();
                foreach (string name in new[] { "Selected", "StepSelected" })
                    if (row.GetType().GetProperty(name)?.CanWrite == true) values[name] = Value(row, name);
                choices[Key(row)] = values;
            }
            return choices;
        }
        private void SaveChoices()
        {
            try
            {
                var controls = CaptureControls(); var rows = CaptureRows();
                string signature = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { controls, rows });
                if (signature == _signature) return;
                _ledger.State.Controls = controls; _ledger.State.Rows = rows; _ledger.Save(); _signature = signature;
            }
            catch (Exception ex) { _hint.Text = "Devam kaydı yazılamadı: " + ex.Message; Logger.Error("Devam kaydı yazılamadı", ex); }
        }
        private void ApplyControls()
        {
            foreach (var item in Controls())
            {
                string value; if (!_ledger.State.Controls.TryGetValue(item.Key, out value)) continue;
                var c = item.Value;
                if (c is CheckBox) ((CheckBox)c).Checked = value == "True";
                else if (c is RadioButton) ((RadioButton)c).Checked = value == "True";
                else if (c is NumericUpDown) { decimal n; var numeric = (NumericUpDown)c; if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out n)) numeric.Value = Math.Max(numeric.Minimum, Math.Min(numeric.Maximum, n)); }
                else c.Text = value;
            }
        }
        public void BeforeScan() { if (_ready && !_ledger.State.Pending) SaveChoices(); _ready = false; if (_restore) ApplyControls(); }
        public void AfterScan()
        {
            if (_restore)
            {
                ApplyControls();
                var manual = _form.GetType().GetField("_manualQuantities", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_form) as IDictionary;
                foreach (var row in Rows())
                {
                    Dictionary<string, string> choice; if (!_ledger.State.Rows.TryGetValue(Key(row), out choice)) continue;
                    foreach (var value in choice) { if (value.Key != "Selected" && value.Key != "StepSelected") continue; var property = row.GetType().GetProperty(value.Key); if (property?.CanWrite == true) try { property.SetValue(row, Convert.ChangeType(value.Value, property.PropertyType, CultureInfo.InvariantCulture), null); } catch { } }
                }
                _restore = false;
            }
            _ready = true; var grid = _form.GetType().GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_form) as DataGridView; grid?.Refresh(); Update();
        }
        public string Begin(string folder)
        {
            var model = _sw.ActiveDoc as ModelDoc2;
            if (model == null || !string.Equals(model.GetPathName(), _document, StringComparison.OrdinalIgnoreCase) || model.ConfigurationManager.ActiveConfiguration.Name != _configuration) throw new InvalidOperationException("Aktif belge/konfigürasyon değişti. Modülü hedef belgede yeniden açın.");
            var controls = CaptureControls(); var choices = CaptureRows();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _document };
            foreach (var row in Rows()) { paths.Add(Value(row, "SourcePath")); paths.Add(Value(row, "DrawingPath")); }
            string template; if (controls.TryGetValue("_template", out template)) paths.Add(template);
            try { var deps = _sw.GetDocumentDependencies2(_document, true, true, false) as object[]; if (deps != null) foreach (var dep in deps) if (dep is string && File.Exists((string)dep)) paths.Add((string)dep); } catch { }
            _fileStamps.Clear();
            foreach (string path in paths.Where(x => !string.IsNullOrEmpty(x))) _fileStamps[path] = Stamp(path);
            _inputHash = ResumeLedger.Hash(string.Join("\n", paths.Where(x => !string.IsNullOrEmpty(x)).OrderBy(x => x).Select(x => x.ToUpperInvariant() + "=" + ResumeLedger.FileHash(x))) +
                string.Join("\n", controls.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value)) + string.Join("\n", choices.OrderBy(x => x.Key).Select(x => x.Key + "=" + string.Join(";", x.Value.OrderBy(y => y.Key).Select(y => y.Key + "=" + y.Value)))));
            // Output reuse also depends on fresh row values and current manual edits.
            var currentRows = Rows().OrderBy(Key).Select(row => Key(row) + "=" +
                string.Join(";", row.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .OrderBy(p => p.Name).Select(p => p.Name + "=" + Value(row, p.Name))));
            _inputHash = ResumeLedger.Hash(_inputHash + "\n" + string.Join("\n", currentRows));
            _ledger.Begin(folder, WantsResume, controls, choices); _running = true; Update(); return _ledger.State.OutputFolder;
        }
        private static string Stamp(string path)
        {
            try { var info = new FileInfo(path); return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "missing"; } catch { return "unreadable"; }
        }
        private string Input(object row)
        {
            foreach (string path in new[] { _document, Value(row, "SourcePath"), Value(row, "DrawingPath") }.Where(x => !string.IsNullOrEmpty(x)))
            {
                var doc = _sw.GetOpenDocumentByName(path) as ModelDoc2;
                if (doc != null && doc.GetSaveFlag()) return "";
            }
            foreach (var pair in _fileStamps) if (pair.Value != Stamp(pair.Key)) return "";
            return _inputHash;
        }
        public bool Reuse(string stage, object row, string path) => _ledger.Resuming && _ledger.Reuse(stage + "|" + Key(row), path, Input(row));
        public void Start(string stage, object row) => _ledger.Invalidate(stage + "|" + Key(row));
        public void Commit(string stage, object row, string path) { _ledger.Commit(stage + "|" + Key(row), path, Input(row)); if (++_committed % 12 == 0) ComUtil.ManagedCleanup(true); }
        public string WorkFolder { get { string p = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "resume_work", _ledger.State.RunId ?? "pending"); Directory.CreateDirectory(p); return p; } }
        public List<string> PreviousErrors() => _ledger.Resuming ? new List<string>(_ledger.State.Errors) : new List<string>();
        public Dictionary<string, string> PreviousNotes() => _ledger.Resuming ? new Dictionary<string, string>(_ledger.State.Notes, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public void SaveProgress(IEnumerable<string> errors, IDictionary<string, string> notes) { _ledger.State.Errors = errors.Distinct().ToList(); _ledger.State.Notes = notes == null ? new Dictionary<string, string>() : new Dictionary<string, string>(notes); _ledger.Save(); }
        public void ClearFailure(string stage, object row, IList<string> errors, IDictionary<string, string> notes, string noteKey)
        {
            string code = Value(row, "PartCode");
            Func<string, bool> kind = text => text.IndexOf(stage, StringComparison.OrdinalIgnoreCase) >= 0 || (stage == "PDF" && text.IndexOf("teknik resim", StringComparison.OrdinalIgnoreCase) >= 0);
            Func<string, bool> match = text => kind(text) && (row == null || (!string.IsNullOrEmpty(code) && System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(code) + @"(?![A-Za-z0-9_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
            Func<string, bool> noteMatch = text => match(text) || (row != null && noteKey == code && !text.Contains(" - ") && kind(text));
            for (int i = errors.Count - 1; i >= 0; i--) if (match(errors[i])) errors.RemoveAt(i);
            string note; if (notes != null && noteKey != null && notes.TryGetValue(noteKey, out note)) { note = string.Join(" | ", note.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries).Where(x => !noteMatch(x))); if (note.Length == 0) notes.Remove(noteKey); else notes[noteKey] = note; }
        }
        public void Finish(bool success) { _ledger.Finish(success); _running = false; _signature = ""; Update(); if (success) try { Directory.Delete(WorkFolder, true); } catch { } }
        public void Suspend() { _running = false; Update(); }
        public void Dispose() { _timer.Stop(); _timer.Dispose(); }
    }
}
