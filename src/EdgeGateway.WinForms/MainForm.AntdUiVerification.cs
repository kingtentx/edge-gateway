namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    // AntdUI.Button implements IButtonControl but does not inherit the native
    // WinForms Button. Keep the same preferred-size assertion for both types;
    // switching the navigation library must not remove its layout checks.
    private static void AssertButtonContentFits(AntdUI.Button button, string context)
    {
        var preferred = button.GetPreferredSize(Size.Empty);
        if (button.Width < preferred.Width || button.Height < preferred.Height)
            throw new InvalidOperationException($"{context}: button '{button.Text}' {button.Size} is smaller than its untruncated text/icon layout {preferred}.");
    }
}
