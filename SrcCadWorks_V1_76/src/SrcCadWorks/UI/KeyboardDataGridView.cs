using System.Linq;
using System.Windows.Forms;

namespace SrcCadWorks.UI
{
    internal sealed class KeyboardDataGridView : DataGridView
    {
        protected override bool ProcessDialogKey(Keys keyData)
        {
            return Navigate(keyData) || base.ProcessDialogKey(keyData);
        }

        protected override bool ProcessDataGridViewKey(KeyEventArgs e)
        {
            return Navigate(e.KeyData) || base.ProcessDataGridViewKey(e);
        }

        private bool Navigate(Keys keyData)
        {
            if (CurrentCell == null || !Enabled) return false;
            var key = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) != Keys.None) return false;
            // Editing text or an open list keeps normal editor keyboard behaviour.
            if (IsCurrentCellInEditMode) return false;
            if ((key == Keys.Enter || key == Keys.F4) && !CurrentCell.ReadOnly &&
                CurrentCell is DataGridViewComboBoxCell)
            {
                if (BeginEdit(true) && EditingControl is ComboBox combo) combo.DroppedDown = true;
                return true;
            }
            if (key != Keys.Up && key != Keys.Down && key != Keys.Left && key != Keys.Right) return false;
            var columns = Columns.Cast<DataGridViewColumn>().Where(c => c.Visible)
                .OrderBy(c => c.DisplayIndex).ToList();
            int column = columns.FindIndex(c => c.Index == CurrentCell.ColumnIndex);
            int row = CurrentCell.RowIndex;
            if (key == Keys.Left || key == Keys.Right)
            {
                column += key == Keys.Left ? -1 : 1;
                if (column < 0 || column >= columns.Count) return true;
            }
            else
            {
                int step = key == Keys.Up ? -1 : 1;
                do { row += step; } while (row >= 0 && row < RowCount && (!Rows[row].Visible || Rows[row].IsNewRow));
                if (row < 0 || row >= RowCount) return true;
            }
            if (column < 0) return false;
            CurrentCell = Rows[row].Cells[columns[column].Index];
            return true;
        }
    }
}
