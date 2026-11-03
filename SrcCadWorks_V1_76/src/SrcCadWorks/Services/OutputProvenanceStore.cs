using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    [Serializable]
    public sealed class OutputProvenanceRecord
    {
        public int Version { get; set; } = 1;
        public string OutputPath { get; set; }
        public string OutputKind { get; set; }
        public string SourcePath { get; set; }
        public string Configuration { get; set; }
        public string PartCode { get; set; }
        public string SourceSha256 { get; set; }
        public long SourceLength { get; set; }
        public long SourceLastWriteUtcTicks { get; set; }
        public string OptionsSignature { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    /// <summary>
    /// A previous output is reusable only when it was created from the exact same
    /// source bytes + source path + configuration + relevant export options.
    /// Part code alone is NEVER accepted as cache identity.
    /// </summary>
    internal static class OutputProvenanceStore
    {
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "output-state-v1");

        public static string ComputeFileSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Kaynak fingerprint alınamadı; güvenlik için cache kullanılmayacak: " + path + " | " + ex.Message);
                return "";
            }
        }

        public static string BuildRecoveryKey(LaserPartRow row)
        {
            if (row == null) return "";
            string hash = ComputeFileSha256(row.SourcePath);
            if (string.IsNullOrWhiteSpace(hash)) return "";
            return Normalize(row.PartCode) + "|" + NormalizePath(row.SourcePath) + "|" +
                   Normalize(row.Configuration) + "|" + hash;
        }

        public static bool IsCurrent(string outputPath, string outputKind, string sourcePath,
            string configuration, string partCode, string optionsSignature)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath)) return false;
                if (!OutputFileValidator.IsUsable(outputPath)) return false;
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return false;

                OutputProvenanceRecord record = Load(outputPath);
                if (record == null || record.Version != 1) return false;
                if (!string.Equals(NormalizePath(record.OutputPath), NormalizePath(outputPath), StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(Normalize(record.OutputKind), Normalize(outputKind), StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(NormalizePath(record.SourcePath), NormalizePath(sourcePath), StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(Normalize(record.Configuration), Normalize(configuration), StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(Normalize(record.PartCode), Normalize(partCode), StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(record.OptionsSignature ?? "", optionsSignature ?? "", StringComparison.Ordinal)) return false;

                string currentHash = ComputeFileSha256(sourcePath);
                if (string.IsNullOrWhiteSpace(currentHash)) return false;
                return string.Equals(record.SourceSha256 ?? "", currentHash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void MarkCurrent(string outputPath, string outputKind, string sourcePath,
            string configuration, string partCode, string optionsSignature)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath)) return;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;

            string hash = ComputeFileSha256(sourcePath);
            if (string.IsNullOrWhiteSpace(hash)) return;

            try
            {
                var fi = new FileInfo(sourcePath);
                var record = new OutputProvenanceRecord
                {
                    OutputPath = Path.GetFullPath(outputPath),
                    OutputKind = outputKind ?? "",
                    SourcePath = Path.GetFullPath(sourcePath),
                    Configuration = configuration ?? "",
                    PartCode = partCode ?? "",
                    SourceSha256 = hash,
                    SourceLength = fi.Length,
                    SourceLastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    OptionsSignature = optionsSignature ?? "",
                    SavedUtc = DateTime.UtcNow
                };

                Directory.CreateDirectory(Root);
                string path = MetadataPath(outputPath);
                string temp = path + ".tmp";
                using (var stream = File.Create(temp))
                    new XmlSerializer(typeof(OutputProvenanceRecord)).Serialize(stream, record);

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Logger.Info("Çıktı provenance kaydı yazılamadı: " + outputPath + " | " + ex.Message);
            }
        }

        public static void DeleteForOutput(string outputPath)
        {
            try
            {
                string path = MetadataPath(outputPath);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
            catch { }
        }

        private static OutputProvenanceRecord Load(string outputPath)
        {
            try
            {
                string path = MetadataPath(outputPath);
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return (OutputProvenanceRecord)new XmlSerializer(typeof(OutputProvenanceRecord)).Deserialize(stream);
            }
            catch
            {
                return null;
            }
        }

        private static string MetadataPath(string outputPath)
        {
            string normalized = NormalizePath(outputPath);
            string key;
            using (var sha = SHA256.Create())
            {
                byte[] data = Encoding.UTF8.GetBytes(normalized.ToUpperInvariant());
                key = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
            return Path.Combine(Root, key + ".xml");
        }

        private static string NormalizePath(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path).Trim(); }
            catch { return (path ?? "").Trim(); }
        }

        private static string Normalize(string text)
        {
            return (text ?? "").Trim();
        }
    }
}
