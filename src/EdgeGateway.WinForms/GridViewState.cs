using System.ComponentModel;

namespace EdgeGateway.WinForms;

/// <summary>Retains a stable row, column and scroll position while a UI snapshot is replaced.</summary>
internal sealed record GridViewState(object? SelectedKey, string? ColumnName, object? FirstVisibleKey,
    int FirstVisibleIndex, int HorizontalOffset)
{
    internal static GridViewState Capture(DataGridView grid, Func<object, object?> key)
    {
        var first = grid.FirstDisplayedScrollingRowIndex;
        return new(grid.CurrentRow?.DataBoundItem is { } item ? key(item) : null,
            grid.CurrentCell?.OwningColumn?.Name,
            first >= 0 && first < grid.Rows.Count && grid.Rows[first].DataBoundItem is { } firstItem ? key(firstItem) : null,
            first, grid.HorizontalScrollingOffset);
    }

    internal void Restore(DataGridView grid, Func<object, object?> key, object? preferredKey = null)
    {
        if (grid.Rows.Count == 0) { grid.ClearSelection(); return; }
        var wanted = preferredKey ?? SelectedKey;
        DataGridViewRow? selected = null, first = null;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.DataBoundItem is not { } data) continue;
            var rowKey = key(data);
            if (wanted is not null && Equals(rowKey, wanted)) selected = row;
            if (FirstVisibleKey is not null && Equals(rowKey, FirstVisibleKey)) first = row;
        }
        var column = ColumnName is not null && grid.Columns.Contains(ColumnName) && grid.Columns[ColumnName].Visible
            ? grid.Columns[ColumnName] : grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).MinBy(c => c.DisplayIndex);
        if (column is not null && (selected ?? grid.CurrentRow ?? grid.Rows[0]) is { } target)
        {
            grid.ClearSelection();
            grid.CurrentCell = target.Cells[column.Index]; target.Selected = true;
        }
        // A newly created / explicitly requested item should be shown. Otherwise the
        // reader's viewport wins over the temporary auto-scroll caused by binding.
        var firstIndex = preferredKey is not null && !Equals(preferredKey, SelectedKey) && selected is not null
            ? selected.Index : first?.Index ?? Math.Clamp(FirstVisibleIndex, 0, grid.Rows.Count - 1);
        if (grid.IsHandleCreated && grid.Visible && grid.ClientSize.Height > grid.ColumnHeadersHeight
            && !grid.Rows[firstIndex].Frozen && grid.Rows[firstIndex].Visible)
            grid.FirstDisplayedScrollingRowIndex = firstIndex;
        grid.HorizontalScrollingOffset = Math.Max(0, HorizontalOffset);
    }

    internal static void Rebind<T>(DataGridView grid, IReadOnlyList<T> rows, Func<T, object?> key, object? preferredKey = null)
    {
        object? Key(object item) => item is T row ? key(row) : null;
        var state = Capture(grid, Key);
        grid.DataSource = rows.ToList();
        state.Restore(grid, Key, preferredKey);
    }

    /// <summary>Polling keeps bound rows alive until stable identities or their order change.</summary>
    internal static void UpdateRows<T>(DataGridView grid, IReadOnlyList<T> rows, Func<T, object?> key, object? preferredKey = null)
    {
        object? RowKey(object item) => item is T value ? key(value) : null;
        if (grid.DataSource is BindingList<T> bound && bound.Count == rows.Count
            && bound.Select(key).SequenceEqual(rows.Select(key)))
        {
            GridViewState? state = preferredKey is not null ? Capture(grid, RowKey) : null;
            for (var index = 0; index < rows.Count; index++)
            {
                if (EqualityComparer<T>.Default.Equals(bound[index], rows[index])) continue;
                state ??= Capture(grid, RowKey);
                bound[index] = rows[index];
            }
            state?.Restore(grid, RowKey, preferredKey);
            return;
        }
        var previous = Capture(grid, RowKey);
        grid.DataSource = new BindingList<T>(rows.ToList());
        previous.Restore(grid, RowKey, preferredKey);
    }
}
