using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.Services
{
    internal sealed class SolidWorksAutomationScope : IDisposable
    {
        private readonly SldWorks _swApp;
        private readonly HashSet<string> _baselineVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool _hideParts;
        private readonly bool _hideDrawings;
        private bool _disposed;
        private readonly bool _lockUserCommands;
        private readonly bool _cleanupNewVisibleDocuments;
        private bool _oldNoPrompt;
        private bool _oldNoPromptKnown;
        private long _startPrivateMb;
        private int _startDocuments;

        public SolidWorksAutomationScope(SldWorks swApp, bool hideParts, bool hideDrawings, bool lockUserCommands = true, bool cleanupNewVisibleDocuments = true)
        {
            _swApp = swApp;
            _hideParts = hideParts;
            _hideDrawings = hideDrawings;
            _lockUserCommands = lockUserCommands;
            _cleanupNewVisibleDocuments = cleanupNewVisibleDocuments;
            CaptureBaselineVisibleDocuments();
            if (!lockUserCommands)
            {
                try { using (var process = System.Diagnostics.Process.GetCurrentProcess()) _startPrivateMb = process.PrivateMemorySize64 / (1024 * 1024); _startDocuments = _swApp.GetDocumentCount(); }
                catch { }
            }

            try
            {
                int pref = (int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave;
                _oldNoPrompt = _swApp.GetUserPreferenceToggle(pref);
                _oldNoPromptKnown = true;
                _swApp.SetUserPreferenceToggle(pref, true);
            }
            catch { }

            // Uzun SrcCadWorks tarama/üretim işlerinde kullanıcı ana SOLIDWORKS penceresini
            // kullanabilsin diye bu kilit isteğe bağlıdır. Önizleme gibi kısa ve hassas işlemler
            // varsayılan olarak eski davranışı (CommandInProgress=true) korur.
            if (_lockUserCommands)
            {
                try { ((dynamic)_swApp).CommandInProgress = true; } catch { }
            }
            if (hideParts) { try { ((dynamic)_swApp).DocumentVisible(false, (int)swDocumentTypes_e.swDocPART); } catch { } }
            if (hideDrawings) { try { ((dynamic)_swApp).DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING); } catch { } }
        }

        // SrcCadWorks işlemi sırasında bir parça/drawing görünür sekmeye dönüştüyse ve işlem başlamadan önce
        // kullanıcı tarafından görünür değildiyse kapatır. Referans model verisi montaj belleğinde kalabilir;
        // CloseDoc UI kaynaklarını serbest bırakır.
        public int CleanupTransientDocuments()
        {
            // Modeless uzun işlemlerde kullanıcı üretim sürerken yeni bir belge açabilir.
            // Bu modda başlangıçtan sonra görünen her belgeyi "geçici" sayıp kapatmak güvenli değildir.
            // Export servisleri kendi açtıkları belgeleri zaten kendileri kapatır.
            if (!_cleanupNewVisibleDocuments) return 0;
            if (_disposed || _swApp == null) return 0;
            int closed = 0;
            object docsObj = null;
            try
            {
                docsObj = _swApp.GetDocuments();
                var docs = docsObj as object[];
                if (docs == null) return 0;
                foreach (var obj in docs)
                {
                    var doc = obj as ModelDoc2;
                    if (doc == null) continue;
                    try
                    {
                        bool visible = false;
                        string key = "";
                        string title = "";
                        try { visible = doc.Visible; } catch { }
                        if (!visible) continue;
                        try { key = DocumentKey(doc); } catch { }
                        if (_baselineVisible.Contains(key)) continue;
                        try { title = doc.GetTitle() ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        try
                        {
                            _swApp.CloseDoc(title);
                            closed++;
                            Logger.Info("Geçici görünür SOLIDWORKS belgesi kapatıldı: " + key);
                        }
                        catch { }
                    }
                    finally
                    {
                        // Baseline kullanıcı belgelerinin RCW'sini zorla release etmiyoruz. Geçici belge kapatıldıysa
                        // yerel wrapper'ın GC ile bırakılması yeterli; kullanıcıya ait aktif RCW'leri bozmayalım.
                    }
                }
            }
            catch { }
            return closed;
        }

        private void CaptureBaselineVisibleDocuments()
        {
            object docsObj = null;
            try
            {
                docsObj = _swApp.GetDocuments();
                var docs = docsObj as object[];
                if (docs == null) return;
                foreach (var obj in docs)
                {
                    var doc = obj as ModelDoc2;
                    if (doc == null) continue;
                    try
                    {
                        bool visible = false;
                        try { visible = doc.Visible; } catch { }
                        if (visible) _baselineVisible.Add(DocumentKey(doc));
                    }
                    finally { }
                }
            }
            catch { }
        }

        private static string DocumentKey(ModelDoc2 doc)
        {
            string path = "";
            try { path = doc.GetPathName() ?? ""; } catch { }
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
                catch { return path.ToUpperInvariant(); }
            }
            string title = "";
            try { title = doc.GetTitle() ?? ""; } catch { }
            return "TITLE:" + title.ToUpperInvariant();
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { CleanupTransientDocuments(); } catch { }
            _disposed = true;
            if (_hideParts) { try { ((dynamic)_swApp).DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); } catch { } }
            if (_hideDrawings) { try { ((dynamic)_swApp).DocumentVisible(true, (int)swDocumentTypes_e.swDocDRAWING); } catch { } }
            if (_lockUserCommands)
            {
                try { ((dynamic)_swApp).CommandInProgress = false; } catch { }
            }
            if (_oldNoPromptKnown)
            {
                try { _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave, _oldNoPrompt); } catch { }
            }
            if (!_lockUserCommands)
            {
                ComUtil.ManagedCleanup(true);
                try
                {
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        process.Refresh();
                        Logger.Info("İşlem sonu bellek: özel=" + _startPrivateMb + " -> " + (process.PrivateMemorySize64 / (1024 * 1024)) + " MB; RAM=" + (process.WorkingSet64 / (1024 * 1024)) + " MB; yüklü belge=" + _startDocuments + " -> " + _swApp.GetDocumentCount());
                    }
                }
                catch { }
            }
        }
    }
}
