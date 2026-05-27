#nullable disable

using KHStrategyLab.Models;
using System;
using System.Linq;
using System.Windows.Controls;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private string GetSelectedGridCode(DataGrid grid)
        {
            return grid?.SelectedItem is StockGridRow row
                ? NormalizeStockCode(row.Code)
                : "";
        }

        private bool IsGridKeyboardFocusWithin(DataGrid grid)
        {
            return grid?.IsKeyboardFocusWithin == true;
        }

        private int GetSelectedGridIndex(DataGrid grid)
        {
            return grid?.SelectedIndex ?? -1;
        }

        private void RestoreGridSelection(DataGrid grid, string code, int index, bool preferIndex)
        {
            code = NormalizeStockCode(code);
            if (grid == null)
                return;

            StockGridRow row = null;

            if (preferIndex && index >= 0 && index < grid.Items.Count)
                row = grid.Items[index] as StockGridRow;

            row ??= grid.Items
                .OfType<StockGridRow>()
                .FirstOrDefault(x => NormalizeStockCode(x.Code) == code);

            if (row == null)
                return;

            if (ReferenceEquals(grid.SelectedItem, row))
                return;

            _isRestoringGridSelection = true;
            try
            {
                grid.SelectedItem = row;
            }
            finally
            {
                _isRestoringGridSelection = false;
            }
        }

    }
}
