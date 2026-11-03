using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SrcCadWorks.Services
{
    // Yalnızca SrcCadWorks tarama/üretim işlemi sürerken aktiftir.
    // Bilinen SOLIDWORKS/PDM/CAM modal pencerelerinin otomasyonu kilitlemesini engeller.
    // Kullanıcının normal SOLIDWORKS kullanımında aktif değildir.
    internal sealed class ModalDialogGuard : IDisposable
    {
        private const uint BM_CLICK = 0x00F5;
        private const uint BM_GETCHECK = 0x00F0;
        private const uint BM_SETCHECK = 0x00F1;
        private const int BST_CHECKED = 1;
        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_CLOSE = 0x0010;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDNO = 7;

        private readonly Thread _thread;
        private readonly uint _solidWorksProcessId;
        private volatile bool _stop;
        private volatile bool _criticalResourceWarningSeen;
        private readonly Dictionary<IntPtr, DateTime> _lastAction = new Dictionary<IntPtr, DateTime>();

        public bool CriticalResourceWarningSeen => _criticalResourceWarningSeen;

        public ModalDialogGuard(Action requestCancel)
        {
            // requestCancel parametresi geriye dönük imza uyumluluğu için tutuluyor.
            // Kaynak uyarısı kullanıcı iptali değildir; ayrı flag ile bildirilir.
            _solidWorksProcessId = (uint)Process.GetCurrentProcess().Id;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SrcCadWorks Modal Guard" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Loop()
        {
            while (!_stop)
            {
                try { EnumWindows(InspectWindow, IntPtr.Zero); } catch { }
                Thread.Sleep(35);
            }
        }

        private bool InspectWindow(IntPtr hwnd, IntPtr lParam)
        {
            if (_stop || hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return true;

            string title = GetText(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);

            var children = new List<IntPtr>();
            try { EnumChildWindows(hwnd, (child, lp) => { children.Add(child); return true; }, IntPtr.Zero); } catch { }
            var all = new StringBuilder(title);
            foreach (var child in children)
            {
                string t = GetText(child);
                if (!string.IsNullOrWhiteSpace(t)) all.Append(" | ").Append(t);
            }
            string text = all.ToString();

            // Aynı pencere kapanana kadar tekrar deneyebiliriz; HWND yeniden kullanımı yüzünden "bir kere gördüm"
            // kilidi kullanmıyoruz. Ancak aynı pencereye çok hızlı mesaj yağdırmamak için kısa throttle uygulanır.
            if (!CanAct(hwnd, 120)) return true;

            // PDM "Kasadan Al / Check Out" penceresi: SrcCadWorks kaynak dosyaları salt-okunur kullanır,
            // bu yüzden checkout istemini otomatik iptal eder. Bu, SrcCadWorks üretimini iptal etmez; yalnız
            // PDM'de dosyanın kasadan alınmasını engeller.
            if (Contains(title, "Kasadan Al") || Contains(title, "Check Out") ||
                Contains(text, "Kasadan alınacak dosyalar") || Contains(text, "Files to check out"))
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "İptal", "Cancel")) SendCommand(hwnd, IDCANCEL);
                CloseIfStillVisible(hwnd, IDCANCEL);
                Logger.Info("PDM Kasadan Al penceresi SrcCadWorks işlemi sırasında otomatik iptal edildi; kaynak belge salt-okunur bırakıldı.");
                return true;
            }

            // SOLIDWORKS PDM: kasadan alınmamış salt-okunur kaynak belgede değişiklik uyarısı.
            // Bazı kurulumlarda pencere PDM yardımcı prosesine aittir; bu nedenle yalnız bu TAM metin deseni
            // için PID filtresi uygulanmaz.
            if ((Contains(title, "SOLIDWORKS PDM") || Contains(text, "SOLIDWORKS PDM")) &&
                (Contains(text, "kasadan alınmadı") || Contains(text, "not checked out")) &&
                (Contains(text, "değiştirilen dosya") || Contains(text, "modified file") || Contains(text, "değişikliklerin")))
            {
                HideKnownDialog(hwnd);
                SetCheck(children, "Bu iletiyi bir daha gösterme", "Do not show this message again");
                if (!ClickButton(children, "Tamam", "OK")) SendCommand(hwnd, IDOK);
                CloseIfStillVisible(hwnd, IDOK);
                Logger.Info("PDM 'kasadan alınmadı' uyarısı SrcCadWorks işlemi sırasında otomatik kapatıldı. PID=" + pid);
                return true;
            }

            // Bundan sonraki genel SOLIDWORKS/CAM pencereleri aynı SLDWORKS prosesine ait olmalıdır.
            if (pid != _solidWorksProcessId) return true;

            // DXF/DWG/PDF otomasyonu sırasında bazı SOLIDWORKS kurulumları Adobe Illustrator
            // bağımlılığıyla ilgili yalnızca bilgi amaçlı bir uyarı gösterebiliyor. SrcCadWorks işlemi
            // sırasında bu pencere otomasyonu kilitlemesin; OK/Tamam ile kapatıp üst katmanın gerçek
            // export sonucunu değerlendirmesine izin veriyoruz.
            bool illustratorNotice = Contains(text, "Adobe Illustrator") &&
                (Contains(text, "gerekli") || Contains(text, "gereklidir") || Contains(text, "required") ||
                 Contains(text, "kurulu") || Contains(text, "installed") || Contains(text, "bulunamadı") || Contains(text, "not found"));
            if (illustratorNotice)
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Tamam", "OK", "Kapat", "Close")) SendCommand(hwnd, IDOK);
                CloseIfStillVisible(hwnd, IDOK);
                Logger.Info("Adobe Illustrator gereksinim/bilgi uyarısı SrcCadWorks işlemi sırasında otomatik kapatıldı.");
                return true;
            }

            // SOLIDWORKS 'Kopya olarak kaydet ve aç / Save as Copy and Open' seçim penceresi.
            // Kaynak belgeyi kullanıcının oturumunda açık tutmak en güvenli seçenektir. Kalıcı
            // 'bir daha gösterme' ayarına dokunmadan yalnız mevcut SrcCadWorks işlemi için seçim yapılır.
            bool saveCopyAndOpen = Contains(text, "Kopya olarak kaydet ve aç") || Contains(text, "Save as Copy and Open");
            if (saveCopyAndOpen)
            {
                HideKnownDialog(hwnd);
                bool clicked = ClickButton(children, "Orijinal belgeyi açık tut", "Keep original document open");
                if (!clicked) clicked = ClickButtonByPrefix(children, "Orijinal belgeyi açık tut", "Keep original document open");
                if (!clicked) SendCommand(hwnd, IDOK);
                CloseIfStillVisible(hwnd, IDOK);
                Logger.Info("SOLIDWORKS 'Kopya olarak kaydet ve aç' penceresinde orijinal belgeyi açık tut seçeneği otomatik seçildi.");
                return true;
            }

            // SrcCadWorks işlemi sırasında SOLIDWORKS bazen geçici/eksik referans için
            // "C:\...\*.SLDPRT bulunamadı" benzeri yalnız Tamam içeren modal uyarı gösterebilir.
            // Bu uyarı bütün tarama/üretimi durdurmamalıdır. Pencere otomatik kapatılır; ilgili
            // parça/export çağrısı başarısızsa üst katmandaki mevcut hata yakalama o öğeyi raporlayıp devam eder.
            bool missingSolidWorksFile =
                (Contains(text, "bulunamadı") ||
                 Contains(text, "bulunamadi") ||
                 Contains(text, "not found") ||
                 Contains(text, "could not be found") ||
                 Contains(text, "cannot find")) &&
                (Contains(text, ".SLDPRT") ||
                 Contains(text, ".SLDASM") ||
                 Contains(text, ".SLDDRW") ||
                 Contains(text, @"\AppData\Local\Temp\") ||
                 Contains(text, @"\Temp\swx"));

            if (string.Equals(title.Trim(), "SOLIDWORKS", StringComparison.OrdinalIgnoreCase) &&
                missingSolidWorksFile)
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Tamam", "OK")) SendCommand(hwnd, IDOK);
                CloseIfStillVisible(hwnd, IDOK);
                Logger.Info("SOLIDWORKS eksik/geçici dosya uyarısı otomatik kapatıldı; SrcCadWorks işlemi devam ediyor. Mesaj=" + text);
                return true;
            }

            // SrcCadWorks pasif/suppressed bileşeni dosya üzerinden okumayı dener; montaj durumunu otomatik
            // değiştirmez. SOLIDWORKS "pasif bileşenleri aktifleştir/çözümle" diye sorarsa Hayır/İptal seçilir.
            bool passiveActivationPrompt =
                ((Contains(text, "pasif") || Contains(text, "bastırılmış") || Contains(text, "bastirilmis")) &&
                 (Contains(text, "aktifleştir") || Contains(text, "aktiflestir") || Contains(text, "çözümle") || Contains(text, "cozumle") || Contains(text, "etkinleştir") || Contains(text, "etkinlestir"))) ||
                ((Contains(text, "suppressed") || Contains(text, "lightweight")) &&
                 (Contains(text, "unsuppress") || Contains(text, "resolve") || Contains(text, "activate")));

            if (string.Equals(title.Trim(), "SOLIDWORKS", StringComparison.OrdinalIgnoreCase) && passiveActivationPrompt)
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Hayır", "No", "İptal", "Cancel")) SendCommand(hwnd, IDNO);
                CloseIfStillVisible(hwnd, IDNO);
                Logger.Info("Pasif/suppressed bileşenleri aktifleştirme sorusuna SrcCadWorks işlemi sırasında otomatik Hayır seçildi.");
                return true;
            }

            // SOLIDWORKS Resource Monitor / kritik pencere kaynağı. Bu pencere yalnız OK içerir; önceki
            // sürümde IDNO gönderildiği için ekranda kalabiliyordu. V1.10.2'de OK ile kapatılır; kaynaklar izlenir ama işlem otomatik durdurulmaz.
            if (Contains(text, "pencere kaynakları kritik") ||
                Contains(text, "kullanılabilir pencere kaynakları kritik") ||
                (Contains(text, "window resources") && Contains(text, "critical")))
            {
                HideKnownDialog(hwnd);
                _criticalResourceWarningSeen = true;
                if (!ClickButton(children, "Tamam", "OK")) SendCommand(hwnd, IDOK);
                CloseIfStillVisible(hwnd, IDOK);
                Logger.Info("SOLIDWORKS kritik pencere kaynağı uyarısı otomatik kapatıldı; SrcCadWorks işlemi kullanıcı iptal etmedikçe devam eder.");
                return true;
            }

            // Sheet-metal export sırasında SOLIDWORKS'un gösterebildiği bilinen bilgi uyarısı.
            if (Contains(text, "aktif bir yassı çoğalt") || Contains(text, "active flat pattern"))
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Tamam", "OK")) SendCommand(hwnd, IDOK);
                CloseIfStillVisible(hwnd, IDOK);
                Logger.Info("Aktif yassı çoğaltma bilgi uyarısı otomatik kapatıldı.");
                return true;
            }

            // SOLIDWORKS CAM, SrcCadWorks'in salt-okunur açtığı parçada kendi verisini değiştirilmiş sayabilir.
            // Bu kaynak belgeyi asla kaydetmiyoruz.
            if (Contains(title, "SOLIDWORKS CAM") &&
                (Contains(text, "kaydedilsin mi") || Contains(text, "save")))
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Hayır", "No")) SendCommand(hwnd, IDNO);
                CloseIfStillVisible(hwnd, IDNO);
                Logger.Info("SOLIDWORKS CAM kaydetme sorusuna SrcCadWorks işlemi sırasında otomatik Hayır seçildi.");
                return true;
            }

            // Çoklu değiştirilmiş belge ekranında doğrudan "Kaydetme / Don't Save" kullanılır.
            if (Contains(title, "Değiştirilmiş Belgeleri Kaydet") || Contains(title, "Save Modified Documents"))
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Kaydetme", "Don't Save", "Tümünü At", "Discard All", "Hayır", "No") &&
                    !ClickButtonByPrefix(children, "Kaydetme", "Don't Save", "Discard"))
                    SendCommand(hwnd, IDNO);
                CloseIfStillVisible(hwnd, IDNO);
                Logger.Info("Değiştirilmiş Belgeleri Kaydet penceresi SrcCadWorks işlemi sırasında kaydetmeden geçildi.");
                return true;
            }

            // Standart SOLIDWORKS kaydetme soruları. Yalnız SrcCadWorks işi sürerken guard var olduğundan kullanıcı
            // tarafından manuel yürütülen normal SOLIDWORKS oturumuna müdahale etmez.
            bool solidWorksSavePrompt =
                Contains(title, "SOLIDWORKS") &&
                (Contains(text, "kaydedilsin mi") ||
                 Contains(text, "kaydedilsin?") ||
                 Contains(text, "değişiklikler kaydedilsin") ||
                 Contains(text, "degisiklikler kaydedilsin") ||
                 Contains(text, "save changes") ||
                 Contains(text, "do you want to save"));

            if (solidWorksSavePrompt)
            {
                HideKnownDialog(hwnd);
                if (!ClickButton(children, "Hayır", "No", "Kaydetme", "Don't Save")) SendCommand(hwnd, IDNO);
                CloseIfStillVisible(hwnd, IDNO);
                Logger.Info("SOLIDWORKS kaydetme sorusuna SrcCadWorks işlemi sırasında otomatik Hayır seçildi.");
                return true;
            }

            return true;
        }

        private bool CanAct(IntPtr hwnd, int minimumIntervalMs)
        {
            lock (_lastAction)
            {
                DateTime last;
                DateTime now = DateTime.UtcNow;
                if (_lastAction.TryGetValue(hwnd, out last) && (now - last).TotalMilliseconds < minimumIntervalMs)
                    return false;
                _lastAction[hwnd] = now;

                if (_lastAction.Count > 256)
                {
                    var cutoff = now.AddSeconds(-5);
                    var remove = new List<IntPtr>();
                    foreach (var kv in _lastAction) if (kv.Value < cutoff || !IsWindow(kv.Key)) remove.Add(kv.Key);
                    foreach (var key in remove) _lastAction.Remove(key);
                }
                return true;
            }
        }

        private static bool SetCheck(IEnumerable<IntPtr> children, params string[] captions)
        {
            foreach (var child in children)
            {
                if (!string.Equals(GetClass(child), "Button", StringComparison.OrdinalIgnoreCase)) continue;
                string txt = NormalizeCaption(GetText(child));
                foreach (var caption in captions)
                {
                    if (txt.IndexOf(NormalizeCaption(caption), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Checkbox'a BM_CLICK göndermiyoruz; set ettikten sonra click toggle edip tekrar kapatabiliyordu.
                        SendTimeout(child, BM_SETCHECK, (IntPtr)BST_CHECKED, IntPtr.Zero, 150);
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ClickButton(IEnumerable<IntPtr> children, params string[] captions)
        {
            foreach (var child in children)
            {
                if (!string.Equals(GetClass(child), "Button", StringComparison.OrdinalIgnoreCase)) continue;
                string txt = NormalizeCaption(GetText(child));
                foreach (var caption in captions)
                {
                    if (string.Equals(txt, NormalizeCaption(caption), StringComparison.OrdinalIgnoreCase))
                    {
                        SendTimeout(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero, 200);
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ClickButtonByPrefix(IEnumerable<IntPtr> children, params string[] prefixes)
        {
            foreach (var child in children)
            {
                if (!string.Equals(GetClass(child), "Button", StringComparison.OrdinalIgnoreCase)) continue;
                string txt = NormalizeCaption(GetText(child));
                foreach (var prefix in prefixes)
                {
                    if (txt.StartsWith(NormalizeCaption(prefix), StringComparison.OrdinalIgnoreCase))
                    {
                        SendTimeout(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero, 200);
                        return true;
                    }
                }
            }
            return false;
        }

        private static void SendCommand(IntPtr hwnd, int commandId)
        {
            SendTimeout(hwnd, WM_COMMAND, (IntPtr)commandId, IntPtr.Zero, 200);
        }

        private static void HideKnownDialog(IntPtr hwnd)
        {
            try { if (IsWindow(hwnd)) ShowWindow(hwnd, SW_HIDE); } catch { }
        }

        private static void CloseIfStillVisible(IntPtr hwnd, int commandId)
        {
            Thread.Sleep(35);
            if (!IsWindow(hwnd)) return;
            SendCommand(hwnd, commandId);
            Thread.Sleep(45);
            if (IsWindow(hwnd)) SendTimeout(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, 180);
            Thread.Sleep(35);
            // Tanıdığımız pencere beklenmedik biçimde kapanmadıysa görünmez modal ile uygulamayı kilitlemeyelim.
            if (IsWindow(hwnd))
            {
                try { ShowWindow(hwnd, SW_SHOW); } catch { }
            }
        }

        private static string NormalizeCaption(string text)
        {
            return (text ?? "").Replace("&", "").Trim();
        }

        private static bool Contains(string source, string value)
        {
            return (source ?? "").IndexOf(value ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetText(IntPtr hwnd)
        {
            try
            {
                int len = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                var sb = new StringBuilder(Math.Max(64, len + 2));
                SendMessage(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string GetClass(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            try { GetClassName(hwnd, sb, sb.Capacity); } catch { }
            return sb.ToString();
        }

        private static void SendTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint timeoutMs)
        {
            try
            {
                IntPtr result;
                SendMessageTimeout(hwnd, message, wParam, lParam, SMTO_ABORTIFHUNG, timeoutMs, out result);
            }
            catch { }
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null && _thread.IsAlive) _thread.Join(350); } catch { }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
