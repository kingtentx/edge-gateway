using System.ComponentModel;

namespace EdgeGateway.WinForms;

/// <summary>AntdUI text editor with the desktop form's existing binding vocabulary.</summary>
public class GatewayInput : AntdUI.Input
{
    public GatewayInput()
    {
        AutoSize = false;
        Height = UiTheme.ActionHeight;
        Radius = 6;
        BorderWidth = 1;

        BackColor = Color.White;
        ForeColor = UiTheme.Ink;
    }

    [DefaultValue(BorderStyle.FixedSingle)]
    public BorderStyle BorderStyle
    {
        get => BorderWidth == 0 ? BorderStyle.None : BorderStyle.FixedSingle;
        set => BorderWidth = value == BorderStyle.None ? 0 : 1;
    }

    // AntdUI owns multiline scrollbars and caret scrolling. A vertical request
    // enables that behavior; none is used for single-line editors.
    [DefaultValue(ScrollBars.None)]
    public ScrollBars ScrollBars
    {
        get => AutoScroll ? ScrollBars.Vertical : ScrollBars.None;
        set => AutoScroll = value != ScrollBars.None;
    }

    [Browsable(false)]
    public int TextLength => Text.Length;
}
