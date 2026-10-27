namespace EdgeGateway.WinForms;

/// <summary>Persistent, non-modal feedback shared by configuration and write dialogs.</summary>
internal sealed partial class EditorFeedback : TableLayoutPanel
{
    private Control? _field;

    public string MessageControlName { get => _message.Name; set => _message.Name = value; }
    public string LocateControlName { get => _locate.Name; set => _locate.Name = value; }

    public EditorFeedback() { InitializeComponent(); }
    public EditorFeedback(string name) : this()
    { Name = name; MessageControlName = name + "Message"; LocateControlName = name + "Locate"; }
    private void OnLocateClicked(object? sender, LinkLabelLinkClickedEventArgs e) => RevealField();

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
        var target = _field;
        void FocusField()
        {
            if (target.IsDisposed || _field != target || form?.IsDisposed == true) return;
            form?.PerformLayout();
            if (target.Enabled && target.CanSelect) target.Focus();
            for (var parent = target.Parent; parent is not null && parent != form; parent = parent.Parent)
                if (parent is ScrollableControl { AutoScroll: true } scroll) scroll.ScrollControlIntoView(target);
            if (target is TextBox text && text.Enabled) text.SelectAll();
        }
        // AntdUI activates pages on the next UI message; locate only after that
        // activation, otherwise a validation error on another tab cannot be focused.
        if (form is { IsHandleCreated: true } && pages.Any(page => !page.Showed)) form.BeginInvoke((Action)FocusField);
        else FocusField();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_heading is not null)
            _heading.MaximumSize = new Size(Math.Max(1, ClientSize.Width - Padding.Horizontal
                - (_locate.Visible ? _locate.PreferredWidth + _locate.Margin.Horizontal : 0)), 0);
        base.OnLayout(levent);
    }
}
