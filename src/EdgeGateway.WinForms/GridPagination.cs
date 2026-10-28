using System.Runtime.CompilerServices;

namespace EdgeGateway.WinForms;

/// <summary>Pages a complete UI snapshot without changing query/export or command targets.</summary>
internal sealed class GridPagination
{
    private static readonly ConditionalWeakTable<DataGridView, GridPagination> Bindings = new();
    private readonly AntdUI.Pagination _pagination;
    private readonly Label _total;
    private readonly ComboBox _size;
    private static readonly int[] PageSizes = [10, 20, 50, 100];
    private Action? _render;
    private bool _binding;
    private int _current = 1;
    private bool _arranging;

    private GridPagination(AntdUI.Pagination pagination, Label total, ComboBox size)
    {
        _pagination = pagination;
        _total = total;
        _size = size;
        pagination.Parent!.Layout += (_, _) => ArrangeFooter();
        size.SelectedIndexChanged += (_, _) =>
        {
            if (!_binding && size.SelectedIndex >= 0) pagination.PageSize = PageSizes[size.SelectedIndex];
        };
        pagination.ValueChanged += (_, _) =>
        {
            if (_binding) return;
            _current = pagination.Current;
            _render?.Invoke();
        };
        pagination.HandleCreated += (_, _) => pagination.BeginInvoke(new Action(() =>
        {
            if (!pagination.IsDisposed) _render?.Invoke();
        }));
    }

    internal static void Attach(DataGridView grid, AntdUI.Pagination pagination, Label total, ComboBox size)
        => Bindings.Add(grid, new GridPagination(pagination, total, size));

    internal static AntdUI.Pagination GetPager(DataGridView grid) => Bindings.GetValue(grid,
        _ => throw new InvalidOperationException("Grid has no pagination binding."))._pagination;

    private void ArrangeFooter()
    {
        if (_arranging || _pagination.Parent is not TableLayoutPanel footer) return;
        _arranging = true;
        try
        {
            var available = footer.ClientSize.Width - footer.Padding.Horizontal
                - _total.GetPreferredSize(Size.Empty).Width - _total.Margin.Horizontal
                - (int)footer.ColumnStyles[2].Width;
            var pages = Math.Max(1, (_pagination.Total + Math.Max(1, _pagination.PageSize) - 1) / Math.Max(1, _pagination.PageSize));
            var slot = Math.Max(_pagination.Height, TextRenderer.MeasureText(pages.ToString(), _pagination.Font).Width)
                + UiTheme.Scale(_pagination, _pagination.Gap);
            var width = Math.Max(0, Math.Min(available, (Math.Min(pages, _pagination.MaxPageTotal) + 2) * slot));
            if (footer.ColumnStyles[3].Width != width) footer.ColumnStyles[3].Width = width;
        }
        finally { _arranging = false; }
    }

    internal static bool Bind<T>(DataGridView grid, IReadOnlyList<T> rows, Func<T, object?> key,
        object? preferredKey, bool update)
    {
        if (!Bindings.TryGetValue(grid, out var binding)) return false;
        binding.SetRows(grid, rows, key, preferredKey, update);
        return true;
    }

    private void SetRows<T>(DataGridView grid, IReadOnlyList<T> rows, Func<T, object?> key,
        object? preferredKey, bool update)
    {
        void Render(object? preferred = null)
        {
            _binding = true;
            try
            {
                _pagination.Total = rows.Count;
                var totalText = $"共 {rows.Count} 条";
                if (_total.Text != totalText) _total.Text = totalText;
                _size.SelectedIndex = Array.IndexOf(PageSizes, _pagination.PageSize);
                ArrangeFooter();
                var size = Math.Max(1, _pagination.PageSize);
                var current = Math.Clamp(_current, 1, Math.Max(1, (rows.Count + size - 1) / size));
                if (preferred is not null)
                    for (var index = 0; index < rows.Count; index++)
                        if (Equals(key(rows[index]), preferred)) { current = index / size + 1; break; }
                // PageTotal is calculated only after handle creation; keep the requested page
                // independently and apply it again once AntdUI has laid out its page buttons.
                _current = current;
                _pagination.Current = current;
                var page = rows.Skip((current - 1) * size).Take(size).ToList();
                if (update) GridViewState.UpdatePageRows(grid, page, key, preferred);
                else GridViewState.RebindPage(grid, page, key, preferred);
            }
            finally { _binding = false; }
        }
        _render = () => Render();
        Render(preferredKey);
    }
}
