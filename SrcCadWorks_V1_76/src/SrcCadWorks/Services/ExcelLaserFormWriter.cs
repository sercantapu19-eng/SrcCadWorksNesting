using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class ExcelLaserFormWriter
    {
        public void CreateFromTemplate(string templatePath, string outputPath, LaserProjectInfo info, IList<LaserPartRow> rows, IDictionary<LaserPartRow, string> outputNotes = null)
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Lazer formu şablonu bulunamadı.", templatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.Copy(templatePath, outputPath, true);

            Type excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null) throw new InvalidOperationException("Microsoft Excel masaüstü uygulaması bulunamadı.");

            dynamic excel = null, workbook = null, sheet = null, table = null;
            try
            {
                excel = Activator.CreateInstance(excelType);
                excel.Visible = false;
                excel.DisplayAlerts = false;
                workbook = excel.Workbooks.Open(outputPath);
                sheet = workbook.Worksheets["Sayfa1"];

                SetIfNotBlank(sheet, "C8", info.ProjectName);
                SetIfNotBlank(sheet, "C9", info.FileName);
                SetIfNotBlank(sheet, "C10", info.FileCode);
                SetIfNotBlank(sheet, "C11", info.Designer);
                SetIfNotBlank(sheet, "C12", info.Preparer);
                SetIfNotBlank(sheet, "C13", info.RequestReason);
                SetIfNotBlank(sheet, "A14", info.Note, keepPrefix: true);
                // V1.12.20: Tarihi gerçek Excel tarih değeri olarak yaz ve şablondaki
                // yerleşik Kısa Tarih biçimini koru. NumberFormat'ı özel bir metinle
                // ezmediğimiz için Hücreleri Biçimlendir > Tarih kategorisinde kalır.
                dynamic dateCell = sheet.Range["H1"];
                dateCell.Value2 = DateTime.Today.ToOADate();

                // Eski satır içeriğini temizle, biçim/table yapısını koru.
                sheet.Range["A16:H5000"].ClearContents();

                int count = rows.Count;
                int lastRow = 15 + Math.Max(1, count);
                table = sheet.ListObjects["Tablo4"];
                table.Resize(sheet.Range["A15:H" + lastRow]);

                if (count > 0)
                {
                    var values = new object[count, 8];
                    for (int i = 0; i < count; i++)
                    {
                        var r = rows[i];
                        values[i, 0] = i + 1;
                        values[i, 1] = r.PartCode;
                        values[i, 2] = r.Quantity;
                        values[i, 3] = r.ThicknessText;
                        values[i, 4] = r.Operation ?? "";
                        values[i, 5] = r.ColorCode ?? "";
                        values[i, 6] = r.WeldProcess ?? "";
                        string outputNote = "";
                        if (outputNotes != null) outputNotes.TryGetValue(r, out outputNote);
                        values[i, 7] = CombineDescription(r.Description, outputNote);
                    }
                    sheet.Range["A16:H" + (15 + count)].Value2 = values;
                    // Longer HAVSA combinations must remain readable in the existing form width.
                    for (int i = 0; i < count; i++)
                    {
                        if ((rows[i].Operation ?? "").IndexOf("HAVŞA", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        dynamic operationCell = null, entireRow = null;
                        try
                        {
                            operationCell = sheet.Cells[16 + i, 5];
                            entireRow = operationCell.EntireRow;
                            double oldHeight = Convert.ToDouble(entireRow.RowHeight);
                            operationCell.WrapText = true;
                            entireRow.AutoFit();
                            if (Convert.ToDouble(entireRow.RowHeight) < oldHeight) entireRow.RowHeight = oldHeight;
                        }
                        finally { FinalRelease(entireRow); FinalRelease(operationCell); }
                    }
                }

                workbook.Save();
                workbook.Close(true);
                workbook = null;
                excel.Quit();
                excel = null;
            }
            finally
            {
                FinalRelease(table);
                FinalRelease(sheet);
                FinalRelease(workbook);
                if (excel != null) { try { excel.Quit(); } catch { } }
                FinalRelease(excel);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }


        private static string CombineDescription(string description, string outputNote)
        {
            string left = (description ?? "").Trim();
            string right = (outputNote ?? "").Trim();
            if (string.IsNullOrWhiteSpace(left)) return right;
            if (string.IsNullOrWhiteSpace(right)) return left;
            if (left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0) return left;
            return left.TrimEnd() + " | " + right;
        }

        private static void SetIfNotBlank(dynamic sheet, string cell, string value, bool keepPrefix = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (keepPrefix)
            {
                var prefix = Convert.ToString(sheet.Range[cell].Value2) ?? "";
                sheet.Range[cell].Value2 = string.IsNullOrWhiteSpace(prefix) ? value : prefix + " " + value;
            }
            else sheet.Range[cell].Value2 = value;
        }

        private static void FinalRelease(object obj)
        {
            if (obj == null) return;
            try { if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); } catch { }
        }
    }
}
