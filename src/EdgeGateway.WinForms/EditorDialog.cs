using EdgeGateway.Model;
using EdgeGateway.Application;
using System.Globalization;

namespace EdgeGateway.WinForms;

public partial class EditorDialog : DpiAwareForm
{
    private bool _arranging;
    private bool? _compactMode;
    private bool _readOnlyView;
    private bool _applying;
    private Func<Task>? _applyChanges;
    private Action<Exception>? _applyFailed;
    internal Exception? LastApplyErrorForSmoke { get; private set; }
    internal bool SuppressApplyErrorDialogForSmoke { get; set; }
    internal IReadOnlyList<TabPage> SmokeTabs => Tabs.TabPages.Cast<TabPage>().ToArray();
    internal TabPage? SelectedSmokeTab { get => Tabs.SelectedTab; set => Tabs.SelectedTab = value; }
    internal IReadOnlyList<Control> SmokeCommands => _readOnlyView ? new Control[] { _cancel } : new Control[] { _confirm, _cancel };
    public EditorDialog() : this("配置", "") { }

    private void OnActionsPaint(object? sender, PaintEventArgs e)
    { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawLine(pen, 0, 0, _buttons.Width, 0); }

    protected EditorDialog(string title, string hint)
    {
        Text = title;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        StartPosition = FormStartPosition.CenterParent;
        ConfigureWindow(new Size(780, 700), new Size(604, 481));
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        InitializeComponent();
        _title.Text = title;
        _description.Text = hint;
        _help.AccessibleDescription = hint;
        _help.LinkClicked += (_, _) => MessageBox.Show(this, _description.Text, "配置说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _confirm.Click += async (_, _) =>
        {
            if (_applying) return;
            try { await ApplyAndAcceptAsync(); }
            catch { /* The same apply path has already retained the draft and shown inline feedback. */ }
        };
        AcceptButton = _confirm;
        CancelButton = _cancel;
    }

    internal void VerifyHeadingForSmoke(string context)
    {
        var bounds = _title.RectangleToScreen(_title.ClientRectangle);
        if (!_title.Visible || bounds.Width <= 0 || bounds.Height <= 0
            || !_heading.RectangleToScreen(_heading.ClientRectangle).Contains(bounds)
            || !RectangleToScreen(ClientRectangle).Contains(bounds))
            throw new InvalidOperationException($"{context}: editor heading has no fully visible area ({bounds}).");

        var singleLine = _title.GetPreferredSize(new Size(int.MaxValue, int.MaxValue));
        var availableWidth = _heading.ClientSize.Width - _heading.Padding.Horizontal
            - _titleRow.Margin.Horizontal - _titleRow.Padding.Horizontal - _title.Margin.Horizontal
            - (_help.Visible ? _help.Width + _help.Margin.Horizontal : 0);
        var tolerance = UiTheme.Scale(this, 2);
        if (availableWidth >= singleLine.Width
            && (_title.ClientSize.Width + tolerance < singleLine.Width
                || Math.Abs(_title.ClientSize.Height - singleLine.Height) > tolerance))
            throw new InvalidOperationException($"{context}: heading '{_title.Text}' wraps or is clipped despite available width {availableWidth}; bounds {_title.Bounds}, single-line preferred size {singleLine}.");
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_layout is null || _arranging || !IsHandleCreated || !IsDpiLayoutReady) return;
        _arranging = true;
        try
        {
            var compact = ClientSize.Height < UiTheme.Scale(this, 460);
            var padding = UiTheme.Scale(this, new Padding(compact ? 10 : 16));
            if (_layout.Padding != padding) _layout.Padding = padding;
            var headingPadding = UiTheme.Scale(this, new Padding(0, 0, 0, compact ? 4 : 8));
            if (_heading.Padding != headingPadding) _heading.Padding = headingPadding;
            var buttonPadding = UiTheme.Scale(this, new Padding(12, compact ? 6 : 10, 4, 4));
            if (_buttons.Padding != buttonPadding) _buttons.Padding = buttonPadding;
            if (_compactMode != compact)
            {
                _compactMode = compact;
                _description.Visible = !compact;
                _help.Visible = compact;
            }
        }
        finally { _arranging = false; }
    }

    protected virtual void Commit() => throw new InvalidOperationException("请选择具体配置编辑器。");
    protected virtual Control? ResolveValidationField(Exception exception) => (exception as EditorValidationException)?.Field;

    protected void SetConfirmText(string text) => _confirm.Text = text;
    protected void ClearValidationFeedback() => _feedback.Clear();

    /// <summary>Validate and persist while the editor is still open, so any failure retains the user's inputs.</summary>
    internal void SetApplyHandler(Func<Task> apply, bool runningDevice, Action<Exception> onFailure)
    {
        _applyChanges = apply;
        _applyFailed = onFailure;
        if (!runningDevice) return;
        _confirm.Text = "保存并应用";
        _description.Text = "该设备正在采集。修改后保存将仅重新连接此设备；禁用设备将停止它。其他设备继续采集。";
        _help.AccessibleDescription = _description.Text;
    }

    private async Task ApplyAndAcceptAsync()
    {
        _applying = true;
        _confirm.Enabled = _cancel.Enabled = Tabs.Enabled = false;
        UseWaitCursor = true;
        LastApplyErrorForSmoke = null;
        _feedback.Clear();
        var confirmText = _confirm.Text;
        _confirm.Text = "正在保存…";
        try
        {
            try
            {
                Commit();
                if (_applyChanges is not null) await _applyChanges();
            }
            finally
            {
                _applying = false;
                _confirm.Enabled = !_readOnlyView;
                _cancel.Enabled = Tabs.Enabled = true;
                _confirm.Text = confirmText;
                UseWaitCursor = false;
            }
        }
        catch (Exception exception)
        {
            LastApplyErrorForSmoke = exception;
            _feedback.ShowFailure(exception, ResolveValidationField(exception));
            _applyFailed?.Invoke(exception);
            throw;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    internal Task ApplyAndAcceptForSmokeAsync() => ApplyAndAcceptAsync();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_applying) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <summary>Keep a configuration view navigable while removing every apply action.</summary>
    protected void SetReadOnlyView()
    {
        _readOnlyView = true;
        _confirm.Enabled = false;
        _confirm.Visible = false;
        _cancel.Text = "关闭";
        AcceptButton = _cancel;
        CancelButton = _cancel;
    }

    internal static void Field(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = EditorLayout.WrappingLabel(label, 10, UiTheme.Ink); caption.Padding = new Padding(0, 7, 14, 7);
        caption.Margin = Padding.Empty;
        caption.TextAlign = ContentAlignment.MiddleLeft;
        control.Margin = new Padding(0, 5, 0, 10);
        control.AccessibleName ??= label;
        control.TabIndex = row;
        UiTheme.StyleInput(control);
        if (control is not CheckBox) control.Dock = DockStyle.Fill;
        table.Controls.Add(caption, 0, row); table.Controls.Add(UiTheme.InputSurface(control), 1, row);
    }
    internal static void Section(TableLayoutPanel table, string title, string? description = null) =>
        EditorLayout.Section(table, title, description);
    internal static void Hint(TableLayoutPanel table, string text)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = EditorLayout.WrappingLabel(text, 10, UiTheme.Muted);
        label.Padding = new Padding(12, 10, 12, 10); label.Margin = new Padding(0, 8, 0, 8); label.BackColor = UiTheme.Background;
        table.Controls.Add(label, 0, row); table.SetColumnSpan(label, 2);
    }
    protected static TextBox TextInput(string value) => new() { Text = value, MinimumSize = new Size(150, 30), BorderStyle = BorderStyle.FixedSingle };
    protected static NumericUpDown NumberInput(int value, int minimum, int maximum) => new() { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), ThousandsSeparator = false, BorderStyle = BorderStyle.FixedSingle };
    protected static ComboBox SelectInput(object[] values, object? selected)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; box.Items.AddRange(values); box.SelectedItem = selected;
        EditorLayout.ConfigureDropDown(box);
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }
    protected static void BindChoice(ComboBox box, object[] values, object? selected)
    {
        box.Items.Clear(); box.Items.AddRange(values); box.SelectedItem = selected;
        EditorLayout.ConfigureDropDown(box);
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    protected static void SetFieldVisible(Control input, bool visible)
    {
        var surface = input.Parent is InputFrame frame ? (Control)frame : input;
        surface.Visible = visible;
        if (surface.Parent is TableLayoutPanel table)
            table.GetControlFromPosition(0, table.GetRow(surface))!.Visible = visible;
    }

    protected static string Required(TextBox box, string label)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) throw new EditorValidationException(label + "不能为空。", box);
        return box.Text.Trim();
    }
}
