using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace SrcCadWorks.Services
{
    internal sealed class ResourceSnapshot
    {
        public int GdiObjects { get; set; }
        public int UserObjects { get; set; }
        public long WorkingSetMb { get; set; }
        public int LoadedDocuments { get; set; }
        public int VisibleDocuments { get; set; }

        // DOC görünür/yüklü. Büyük montajlarda SOLIDWORKS referans parçaları bellekte yüklü
        // tutabilir; bu tek başına pencere/GDI kaynağı sızıntısı anlamına gelmez.
        public string ShortText => "DOC " + VisibleDocuments + "/" + LoadedDocuments +
            " / GDI " + GdiObjects + " / USER " + UserObjects + " / RAM " + WorkingSetMb + " MB";
    }

    internal sealed class SolidWorksResourceMonitor
    {
        private const int GR_GDIOBJECTS = 0;
        private const int GR_USEROBJECTS = 1;
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly SldWorks _swApp;
        private readonly ResourceSnapshot _baseline;

        public SolidWorksResourceMonitor(SldWorks swApp = null)
        {
            _swApp = swApp;
            _baseline = Snapshot();
        }

        public ResourceSnapshot Checkpoint(int processed, bool force = false)
        {
            var before = Snapshot();
            int gdiGrowth = before.GdiObjects - _baseline.GdiObjects;
            int userGrowth = before.UserObjects - _baseline.UserObjects;
            int visibleDocGrowth = before.VisibleDocuments - _baseline.VisibleDocuments;

            // GetDocumentCount tüm bellekte yüklü referansları da içerir. Büyük montajlarda 50, 500
            // hatta daha fazla yüklü parça normal olabilir. Koruma kararı yalnız gerçek pencere/GDI/USER
            // kaynakları ve görünür belge artışı üzerinden verilir.
            if (processed % 8 == 0) ComUtil.ManagedCleanup(false);
            if (force || gdiGrowth > 220 || userGrowth > 220 || visibleDocGrowth > 6 || processed % 36 == 0)
            {
                ComUtil.ManagedCleanup(true);
                Thread.Sleep(20);
            }

            var after = Snapshot();
            int afterGdiGrowth = after.GdiObjects - _baseline.GdiObjects;
            int afterUserGrowth = after.UserObjects - _baseline.UserObjects;
            int afterVisibleDocGrowth = after.VisibleDocuments - _baseline.VisibleDocuments;

            // V1.10.2: Kaynak göstergeleri yalnız izlenir; SrcCadWorks kendi kendine tarama/üretimi iptal etmez.
            // Yük yükselirse temizleme yapılır ve loga uyarı düşülür, ancak durdurma kararı kullanıcıdadır.
            if (after.GdiObjects >= 7600 || after.UserObjects >= 7600 ||
                afterGdiGrowth >= 2400 || afterUserGrowth >= 2400 || afterVisibleDocGrowth >= 20)
            {
                Logger.Info("SOLIDWORKS kaynak kullanımı yüksek, işlem devam ediyor: " + after.ShortText);
            }
            return after;
        }

        public ResourceSnapshot Snapshot()
        {
            try
            {
                _process.Refresh();
                int loaded = 0;
                int visible = 0;
                if (_swApp != null)
                {
                    try { loaded = _swApp.GetDocumentCount(); } catch { }
                    try
                    {
                        object docsObject = _swApp.GetDocuments();
                        var docs = docsObject as Array;
                        if (docs != null)
                        {
                            foreach (object item in docs)
                            {
                                var doc = item as ModelDoc2;
                                if (doc == null) continue;
                                try { if (doc.Visible) visible++; } catch { }
                            }
                        }
                    }
                    catch { }
                }
                return new ResourceSnapshot
                {
                    GdiObjects = GetGuiResources(_process.Handle, GR_GDIOBJECTS),
                    UserObjects = GetGuiResources(_process.Handle, GR_USEROBJECTS),
                    WorkingSetMb = _process.WorkingSet64 / (1024 * 1024),
                    LoadedDocuments = loaded,
                    VisibleDocuments = visible
                };
            }
            catch { return new ResourceSnapshot(); }
        }

        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);
    }
}
