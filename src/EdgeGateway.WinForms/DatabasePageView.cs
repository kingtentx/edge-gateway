namespace EdgeGateway.WinForms;

/// <summary>Fixed design-time structure for database connections and acquisition write groups.</summary>
internal sealed partial class DatabasePageView : UserControl
{
    internal TabControl DatabaseTabs => databaseTabs;
    internal DataGridView ConnectionsGrid => connectionsGrid;
    internal DataGridView GroupsGrid => groupsGrid;
    internal Label DatabaseState => databaseStateLabel;
    internal Control ConnectionsToolbarHost => connectionsToolbarHost;
    internal Control GroupsToolbarHost => groupsToolbarHost;

    public DatabasePageView() { InitializeComponent(); GridPagination.Attach(connectionsGrid, connectionsGridPagination); GridPagination.Attach(groupsGrid, groupsGridPagination); }
}
