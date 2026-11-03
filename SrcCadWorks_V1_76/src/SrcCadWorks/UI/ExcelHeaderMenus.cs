using System;
using System.Drawing;
using System.Windows.Forms;

namespace SrcCadWorks.UI
{
    internal static class ExcelHeaderMenus
    {
        internal static int ArrowWidth(DataGridView grid)
        {
            return Math.Max(24, (int)Math.Ceiling(24.0 * grid.DeviceDpi / 96.0));
        }

        internal static void Attach(DataGridView grid, Func<string, bool> hasMenu, Func<string, int> direction)
        {
            Action fitHeaders = () =>
            {
                int arrow = ArrowWidth(grid);
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    if (!hasMenu(column.DataPropertyName)) continue;
                    Font font = column.HeaderCell.InheritedStyle.Font ?? grid.Font;
                    int textWidth = TextRenderer.MeasureText(column.HeaderText, font,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                    // Başlığın tamamı + ayrı menü düğmesi: küçük sütunlarda da metin ezilmez.
                    column.MinimumWidth = Math.Max(column.MinimumWidth, textWidth + arrow + 12);
                    if (column.Width < column.MinimumWidth) column.Width = column.MinimumWidth;
                }
                grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
                grid.ColumnHeadersHeight = Math.Max(grid.ColumnHeadersHeight,
                    TextRenderer.MeasureText("Ag", grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font).Height + 12);
                grid.Invalidate();
            };
            fitHeaders();
            grid.HandleCreated += (s, e) => fitHeaders();
            grid.FontChanged += (s, e) => fitHeaders();
            grid.DpiChangedAfterParent += (s, e) => fitHeaders();
            grid.CellPainting += (s, e) =>
            {
                if (e.RowIndex != -1 || e.ColumnIndex < 0) return;
                var column = grid.Columns[e.ColumnIndex];
                string property = column.DataPropertyName;
                if (!hasMenu(property)) return;
                // Native metin/glyph çizilmez; metin ve ok birbirinden bağımsız iki alanda çizilir.
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
                int width = ArrowWidth(grid);
                var button = new Rectangle(e.CellBounds.Right - width + 2, e.CellBounds.Top + 5,
                    width - 5, e.CellBounds.Height - 10);
                var text = new Rectangle(e.CellBounds.Left + 5, e.CellBounds.Top + 2,
                    Math.Max(0, button.Left - e.CellBounds.Left - 9), e.CellBounds.Height - 4);
                using (var brush = new SolidBrush(Color.FromArgb(248, 249, 251))) e.Graphics.FillRectangle(brush, button);
                using (var pen = new Pen(Color.FromArgb(180, 187, 195))) e.Graphics.DrawRectangle(pen, button);
                TextRenderer.DrawText(e.Graphics, column.HeaderText, e.CellStyle.Font ?? grid.Font,
                    text, e.CellStyle.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.PreserveGraphicsClipping);
                int sort = direction(property);
                int x = button.Left + button.Width / 2, y = button.Top + button.Height / 2;
                int half = Math.Max(3, (int)Math.Round(3.0 * grid.DeviceDpi / 96.0));
                Point[] points = sort > 0
                    ? new[] { new Point(x - half, y + 2), new Point(x + half, y + 2), new Point(x, y - 2) }
                    : new[] { new Point(x - half, y - 2), new Point(x + half, y - 2), new Point(x, y + 2) };
                using (var brush = new SolidBrush(sort == 0 ? Color.FromArgb(75, 83, 92) : Color.FromArgb(202, 28, 36)))
                    e.Graphics.FillPolygon(brush, points);
                e.Handled = true;
            };
        }
    }
}
