using EdgeGateway.Model;
using EdgeGateway.Application;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool SurfacesSmoke { get; set; }
    internal bool SurfacesListsOnly { get; set; }

    private static void AssertSurfaceStyles(Control root)
    {
        if (UiTheme.Background != Color.White) throw new InvalidOperationException("Content background is not white.");
        foreach (var grid in Descendants(root).OfType<GatewayTable>())
            if (grid.RowSelectedBg != AntdUI.Style.Get(AntdUI.Colour.PrimaryBgHover, nameof(AntdUI.Table)))
                throw new InvalidOperationException("Selected row does not use the stronger theme color.");
        foreach (var pager in Descendants(root).OfType<AntdUI.Pagination>().Where(p => p.Visible))
            if (Math.Abs(pager.Parent!.Height - UiTheme.Scale(pager, 36)) > 2 || pager.Height < pager.Font.Height)
                throw new InvalidOperationException($"Invalid pagination height: {pager.Name}, {pager.Parent.Height}/{pager.Height}.");
    }

    private async Task SmokeListSurfacesAsync()
    {
        using var points = new DevicePointsDialog();
        using var catalog = new ConfigurationCatalogDialog();
        using var drivers = new DriverPluginDialog(_application, _driverDirectory);
        using var preview = new DatabasePreviewDialog();
        using var tags = new TagImportDialog("预览检查", TagCsvExchange.Export([new TagConfiguration { Name = "温度", Address = "sine" }]), [], _ => []);
        using var devices = new DeviceWorkbookImportDialog(_ => new([], 0, []));
        GridViewState.Rebind(points.TagGrid, new[]
        {
            new TagRow(Guid.NewGuid(), "出水温度", "sine", "", "Float64", "", "", "× 0.5 + 15", "℃", "只读", "是", "是", "继承设备", "0"),
            new TagRow(Guid.NewGuid(), "目标温度", "constant:45", "", "Float64", "", "", "原样保存", "℃", "读 / 写", "是", "是", "继承设备", "0")
        }, row => row.Id);
        foreach (var dialog in new Form[] { points, catalog, drivers, preview, tags, devices })
        {
            dialog.Show(this);
            try
            {
                var original = dialog.Size;
                foreach (var narrow in new[] { false, true })
                {
                    dialog.Size = narrow ? new Size(Math.Max(dialog.MinimumSize.Width, UiTheme.Scale(dialog, 680)), UiTheme.Scale(dialog, 600)) : original;
                    await Task.Delay(100);
                    if (dialog is EditorDialog editor)
                    {
                        VerifyEditorLayout(editor, "list surfaces " + dialog.GetType().Name);
                        editor.SelectedSmokeTab = editor.SmokeTabs.Last();
                    }
                    PerformLayoutRecursively(dialog);
                    AssertSurfaceStyles(dialog);
                    foreach (var pager in Descendants(dialog).OfType<AntdUI.Pagination>().Where(p => p.Visible))
                    {
                        BringIntoViewAndAssert(pager, "last pagination row");
                        var bottom = pager.Parent!.RectangleToScreen(pager.Parent.ClientRectangle).Bottom;
                        foreach (var control in Descendants(dialog).Where(c => c.Visible && c is Button or Label))
                        {
                            var rect = control.RectangleToScreen(control.ClientRectangle);
                            if (rect.Top >= bottom && dialog.RectangleToScreen(dialog.ClientRectangle).IntersectsWith(rect))
                                throw new InvalidOperationException($"Content below pagination: {dialog.GetType().Name}/{control.Name}.");
                        }
                    }
                    if (dialog == points)
                    {
                        points.TagSearch.Text = "温度";
                        FocusForSmoke(points.TagSearch, points.TagSearch, points, "point toolbar search");
                        await Task.Delay(150);
                        AssertPointToolbarAlignment(points);
                    }
                    SaveEditorScreenshot(dialog, "surfaces", dialog.GetType().Name + (narrow ? "-narrow" : "-wide"));
                }
                if (dialog == tags)
                {
                    await tags.ApplyAndAcceptForSmokeAsync();
                    if (tags.DialogResult != DialogResult.OK) throw new InvalidOperationException("Import confirmation did not accept the preview.");
                }
                else if (dialog.CancelButton is Button cancel)
                {
                    cancel.PerformClick();
                    if (dialog.DialogResult != DialogResult.Cancel) throw new InvalidOperationException("List cancel did not return Cancel.");
                }
            }
            finally { dialog.Close(); }
        }
        Console.WriteLine("List surfaces passed: pagination last, compact bars, theme selection and cancel paths.");
    }

    private static void AssertPointToolbarAlignment(DevicePointsDialog points)
    {
        var search = points.TagSearch;
        var searchRect = search.RectangleToScreen(search.ReadRectangle);
        foreach (var button in points.tagSearchSurface.Parent!.Controls.OfType<GatewayButton>())
        {
            var buttonRect = button.RectangleToScreen(button.ReadRectangle);
            // Wrapped rows may have different Y coordinates, but retain equal field heights.
            if (Math.Abs(buttonRect.Height - searchRect.Height) > UiTheme.Scale(search, 1)
                || (button.Top == points.tagSearchSurface.Top
                    && Math.Abs(buttonRect.Top - searchRect.Top) > UiTheme.Scale(search, 1)))
                throw new InvalidOperationException($"Point toolbar surfaces differ: {button.Name}={buttonRect}, search={searchRect}.");
        }
    }
}
