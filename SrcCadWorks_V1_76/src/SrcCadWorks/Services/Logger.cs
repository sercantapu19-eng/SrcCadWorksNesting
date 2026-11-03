using System;
using System.IO;
using System.Text;

namespace SrcCadWorks.Services
{
    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "logs");

        public static void Info(string message) => Write("INFO", message, null);
        public static void Error(string message, Exception ex) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                var path = Path.Combine(LogFolder, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').AppendLine(message ?? "");
                if (ex != null) sb.AppendLine(ex.ToString());
                lock (Gate) File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
