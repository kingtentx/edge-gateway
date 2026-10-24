namespace EdgeGateway.WinForms;

/// <summary>A field-specific validation failure, preserving normal InvalidOperationException handling.</summary>
internal sealed class EditorValidationException(string message, Control field) : InvalidOperationException(message)
{
    public Control Field { get; } = field;
}

/// <summary>Persistent, non-modal feedback shared by configuration and write dialogs.</summary>
internal sealed class EditorFeedback : TableLayoutPanel
{
    private readonly Label _heading;
    private readonly TextBox _message;
    private readonly LinkLabel _locate;
    private Control? _field;

    public EditorFeedback(string name)
    {
        Name = name; Visible = false;
        Dock = DockStyle.Fill; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 2; RowCount = 2;
        Padding = new Padding(12, 8, 12, 8); Margin = new Padding(0, 8, 0, 0);
        BackColor = Color.FromArgb(255, 245, 245);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _heading = UiTheme.Label("请检查后重试，输入内容已保留", 10, UiTheme.Red, true);
        _heading.Dock = DockStyle.Fill; _heading.Margin = Padding.Empty;
        _locate = new LinkLabel
        {
            Name = name + "Locate", Text = "定位到字段", AutoSize = true,
            Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0),
            LinkColor = UiTheme.Red, ActiveLinkColor = UiTheme.Red,
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        _locate.LinkClicked += (_, _) => RevealField();
        _message = new TextBox
        {
            Name = name + "Message", Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = BackColor, ForeColor = UiTheme.Ink, Dock = DockStyle.Fill,
            Height = 42, Margin = new Padding(0, 5, 0, 0),
            AccessibleName = "校验或保存错误详情"
        };
        Controls.Add(_heading, 0, 0); Controls.Add(_locate, 1, 0);
        Controls.Add(_message, 0, 1); SetColumnSpan(_message, 2);
    }

    public void Clear()
    {
        _field = null;
        _message.Clear();
        Visible = false;
    }

    public void ShowFailure(Exception exception, Control? field = null)
    {
        _field = field ?? (exception as EditorValidationException)?.Field;
        _locate.Visible = _field is { IsDisposed: false };
        _message.Text = exception.Message;
        Visible = true;
        Parent?.PerformLayout();
        RevealField();
    }

    private void RevealField()
    {
        if (_field is null || _field.IsDisposed) { _locate.Visible = false; return; }
        var pages = new Stack<TabPage>();
        for (var parent = _field.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage page) pages.Push(page);
        foreach (var page in pages)
            if (page.Parent is TabControl tabs) tabs.SelectedTab = page;
        var form = _field.FindForm();
        form?.PerformLayout();
        if (_field.Enabled && _field.CanSelect) _field.Focus();
        for (var parent = _field.Parent; parent is not null && parent != form; parent = parent.Parent)
            if (parent is ScrollableControl { AutoScroll: true } scroll) scroll.ScrollControlIntoView(_field);
        if (_field is TextBoxBase text && text.Enabled) text.SelectAll();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_heading is not null)
            _heading.MaximumSize = new Size(Math.Max(1, ClientSize.Width - Padding.Horizontal
                - (_locate.Visible ? _locate.PreferredWidth + _locate.Margin.Horizontal : 0)), 0);
        base.OnLayout(levent);
    }
}
