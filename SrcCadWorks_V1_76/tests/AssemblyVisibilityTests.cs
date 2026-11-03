using System;
using System.Collections.Generic;
using System.Linq;
using SrcCadWorks.Models;
using SrcCadWorks.Services;
using SolidWorks.Interop.sldworks;

internal static class VisibilityTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static Component2 Part(string id, string cfg = "A") { return new Component2 { Id = id, Path = "part.sldprt", Configuration = cfg }; }
    static LaserPartRow Row(params string[] ids) { return new LaserPartRow { PartCode = "P1", SourcePath = "part.sldprt", Configuration = "A", OccurrenceSelectIds = ids.ToList() }; }
    static AssemblyDoc Model(params Component2[] components) { return new AssemblyDoc { Components = components }; }
    static AssemblyVisibilityTracker Tracker(AssemblyDoc model) { return new AssemblyVisibilityTracker(new SldWorks { ActiveDoc = model }); }
    public static void Main()
    {
        var a = Part("a"); var b = Part("b"); var other = Part("other", "B");
        var model = Model(a, b, other); model.Extension.Ids["a"] = a;
        using (var t = Tracker(model)) {
            Check(t.HideOccurrences(Row("a", "stale")) == 2, "Partial ID failure must hide both A occurrences");
            Check(a.Visible == 0 && b.Visible == 0 && other.Visible == 1, "Configuration boundary");
            Check(model.ScanCount == 1, "One fallback traversal");
        }
        a = Part("a"); b = Part("b"); b.Path = "second.sldprt";
        model = Model(a, b); model.Extension.Ids["a"] = a;
        var missing = Row(); missing.SourcePath = b.Path;
        using (var t = Tracker(model)) Check(t.HideOccurrencesBatch(new[] { Row("a"), missing }) == 2, "Mixed ID/no-ID batch");
        a = Part("a"); model = Model(a); model.Extension.Ids["a"] = a; model.HideNoOp = true;
        using (var t = Tracker(model)) Check(t.HideOccurrences(Row("a")) == 1 && a.Visible == 0, "No-op command must use component setter");
        a = Part("a"); a.IgnoreHide = true; model = Model(a); model.Extension.Ids["a"] = a;
        using (var t = Tracker(model)) Check(t.HideOccurrences(Row("a")) == 0 && t.HiddenOccurrenceCount == 0, "Failed setter must not count as success");
        a = Part("a"); other = Part("wrong", "B"); model = Model(a, other); model.Extension.Ids["stale"] = other;
        using (var t = Tracker(model)) Check(t.HideOccurrences(Row("stale")) == 1 && other.Visible == 1, "Reused wrong identity must not hide other config");
        a = Part("a"); a.Visible = 0; model = Model(a); model.Extension.Ids["a"] = a;
        using (var t = Tracker(model)) Check(t.HideOccurrences(Row("a")) == 0 && t.HiddenOccurrenceCount == 0, "Pre-hidden components must not be restored later");
        var many = Enumerable.Range(0, 130).Select(i => Part("id" + i)).ToArray(); model = Model(many);
        foreach (var c in many) model.Extension.Ids[c.Id] = c;
        using (var t = Tracker(model)) Check(t.HideOccurrences(Row(many.Select(c => c.Id).ToArray())) == 130 && model.ScanCount == 0, "Fast path over batch boundary");
        a = Part("a"); model = Model(a);
        var app = new SldWorks { ActiveDoc = model };
        using (var t = new AssemblyVisibilityTracker(app)) {
            app.ActiveDoc = new ModelDoc2();
            Check(t.HideOccurrences(Row()) == 1, "Hide must target captured assembly after document switch");
            Check(t.VerifyHidden(new[] { Row() }).Count == 0, "Hidden occurrence must verify");
        }
        a = Part("a"); a.IgnoreHide = true; model = Model(a);
        using (var t = Tracker(model)) {
            t.HideOccurrences(Row());
            Check(t.VerifyHidden(new[] { Row() }).Count == 1, "Visible occurrence must report failure");
            var absent = Row(); absent.SourcePath = "missing.sldprt";
            Check(t.VerifyHidden(new[] { absent }).Count == 1, "Missing occurrence must report failure");
        }
        Console.WriteLine("PASS: 9 visibility scenarios (COM substitutes; real SOLIDWORKS test still required).");
    }
}
namespace SrcCadWorks.Services {
    internal static class ComUtil { public static void Release(object value) {} }
    internal static class Logger { public static void Info(string value) {} }
}
namespace SolidWorks.Interop.swconst {
    public enum swDocumentTypes_e { swDocASSEMBLY = 2 }
    public enum swComponentVisibilityState_e { swComponentHidden = 0 }
    public enum swComponentSuppressionState_e { swComponentSuppressed = 0 }
    public enum swSelectOption_e { swSelectOptionDefault = 0 }
}
namespace SolidWorks.Interop.sldworks {
    public class SldWorks { public object ActiveDoc; }
    public class Component2 {
        public string Id, Path, Configuration;
        public bool IgnoreHide;
        private int visible = 1;
        public int Visible { get { return visible; } set { if (!IgnoreHide) visible = value; } }
        public string ReferencedConfiguration { get { return Configuration; } }
        public string Name2 { get { return Id; } }
        public string GetPathName() { return Path; }
        public int GetSuppression2() { return 2; }
        public int GetSuppression() { return 2; }
        public bool IsHidden(bool suppressed) { return Visible == 0; }
        public object GetParent() { return null; }
        public string GetSelectByIDString() { return Id; }
    }
    public class ModelDoc2 {
        public ModelDocExtension Extension;
        public SelectionMgr SelectionManager;
        public bool HideNoOp;
        public ModelDoc2() { SelectionManager = new SelectionMgr(); Extension = new ModelDocExtension(SelectionManager); }
        public new int GetType() { return 2; }
        public void ClearSelection2(bool all) { SelectionManager.Selected.Clear(); }
        public void HideComponent2() { if (!HideNoOp) foreach (var c in SelectionManager.Selected) c.Visible = 0; }
        public void ShowComponent2() { foreach (var c in SelectionManager.Selected) c.Visible = 1; }
        public void GraphicsRedraw2() {}
    }
    public class AssemblyDoc : ModelDoc2 {
        public Component2[] Components; public int ScanCount;
        public object GetComponents(bool top) { ScanCount++; return Components.Cast<object>().ToArray(); }
    }
    public class SelectionMgr {
        public List<Component2> Selected = new List<Component2>();
        public int GetSelectedObjectCount2(int mark) { return Selected.Count; }
        public object GetSelectedObjectsComponent4(int index, int mark) { return Selected[index - 1]; }
    }
    public class ModelDocExtension {
        public Dictionary<string, Component2> Ids = new Dictionary<string, Component2>();
        private SelectionMgr selection;
        public ModelDocExtension(SelectionMgr s) { selection = s; }
        public bool SelectByID2(string id, string type, double x, double y, double z, bool append, int mark, object data, int option) {
            if (!append) selection.Selected.Clear();
            Component2 c; if (!Ids.TryGetValue(id, out c)) return false;
            selection.Selected.Add(c); return true;
        }
    }
}
