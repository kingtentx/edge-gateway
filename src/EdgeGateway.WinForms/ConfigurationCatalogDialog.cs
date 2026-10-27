namespace EdgeGateway.WinForms;

internal sealed record ConfigurationCatalogRow(string Key, string Name, int DeviceCount, string Details);

/// <summary>Designer-owned catalog surface; the owner binds application commands.</summary>
internal sealed partial class ConfigurationCatalogDialog : DpiAwareForm
{
    private Func<IReadOnlyList<ConfigurationCatalogRow>> _readRows = () => [];
    private Func<bool> _canEdit = () => false;
    private Func<ConfigurationCatalogRow?, Task<string?>> _edit = _ => Task.FromResult<string?>(null);
    private Func<ConfigurationCatalogRow, Task> _delete = _ => Task.CompletedTask;
    private bool _busy;
    internal ConfigurationCatalogRow? Selected => catalogGrid.CurrentRow?.DataBoundItem as ConfigurationCatalogRow;

    public ConfigurationCatalogDialog()
    {
        InitializeComponent();
        ConfigureWindow(new Size(860, 560), new Size(620, 400));
        addButton.Click += async (_, _) => await EditAsync(true);
        editButton.Click += async (_, _) => await EditAsync(false);
        deleteButton.Click += async (_, _) => await DeleteAsync();
        catalogGrid.SelectionChanged += (_, _) => UpdateActions();
        catalogGrid.CurrentCellChanged += (_, _) => UpdateActions();
        catalogGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditAsync(false); };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    internal void Bind(string title, Func<IReadOnlyList<ConfigurationCatalogRow>> readRows,
        Func<bool> canEdit, Func<ConfigurationCatalogRow?, Task<string?>> edit,
        Func<ConfigurationCatalogRow, Task> delete)
    {
        Text = title;
        titleLabel.Text = title;
        _readRows = readRows; _canEdit = canEdit; _edit = edit; _delete = delete;
        Reload();
    }

    internal void Reload(string? key = null)
    {
        GridViewState.Rebind(catalogGrid, _readRows().ToList(), row => row.Key, key ?? Selected?.Key);
        UpdateActions();
    }

    private void UpdateActions()
    {
        var editable = !_busy && _canEdit();
        addButton.Enabled = editable;
        editButton.Enabled = editable && Selected is not null;
        deleteButton.Enabled = editable && Selected is { DeviceCount: 0 };
        closeButton.Enabled = !_busy;
        statusLabel.Text = !_canEdit() ? "请停止全部设备后进行新增、编辑或删除。"
            : Selected is { DeviceCount: > 0 } ? "所选项目正在被设备引用；请先修改设备配置，再删除。"
            : $"共 {catalogGrid.Rows.Count} 项；双击行可编辑，修改后自动保存。";
    }

    private async Task EditAsync(bool add)
    {
        var target = Selected;
        if (_busy || !_canEdit() || (!add && target is null)) return;
        _busy = true; UpdateActions();
        string? error = null;
        try { var key = await _edit(add ? null : target); Reload(key); }
        catch (Exception exception) { error = exception.Message; }
        finally { _busy = false; UpdateActions(); if (error is not null) statusLabel.Text = error; }
    }

    private async Task DeleteAsync()
    {
        var target = Selected;
        if (_busy || !_canEdit() || target is null || target.DeviceCount != 0) return;
        if (MessageBox.Show(this, $"删除“{target.Name}”？", Text, MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true; UpdateActions();
        string? error = null;
        try { await _delete(target); Reload(); }
        catch (Exception exception) { error = exception.Message; }
        finally { _busy = false; UpdateActions(); if (error is not null) statusLabel.Text = error; }
    }
}
