using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SrcCadWorks.Services
{
    public sealed class ResumeFile
    {
        public string Path { get; set; }
        public string InputHash { get; set; }
        public string OutputHash { get; set; }
    }
    public sealed class ResumeState
    {
        public int Version { get; set; } = 1;
        public string Identity { get; set; }
        public string RunId { get; set; }
        public bool Pending { get; set; }
        public string OutputFolder { get; set; }
        public Dictionary<string, string> Controls { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> Rows { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, ResumeFile> Files { get; set; } = new Dictionary<string, ResumeFile>();
        public Dictionary<string, string> Notes { get; set; } = new Dictionary<string, string>();
        public List<string> Errors { get; set; } = new List<string>();
    }
    // No SOLIDWORKS/COM/UI objects are ever stored in this journal.
    internal sealed class ResumeLedger
    {
        private readonly string _path, _identity;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        public ResumeState State { get; private set; }
        public bool Resuming { get; private set; }
        public ResumeLedger(string identity, string directory = null)
        {
            _identity = identity;
            _path = string.IsNullOrEmpty(identity) ? null : Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "resume"), Hash(identity) + ".json");
            State = Read(_path) ?? Read(_path == null ? null : _path + ".bak") ?? Fresh();
            if (State.Version != 1 || State.Identity != identity || State.Controls == null || State.Rows == null || State.Files == null || State.Notes == null || State.Errors == null) State = Fresh();
        }
        private ResumeState Fresh() => new ResumeState { Identity = _identity };
        private static ResumeState Read(string path)
        {
            try { return File.Exists(path) ? Json.Deserialize<ResumeState>(File.ReadAllText(path)) : null; } catch { return null; }
        }
        public void Begin(string folder, bool resume, Dictionary<string, string> controls, Dictionary<string, Dictionary<string, string>> rows)
        {
            Resuming = resume && State.Pending;
            if (!Resuming) State = Fresh();
            State.RunId = State.RunId ?? Guid.NewGuid().ToString("N");
            State.Pending = true;
            State.OutputFolder = Resuming ? State.OutputFolder : folder;
            State.Controls = controls; State.Rows = rows;
            Save();
        }
        public bool Reuse(string key, string path, string inputHash)
        {
            ResumeFile file;
            return Resuming && !string.IsNullOrEmpty(inputHash) && State.Files.TryGetValue(key, out file) &&
                string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase) && file.InputHash == inputHash &&
                Complete(path) && !string.IsNullOrEmpty(file.OutputHash) && file.OutputHash == FileHash(path);
        }
        public void Invalidate(string key) { State.Files.Remove(key); Save(); }
        public void Commit(string key, string path, string inputHash)
        {
            if (!Complete(path)) throw new InvalidDataException("Çıktı doğrulanamadı: " + path);
            State.Files[key] = new ResumeFile { Path = path, InputHash = inputHash ?? "", OutputHash = FileHash(path) };
            Save();
        }
        public void Finish(bool success) { State.Pending = !success; Resuming = false; Save(); }
        public void Save()
        {
            if (_path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(State));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak"); else File.Move(temp, _path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
        }
        public static string FileHash(string path)
        {
            try { using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); } catch { return ""; }
        }
        public static bool Complete(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < 32) return false;
                using (var stream = File.OpenRead(path))
                {
                    byte[] head = new byte[(int)Math.Min(128, stream.Length)]; stream.Read(head, 0, head.Length);
                    byte[] tail = new byte[(int)Math.Min(4096, stream.Length)]; stream.Seek(-tail.Length, SeekOrigin.End); stream.Read(tail, 0, tail.Length);
                    string h = Encoding.ASCII.GetString(head), t = Encoding.ASCII.GetString(tail);
                    switch (Path.GetExtension(path).ToLowerInvariant())
                    {
                        case ".pdf": return h.StartsWith("%PDF-") && t.Contains("%%EOF");
                        case ".step": case ".stp": return h.Contains("ISO-10303-21;") && t.Contains("END-ISO-10303-21;");
                        case ".dxf": return t.Contains("EOF");
                        case ".xlsx": using (var zip = System.IO.Compression.ZipFile.OpenRead(path)) return zip.GetEntry("xl/workbook.xml") != null;
                        default: return true;
                    }
                }
            }
            catch { return false; }
        }
    }
}
