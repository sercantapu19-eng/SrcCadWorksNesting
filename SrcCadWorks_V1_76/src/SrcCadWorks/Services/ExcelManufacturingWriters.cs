using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// İmalat ve Talaşlı İmalat Excel yazıcısı.
    /// Excel COM otomasyonuna bağlı değildir; XLSX şablonunu doğrudan OpenXML
    /// paket yapısı üzerinden günceller. Böylece 0x80010114 / RPC_COM hataları
    /// ve yarım .src_tmp dosyaları oluşmaz.
    /// </summary>
    internal static class ExcelManufacturingWriters
    {
        private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace XmlNs = XNamespace.Xml;

        public static void CreateManufacturing(string templatePath, string outputPath, ProcessProjectInfo info, IList<ManufacturingRow> rows)
        {
            ValidatePaths(templatePath, outputPath);
            PrepareOutput(templatePath, outputPath);

            var data = rows ?? new List<ManufacturingRow>();
            ProcessProjectInfo x = info ?? new ProcessProjectInfo();

            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                XDocument sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");

                SetText(sheet, "C7", x.ProjectName);
                SetText(sheet, "C8", x.FileName);
                SetText(sheet, "C9", x.FileCode);
                SetText(sheet, "C10", x.Designer);
                SetText(sheet, "C11", x.Preparer);
                SetText(sheet, "C12", x.Approver);
                SetText(sheet, "A13", string.IsNullOrWhiteSpace(x.Note) ? "NOT:" : "NOT: " + x.Note.Trim());
                SetText(sheet, "G1", DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));

                ClearExistingData(sheet, 15, 5000, 1, 7);
                int count = data.Count;
                for (int i = 0; i < count; i++)
                {
                    ManufacturingRow row = data[i] ?? new ManufacturingRow();
                    int r = 15 + i;
                    EnsureStyledRow(sheet, r, 15, 7);
                    SetNumber(sheet, "A" + r, i + 1);
                    SetText(sheet, "B" + r, row.PartCode);
                    SetNumber(sheet, "C" + r, row.Quantity);
                    SetText(sheet, "D" + r, row.ProfileType);
                    SetText(sheet, "E" + r, row.ColorCode);
                    SetText(sheet, "F" + r, string.IsNullOrWhiteSpace(row.Route) ? "TESTERE" : row.Route);
                    SetText(sheet, "G" + r, "");
                }

                int lastDataRow = 14 + Math.Max(1, count);
                UpdateDimension(sheet, "A1:G" + Math.Max(26, lastDataRow));
                SaveXml(archive, "xl/worksheets/sheet1.xml", sheet);
                UpdateTableRef(archive, "A14:G" + lastDataRow);
            }

            ValidateSavedOutput(outputPath, "İmalat Excel");
        }

        public static void CreateMachining(string templatePath, string outputPath, ProcessProjectInfo info, IList<MachiningRow> roots)
        {
            ValidatePaths(templatePath, outputPath);
            PrepareOutput(templatePath, outputPath);

            var data = roots ?? new List<MachiningRow>();
            ProcessProjectInfo x = info ?? new ProcessProjectInfo();

            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                XDocument sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");

                SetText(sheet, "C8", x.ProjectName);
                SetText(sheet, "C9", x.FileName);
                SetText(sheet, "C10", x.FileCode);
                SetText(sheet, "C11", x.Designer);
                SetText(sheet, "C12", x.Preparer);
                SetText(sheet, "C13", x.Approver);
                SetText(sheet, "C14", x.RequestReason);
                SetText(sheet, "A15", string.IsNullOrWhiteSpace(x.Note) ? "NOT:" : "NOT: " + x.Note.Trim());
                SetText(sheet, "E1", "Tarih: " + DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));

                ClearExistingData(sheet, 17, 5000, 1, 5);
                int count = data.Count;
                for (int i = 0; i < count; i++)
                {
                    MachiningRow row = data[i] ?? new MachiningRow();
                    int r = 17 + i;
                    EnsureStyledRow(sheet, r, 17, 5);
                    SetNumber(sheet, "A" + r, i + 1);
                    SetText(sheet, "B" + r, row.PartCode);
                    SetText(sheet, "C" + r, string.IsNullOrWhiteSpace(row.FolderName) ? row.ProductDescription : row.FolderName);
                    SetNumber(sheet, "D" + r, row.Quantity);
                    SetText(sheet, "E" + r, "");
                }

                int lastDataRow = 16 + Math.Max(1, count);
                UpdateDimension(sheet, "A1:E" + Math.Max(26, lastDataRow));
                SaveXml(archive, "xl/worksheets/sheet1.xml", sheet);
                UpdateTableRef(archive, "A16:E" + lastDataRow);
            }

            ValidateSavedOutput(outputPath, "Talaşlı İmalat Excel");
        }

        public static void UpdateManufacturingOutputNotes(string outputPath, IList<ManufacturingRow> rows, IDictionary<string, string> outputNotes)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath)) return;
            var data = rows ?? new List<ManufacturingRow>();
            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                XDocument sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");
                for (int i = 0; i < data.Count; i++)
                {
                    ManufacturingRow row = data[i] ?? new ManufacturingRow();
                    int r = 15 + i;
                    SetText(sheet, "G" + r, OutputNote(row.PartCode, outputNotes));
                }
                SaveXml(archive, "xl/worksheets/sheet1.xml", sheet);
            }
        }

        public static void UpdateMachiningOutputNotes(string outputPath, IList<MachiningRow> roots, IDictionary<string, string> outputNotes)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath)) return;
            var data = roots ?? new List<MachiningRow>();
            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                XDocument sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");
                for (int i = 0; i < data.Count; i++)
                {
                    MachiningRow row = data[i] ?? new MachiningRow();
                    int r = 17 + i;
                    SetText(sheet, "E" + r, OutputNote(row.PartCode, outputNotes));
                }
                SaveXml(archive, "xl/worksheets/sheet1.xml", sheet);
            }
        }

        private static string OutputNote(string partCode, IDictionary<string, string> outputNotes)
        {
            if (outputNotes == null || string.IsNullOrWhiteSpace(partCode)) return "";
            string note;
            return outputNotes.TryGetValue(partCode.Trim(), out note) ? (note ?? "").Trim() : "";
        }

        private static void PrepareOutput(string templatePath, string outputPath)
        {
            string dir = Path.GetDirectoryName(outputPath);
            Directory.CreateDirectory(string.IsNullOrWhiteSpace(dir) ? System.Environment.CurrentDirectory : dir);
            CleanupLegacyTemp(outputPath);
            TryDelete(outputPath);
            File.Copy(templatePath, outputPath, true);
        }

        private static XDocument LoadXml(ZipArchive archive, string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName);
            if (entry == null) throw new InvalidDataException("Excel şablonunda " + entryName + " bulunamadı.");
            using (Stream stream = entry.Open()) return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private static void SaveXml(ZipArchive archive, string entryName, XDocument doc)
        {
            ZipArchiveEntry old = archive.GetEntry(entryName);
            if (old != null) old.Delete();
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream stream = entry.Open()) doc.Save(stream, SaveOptions.DisableFormatting);
        }

        private static void UpdateTableRef(ZipArchive archive, string reference)
        {
            ZipArchiveEntry entry = archive.GetEntry("xl/tables/table1.xml");
            if (entry == null) return;
            XDocument tableDoc;
            using (Stream stream = entry.Open()) tableDoc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            XElement table = tableDoc.Root;
            if (table != null) table.SetAttributeValue("ref", reference);
            SaveXml(archive, "xl/tables/table1.xml", tableDoc);
        }

        private static void UpdateDimension(XDocument sheet, string reference)
        {
            XElement dimension = sheet.Root == null ? null : sheet.Root.Element(Ns + "dimension");
            if (dimension != null) dimension.SetAttributeValue("ref", reference);
        }

        private static void ClearExistingData(XDocument sheet, int startRow, int endRow, int startColumn, int endColumn)
        {
            XElement sheetData = GetSheetData(sheet);
            foreach (XElement row in sheetData.Elements(Ns + "row").ToList())
            {
                int rowNo = AttrInt(row, "r");
                if (rowNo < startRow || rowNo > endRow) continue;
                foreach (XElement cell in row.Elements(Ns + "c").ToList())
                {
                    string reference = (string)cell.Attribute("r") ?? "";
                    int col = ColumnNumber(reference);
                    if (col >= startColumn && col <= endColumn) ClearCellValue(cell);
                }
            }
        }

        private static void SetText(XDocument sheet, string reference, string value)
        {
            XElement cell = EnsureCell(sheet, reference);
            ClearCellValue(cell);
            cell.SetAttributeValue("t", "inlineStr");
            XElement text = new XElement(Ns + "t", value ?? "");
            if (!string.IsNullOrEmpty(value) && (value.StartsWith(" ", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal)))
                text.SetAttributeValue(XmlNs + "space", "preserve");
            cell.Add(new XElement(Ns + "is", text));
        }

        private static void SetNumber(XDocument sheet, string reference, int value)
        {
            XElement cell = EnsureCell(sheet, reference);
            ClearCellValue(cell);
            cell.SetAttributeValue("t", null);
            cell.Add(new XElement(Ns + "v", value.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ClearCellValue(XElement cell)
        {
            if (cell == null) return;
            cell.SetAttributeValue("t", null);
            foreach (XElement child in cell.Elements().Where(e => e.Name == Ns + "v" || e.Name == Ns + "f" || e.Name == Ns + "is").ToList())
                child.Remove();
        }

        private static XElement EnsureCell(XDocument sheet, string reference)
        {
            int rowNo = RowNumber(reference);
            int colNo = ColumnNumber(reference);
            XElement row = EnsureStyledRow(sheet, rowNo, rowNo, Math.Max(1, colNo));
            XElement cell = row.Elements(Ns + "c").FirstOrDefault(c => string.Equals((string)c.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
            if (cell != null) return cell;

            cell = new XElement(Ns + "c", new XAttribute("r", reference));
            XElement before = row.Elements(Ns + "c").FirstOrDefault(c => ColumnNumber((string)c.Attribute("r") ?? "") > colNo);
            if (before != null) before.AddBeforeSelf(cell); else row.Add(cell);
            return cell;
        }

        private static XElement EnsureStyledRow(XDocument sheet, int rowNo, int templateRowNo, int columnCount)
        {
            XElement sheetData = GetSheetData(sheet);
            XElement existing = sheetData.Elements(Ns + "row").FirstOrDefault(r => AttrInt(r, "r") == rowNo);
            if (existing != null)
            {
                EnsureCells(existing, rowNo, columnCount, null);
                return existing;
            }

            XElement template = sheetData.Elements(Ns + "row").FirstOrDefault(r => AttrInt(r, "r") == templateRowNo);
            XElement row;
            if (template != null)
            {
                row = new XElement(template);
                row.SetAttributeValue("r", rowNo);
                foreach (XElement cell in row.Elements(Ns + "c"))
                {
                    int col = ColumnNumber((string)cell.Attribute("r") ?? "");
                    cell.SetAttributeValue("r", ColumnName(col) + rowNo);
                    ClearCellValue(cell);
                }
            }
            else
            {
                row = new XElement(Ns + "row", new XAttribute("r", rowNo));
            }
            EnsureCells(row, rowNo, columnCount, template);

            XElement before = sheetData.Elements(Ns + "row").FirstOrDefault(r => AttrInt(r, "r") > rowNo);
            if (before != null) before.AddBeforeSelf(row); else sheetData.Add(row);
            return row;
        }

        private static void EnsureCells(XElement row, int rowNo, int columnCount, XElement template)
        {
            for (int col = 1; col <= columnCount; col++)
            {
                string reference = ColumnName(col) + rowNo;
                if (row.Elements(Ns + "c").Any(c => string.Equals((string)c.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase))) continue;

                XElement sample = template == null ? null : template.Elements(Ns + "c").FirstOrDefault(c => ColumnNumber((string)c.Attribute("r") ?? "") == col);
                XElement cell = sample == null ? new XElement(Ns + "c") : new XElement(sample);
                cell.SetAttributeValue("r", reference);
                ClearCellValue(cell);

                XElement before = row.Elements(Ns + "c").FirstOrDefault(c => ColumnNumber((string)c.Attribute("r") ?? "") > col);
                if (before != null) before.AddBeforeSelf(cell); else row.Add(cell);
            }
        }

        private static XElement GetSheetData(XDocument sheet)
        {
            if (sheet.Root == null) throw new InvalidDataException("Excel çalışma sayfası XML'i geçersiz.");
            XElement data = sheet.Root.Element(Ns + "sheetData");
            if (data == null)
            {
                data = new XElement(Ns + "sheetData");
                sheet.Root.Add(data);
            }
            return data;
        }

        private static int RowNumber(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return 0;
            int i = 0;
            while (i < reference.Length && char.IsLetter(reference[i])) i++;
            int value;
            return int.TryParse(reference.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static int ColumnNumber(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return 0;
            int value = 0;
            foreach (char ch in reference)
            {
                if (!char.IsLetter(ch)) break;
                value = value * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return value;
        }

        private static string ColumnName(int column)
        {
            if (column <= 0) return "A";
            string result = "";
            while (column > 0)
            {
                column--;
                result = (char)('A' + (column % 26)) + result;
                column /= 26;
            }
            return result;
        }

        private static int AttrInt(XElement element, string name)
        {
            int value;
            return int.TryParse((string)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static void ValidatePaths(string templatePath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
                throw new FileNotFoundException("Excel şablonu bulunamadı. SrcCadWorks templates klasörünü kontrol edin.", templatePath);
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Excel çıktı yolu boş.", nameof(outputPath));
        }

        private static void ValidateSavedOutput(string outputPath, string title)
        {
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new InvalidOperationException(title + " çıktı dosyası doğrulanamadı.");
            try
            {
                using (var archive = ZipFile.OpenRead(outputPath))
                {
                    if (archive.GetEntry("xl/worksheets/sheet1.xml") == null)
                        throw new InvalidDataException("sheet1.xml bulunamadı.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(title + " XLSX paketi doğrulanamadı: " + ex.Message, ex);
            }
        }

        private static void CleanupLegacyTemp(string outputPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (string.IsNullOrWhiteSpace(dir)) return;
                string ext = Path.GetExtension(outputPath);
                string legacy = Path.Combine(dir, "." + Path.GetFileNameWithoutExtension(outputPath) + ".src_tmp" + ext);
                TryDelete(legacy);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
