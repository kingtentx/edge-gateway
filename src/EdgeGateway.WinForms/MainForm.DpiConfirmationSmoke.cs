using System.Runtime.InteropServices;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool DpiConfirmationSmoke { get; set; }
    internal bool DpiRoundTripOnly { get; set; }

    private async Task SmokeDpiConfirmationAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("DPI verification requires an isolated stopped host.");
        _refreshTimer.Stop();
        SmokeAntdMigration();
        ShowPage("devices");
        var originalDpi = DeviceDpi;
        var originalBounds = Bounds;
        var fields = Descendants(_devicesPageView).OfType<FilterFieldPanel>().ToArray();
        var widths = fields.ToDictionary(field => field, field => field.ColumnStyles[1].Width / originalDpi * 96);
        try
        {
            foreach (var dpi in new[] { 96, 120, 144, 192, 96, 120, 144, 192, 96, originalDpi })
            {
                SendDpiNotificationForSmoke(dpi);
                await Task.Delay(100);
                Console.WriteLine($"Controlled DPI notification: requested={dpi}; form={DeviceDpi}; native={GetDpiForWindow(Handle)}; child={_devicesPageView.DeviceDpi}; font={Font.Size}; filters={string.Join(',', fields.Select(field => field.ColumnStyles[1].Width))}.");
                if (DeviceDpi != dpi) throw new InvalidOperationException("DPI notification did not update the form.");
                foreach (var field in fields)
                    if (Math.Abs(field.ColumnStyles[1].Width - widths[field] * dpi / 96) > 0.1)
                        throw new InvalidOperationException($"Filter column drift: {field.Name} at {dpi}; width={field.ColumnStyles[1].Width}.");
                SaveEditorScreenshot(this, "dpi-notification", dpi.ToString());
            }
        }
        finally
        {
            SendDpiNotificationForSmoke(originalDpi);
            Bounds = originalBounds;
        }
        if (DpiRoundTripOnly)
        {
            Console.WriteLine("DPI roundtrip smoke passed: confirmation results and controlled column scaling only; full native-DPI page/dialog run recorded separately.");
            return;
        }
        await SmokeFormWidthsAsync();
        Console.WriteLine("DPI confirmation smoke passed: native-DPI pages/dialogs, confirmation results and controlled WM_DPICHANGED round trips; no physical monitor transition or desktop DPI change.");
    }

    private void SendDpiNotificationForSmoke(int dpi)
    {
        var children = Descendants(this).Where(control => control.IsHandleCreated).ToArray();
        foreach (var control in children.Reverse()) SendControlSmokeMessage(control.Handle, 0x02E2, IntPtr.Zero, IntPtr.Zero);
        var bounds = Bounds;
        var rect = new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom };
        var pointer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(rect, 0, pointer, 4);
            SendControlSmokeMessage(Handle, 0x02E0, new IntPtr(dpi | dpi << 16), pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
        foreach (var control in children) SendControlSmokeMessage(control.Handle, 0x02E3, IntPtr.Zero, IntPtr.Zero);
    }

    private void VerifyModalOrderForSmoke(Form modal, AntdUI.Button[] buttons, MessageBoxButtons kind, string action)
    {
        var ordered = buttons.OrderBy(button => button.Left).ToArray();
        var expected = kind switch
        {
            MessageBoxButtons.OK => new[] { "确定" },
            MessageBoxButtons.OKCancel => new[] { "确定", "取消" },
            MessageBoxButtons.YesNo => new[] { "是", "否" },
            _ => new[] { "是", "否", "取消" }
        };
        if (!ordered.Select(button => button.Text).SequenceEqual(expected))
            throw new InvalidOperationException("Unexpected confirmation order: " + string.Join('/', ordered.Select(button => button.Text)));
        foreach (var button in ordered)
        {
            if (button.Width < UiTheme.Scale(button, 80) || button.Height < button.Font.Height)
                throw new InvalidOperationException("Confirmation button too small: " + button.Text);
            AssertVisibleBounds(button, "confirmation button");
        }
        var defaultButton = (AntdUI.Button)modal.AcceptButton!;
        if (modal.ActiveControl != defaultButton)
            throw new InvalidOperationException($"Wrong confirmation default: {kind}/{action}; default={defaultButton.Text}; active={modal.ActiveControl?.Text}.");
        if (!defaultButton.Focused)
        {
            Console.WriteLine($"Modal retains correct ActiveControl but requires isolated-desktop native focus recovery: {kind}/{action}.");
            FocusForSmoke(defaultButton, defaultButton, modal, "confirmation native focus");
        }
        if (!defaultButton.Focused) throw new InvalidOperationException("Confirmation cannot receive keyboard focus.");
        var processKey = modal.GetType().GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ordered[0].Focus();
        if (ordered.Length > 1)
        {
            processKey.Invoke(modal, [Keys.Tab]);
            if (!ordered[1].Focused) throw new InvalidOperationException("Tab order differs from visible button order.");
            processKey.Invoke(modal, [Keys.Left]);
            if (!ordered[0].Focused) throw new InvalidOperationException("Left arrow differs from visible button order.");
            processKey.Invoke(modal, [Keys.Right]);
            if (!ordered[1].Focused) throw new InvalidOperationException("Right arrow differs from visible button order.");
        }
        defaultButton.Focus();
        SaveEditorScreenshot(modal, "confirmation-order", kind + "-" + action);
    }
}
