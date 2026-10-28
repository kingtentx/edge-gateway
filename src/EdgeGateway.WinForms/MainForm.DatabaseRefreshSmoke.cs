using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeDatabaseRefreshAsync()
    {
        if (!_smokeTest) throw new InvalidOperationException("Database refresh verification requires an isolated host.");
        var original = _configuration;
        try
        {
            _configuration = Clone(original);
            _configuration.Databases = []; _configuration.DatabaseWriteGroups = [];
            ShowPage("database");
            await RefreshSnapshotsAsync();
            await Task.Delay(150);
            PerformLayoutRecursively(this);
            await VerifyStableDatabaseRefreshAsync("empty");
            SaveEditorScreenshot(this, "database-refresh", "empty");

            _configuration.Databases = Enumerable.Range(1, 40).Select(index => new DatabaseConnectionConfiguration
                { Name = $"刷新验证连接 {index}", Enabled = false }).ToList();
            _configuration.DatabaseWriteGroups = _configuration.Databases.Select((connection, index) => new DatabaseWriteGroupConfiguration
                { Name = $"刷新验证写入组 {index + 1}", ConnectionId = connection.Id, Enabled = false }).ToList();
            BindDatabaseViews();
            foreach (var (grid, tab, name) in new[]
                { (_databaseGrid, 0, "connections"), (_databaseGroupGrid, 1, "groups") })
            {
                _databaseTabs.SelectedIndex = tab;
                await Task.Delay(100);
                PerformLayoutRecursively(this);
                GridPagination.GetPager(grid).PageSize = 50;
                grid.CurrentCell = grid.Rows[25].Cells["Name"];
                grid.FirstDisplayedScrollingRowIndex = 20;
                grid.HorizontalScrollingOffset = UiTheme.Scale(grid, 80);
                var before = GridViewState.Capture(grid, item => item is DatabaseConnectionRow connection ? connection.Id : ((DatabaseGroupRow)item).Id);
                var dataSource = grid.DataSource;
                var row = grid.Rows[25];
                await VerifyStableDatabaseRefreshAsync(name);
                var after = GridViewState.Capture(grid, item => item is DatabaseConnectionRow connection ? connection.Id : ((DatabaseGroupRow)item).Id);
                if (before != after || !ReferenceEquals(dataSource, grid.DataSource) || !ReferenceEquals(row, grid.Rows[25]))
                    throw new InvalidOperationException(name + ": timer refresh rebuilt rows or moved the reading position.");
                if (tab == 0) _configuration.Databases[25].Name = "连接名称更新";
                else _configuration.DatabaseWriteGroups[25].Name = "写入组名称更新";
                await RefreshSnapshotsAsync();
                var expectedName = tab == 0 ? "连接名称更新" : "写入组名称更新";
                if (!Equals(grid.Rows[25].Cells["Name"].Value, expectedName) || !ReferenceEquals(dataSource, grid.DataSource))
                    throw new InvalidOperationException(name + ": changed values did not appear in the existing bound rows.");
                SaveEditorScreenshot(this, "database-refresh", name);
            }
            var removed = _configuration.Databases[25].Id;
            _configuration.Databases.RemoveAt(25);
            _configuration.DatabaseWriteGroups.RemoveAll(group => group.ConnectionId == removed);
            await RefreshSnapshotsAsync();
            if (_databaseGrid.Rows.Count != 39 || _databaseGroupGrid.Rows.Count != 39)
                throw new InvalidOperationException("Database configuration removal did not rebuild the table structure.");
            Console.WriteLine($"Database refresh smoke passed: empty page and both populated tabs; actual timer ticks caused no status text churn, tab/grid movement or rebinding; changed values updated in place; selected row/column and scroll retained; removal rebuilt rows at actual {DeviceDpi} DPI. UI-only disabled database fixtures, no external connections.");
        }
        finally { _refreshTimer.Stop(); _configuration = original; }
    }

    private async Task VerifyStableDatabaseRefreshAsync(string context)
    {
        var changes = new List<string>();
        var tabs = _databaseTabs.Bounds;
        var connections = _databaseGrid.Bounds;
        var groups = _databaseGroupGrid.Bounds;
        void TextChanged(object? sender, EventArgs e) => changes.Add("status text changed");
        void DataSourceChanged(object? sender, EventArgs e) => changes.Add(((Control)sender!).Name + " rebound");
        void LayoutChanged(object? sender, EventArgs e)
        {
            if (_databaseTabs.Bounds != tabs || _databaseGrid.Bounds != connections || _databaseGroupGrid.Bounds != groups)
                changes.Add("tab/grid bounds moved");
        }
        _databaseState.TextChanged += TextChanged;
        _databaseGrid.DataSourceChanged += DataSourceChanged; _databaseGroupGrid.DataSourceChanged += DataSourceChanged;
        _databaseTabs.LocationChanged += LayoutChanged; _databaseTabs.SizeChanged += LayoutChanged;
        _databaseGrid.SizeChanged += LayoutChanged; _databaseGroupGrid.SizeChanged += LayoutChanged;
        try
        {
            for (var tick = 0; tick < 3; tick++) { await RefreshSnapshotsAsync(); await Task.Delay(25); }
            _refreshTimer.Start();
            await Task.Delay(2200);
            _refreshTimer.Stop();
            if (changes.Count > 0) throw new InvalidOperationException(context + ": database refresh instability: " + string.Join(", ", changes));
        }
        finally
        {
            _refreshTimer.Stop();
            _databaseState.TextChanged -= TextChanged;
            _databaseGrid.DataSourceChanged -= DataSourceChanged; _databaseGroupGrid.DataSourceChanged -= DataSourceChanged;
            _databaseTabs.LocationChanged -= LayoutChanged; _databaseTabs.SizeChanged -= LayoutChanged;
            _databaseGrid.SizeChanged -= LayoutChanged; _databaseGroupGrid.SizeChanged -= LayoutChanged;
        }
    }
}
