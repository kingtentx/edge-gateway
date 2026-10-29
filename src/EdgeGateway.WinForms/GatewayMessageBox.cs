namespace EdgeGateway.WinForms;

/// <summary>Official AntdUI modal, preserving explicit confirmation and cancellation results.</summary>
internal static class GatewayMessageBox
{
    internal static DialogResult Show(string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        Show(null, text, caption, buttons, icon, defaultButton);

    internal static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        var form = owner as Form ?? (owner as Control)?.FindForm();
        var config = form is null ? new AntdUI.Modal.Config(caption, text) : new AntdUI.Modal.Config(form, caption, text);
        config.Icon = icon switch
        {
            MessageBoxIcon.Error => AntdUI.TType.Error,
            MessageBoxIcon.Warning => AntdUI.TType.Warn,
            MessageBoxIcon.Information => AntdUI.TType.Info,
            MessageBoxIcon.Question => AntdUI.TType.Info,
            _ => AntdUI.TType.None
        };
        config.MaskClosable = false;
        config.DefaultAcceptButton = defaultButton == MessageBoxDefaultButton.Button1;
        config.DefaultFocus = true;
        config.ButtonArrowNavigation = true;
        config.OkText = buttons is MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel ? "是" : "确定";
        config.CancelText = buttons switch { MessageBoxButtons.OK => null, MessageBoxButtons.YesNo => "否", _ => "取消" };
        if (buttons == MessageBoxButtons.YesNoCancel)
            config.Btns = [new AntdUI.Modal.Btn("No", "否") { DialogResult = DialogResult.Ignore }];
        var defaultName = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 when buttons == MessageBoxButtons.YesNoCancel => "No",
            MessageBoxDefaultButton.Button2 or MessageBoxDefaultButton.Button3 => "Cancel",
            _ => "OK"
        };
        var modalButtons = new Dictionary<string, AntdUI.Button>();
        config.OnButtonStyle = (name, button) =>
        {
            modalButtons.Add(name, button);
            button.BorderWidth = 1;
            if (name != "OK") return;
            button.HandleCreated += (_, _) =>
            {
                if (button.FindForm() is not { } dialog) return;
                // Load runs before AntdUI sorts its arrow-navigation list by Left.
                // Reorder the real buttons, preserving their click/result handlers.
                dialog.Load += (_, _) => ArrangeButtons(modalButtons);
                dialog.Shown += (_, _) => dialog.BeginInvoke((Action)(() =>
                {
                    if (dialog.IsDisposed || !modalButtons.TryGetValue(defaultName, out var defaultAction)
                        || defaultAction.IsDisposed) return;
                    dialog.AcceptButton = defaultAction;
                    defaultAction.Focus();
                }));
            };
        };
        var result = AntdUI.Modal.open(config);
        if (buttons is MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel)
        {
            if (result == DialogResult.OK) return DialogResult.Yes;
            if (buttons == MessageBoxButtons.YesNoCancel)
                return result == DialogResult.Ignore ? DialogResult.No : DialogResult.Cancel;
            return DialogResult.No;
        }
        return result == DialogResult.OK ? DialogResult.OK : DialogResult.Cancel;
    }

    private static void ArrangeButtons(IReadOnlyDictionary<string, AntdUI.Button> buttons)
    {
        if (buttons["OK"].Parent is not { } panel) return;
        panel.SuspendLayout();
        try
        {
            var index = 0;
            foreach (var name in new[] { "OK", "No", "Cancel" })
            {
                if (!buttons.TryGetValue(name, out var button)) continue;
                // Right-docked controls are laid out in reverse child-index order.
                panel.Controls.SetChildIndex(button, index);
                button.TabIndex = index++;
                // Width-only autosizing can resize a right-docked button after
                // its parent has placed it, leaving the new right edge clipped.
                var width = Math.Max(button.PSize.Width, UiTheme.Scale(button, 80));
                button.AutoSizeMode = AntdUI.TAutoSize.None;
                button.MinimumSize = new Size(UiTheme.Scale(button, 80), 0);
                button.Width = width;
            }
        }
        finally { panel.ResumeLayout(true); }
    }
}
