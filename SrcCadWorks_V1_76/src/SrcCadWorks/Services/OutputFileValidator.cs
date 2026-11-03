using System;
using System.IO;
using System.Text;

namespace SrcCadWorks.Services
{
    internal static class OutputFileValidator
    {
        public static bool IsUsable(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                var fi = new FileInfo(path);
                if (fi.Length <= 16) return false;
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ext == ".pdf") return IsPdfUsable(path, fi.Length);
                if (ext == ".dxf") return fi.Length > 64;
                if (ext == ".dwg") return fi.Length > 64;
                return fi.Length > 0;
            }
            catch { return false; }
        }

        private static bool IsPdfUsable(string path, long length)
        {
            if (length < 128) return false;
            using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[5];
                if (fs.Read(head, 0, head.Length) != head.Length) return false;
                if (Encoding.ASCII.GetString(head) != "%PDF-") return false;
                int tailSize = (int)Math.Min(4096, length);
                fs.Seek(-tailSize, SeekOrigin.End);
                var tail = new byte[tailSize];
                int read = fs.Read(tail, 0, tail.Length);
                string tailText = Encoding.ASCII.GetString(tail, 0, read);
                return tailText.IndexOf("%%EOF", StringComparison.Ordinal) >= 0;
            }
        }
    }
}
