namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private async Task SmokeDataRoutesAsync(Guid deviceId, Guid tagId)
    {
        var observed = false;
        Exception? failure = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var dialog = System.Windows.Forms.Application.OpenForms.OfType<DataRoutesDialog>().FirstOrDefault();
            if (dialog is null) return;
            timer.Stop(); observed = true;
            try
            {
                dialog.ClientSize = new Size(UiTheme.Scale(dialog, 640), UiTheme.Scale(dialog, 480));
                PerformLayoutRecursively(dialog);
                var text = CapabilityControl<GatewayInput>(dialog, "DataRoutesDetails");
                if (!text.ReadOnly || !text.AutoScroll || !text.Text.Contains(tagId.ToString()) || !text.Text.Contains("采样时间"))
                    throw new InvalidOperationException("Point details omitted identity/time or allowed editing.");
                foreach (var button in Descendants(dialog).OfType<GatewayButton>().Where(button => button.Name.StartsWith("Route")))
                    BringIntoViewAndAssert(button, "data route action " + button.Name);
                SaveEditorScreenshot(dialog, "data-routes", "narrow");
                CapabilityControl<GatewayButton>(dialog, "RouteDiagnostics").PerformClick();
            }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        };
        timer.Start();
        try { await ShowDataRoutesAsync(deviceId, tagId); }
        finally { timer.Stop(); }
        if (!observed || failure is not null) throw new InvalidOperationException("Data routes smoke failed.", failure);
        if (_activePage != "diagnostics" || _diagnosticDeviceId != deviceId
            || _telemetryDiagnosticRows.Any(row => row.Key.StartsWith("device:") && !row.Key.StartsWith($"device:{deviceId}:")))
            throw new InvalidOperationException("Device diagnostics lost route context.");
        _diagnosticDeviceId = null;
        ShowPage("realtime");
    }
}
