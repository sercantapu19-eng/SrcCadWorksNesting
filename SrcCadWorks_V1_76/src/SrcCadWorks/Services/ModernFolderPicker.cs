using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// Windows Vista+ modern klasör seçicisini açar. Eski FolderBrowserDialog'dan farklı olarak
    /// Windows Gezgini'nin sol gezinme panelini (Hızlı Erişim/Giriş, Masaüstü, Bu Bilgisayar vb.)
    /// gösterir; böylece kullanıcının sabitlediği klasörler de görünür.
    /// </summary>
    internal static class ModernFolderPicker
    {
        private const int HResultCancelled = unchecked((int)0x800704C7);

        [Flags]
        private enum FileOpenOptions : uint
        {
            PickFolders = 0x00000020,
            ForceFileSystem = 0x00000040,
            NoChangeDir = 0x00000008,
            PathMustExist = 0x00000800
        }

        private enum Sigdn : uint
        {
            FileSystemPath = 0x80058000
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom
        {
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr parent);

            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(FileOpenOptions fos);
            void GetOptions(out FileOpenOptions pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName(out IntPtr pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(Sigdn sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        public static bool TryPickFolder(IntPtr ownerHandle, string initialPath, string title, out string selectedPath, out bool cancelled)
        {
            selectedPath = null;
            cancelled = false;
            IFileDialog dialog = null;
            IShellItem initialItem = null;
            IShellItem resultItem = null;

            try
            {
                dialog = (IFileDialog)new FileOpenDialogCom();

                FileOpenOptions current;
                dialog.GetOptions(out current);
                dialog.SetOptions(current |
                                  FileOpenOptions.PickFolders |
                                  FileOpenOptions.ForceFileSystem |
                                  FileOpenOptions.NoChangeDir |
                                  FileOpenOptions.PathMustExist);

                if (!string.IsNullOrWhiteSpace(title))
                    dialog.SetTitle(title);
                dialog.SetOkButtonLabel("Klasörü Seç");

                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                {
                    Guid shellItemGuid = typeof(IShellItem).GUID;
                    int hrCreate = SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref shellItemGuid, out initialItem);
                    if (hrCreate >= 0 && initialItem != null)
                        dialog.SetFolder(initialItem);
                }

                int hr = dialog.Show(ownerHandle);
                if (hr == HResultCancelled)
                {
                    cancelled = true;
                    return false;
                }
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                dialog.GetResult(out resultItem);
                if (resultItem == null)
                    return false;

                IntPtr pszPath = IntPtr.Zero;
                try
                {
                    resultItem.GetDisplayName(Sigdn.FileSystemPath, out pszPath);
                    selectedPath = pszPath == IntPtr.Zero ? null : Marshal.PtrToStringUni(pszPath);
                }
                finally
                {
                    if (pszPath != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pszPath);
                }

                return !string.IsNullOrWhiteSpace(selectedPath);
            }
            finally
            {
                if (resultItem != null && Marshal.IsComObject(resultItem)) Marshal.FinalReleaseComObject(resultItem);
                if (initialItem != null && Marshal.IsComObject(initialItem)) Marshal.FinalReleaseComObject(initialItem);
                if (dialog != null && Marshal.IsComObject(dialog)) Marshal.FinalReleaseComObject(dialog);
            }
        }
    }
}
