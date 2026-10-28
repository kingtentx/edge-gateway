namespace EdgeGateway.WinForms;

internal sealed record MenuCommand(string Key, string Text, Func<Task> Execute, Func<bool> CanExecute,
    string? DisabledReason = null, bool SeparateBefore = false, bool Dangerous = false, string? Shortcut = null);

/// <summary>AntdUI menu appearance with commands captured for the target at opening time.</summary>
internal sealed class CommandMenuButton : ModernButton
{
    private Func<IReadOnlyList<MenuCommand>> _commands = () => [];
    private Action<Exception> _onError = _ => { };
    private Form? _menu;
    private IReadOnlyList<MenuCommand> _openedCommands = [];
    private AntdUI.ContextMenuStripItem[] _openedItems = [];
    private bool _executing;

    public CommandMenuButton(string text, Func<IReadOnlyList<MenuCommand>> commands, Action<Exception> onError) : this()
    {
        Text = text; AccessibleName = text; Glyph = UiTheme.ActionIcon(text);
        Bind(commands, onError);
    }

    internal void Bind(Func<IReadOnlyList<MenuCommand>> commands, Action<Exception> onError)
    { _commands = commands; _onError = onError; }

    public CommandMenuButton()
    {
        AutoSize = true; MinimumSize = new Size(100, UiTheme.ActionHeight); Height = UiTheme.ActionHeight;
        Padding = new Padding(13, 6, 13, 6); Margin = new Padding(0, 0, 8, 6);
        Cursor = Cursors.Hand; ShowArrow = true; IsLink = true;
        AccessibleDescription = "下拉操作菜单；按 Alt+下箭头打开，方向键选择，Enter 执行，Escape 关闭。";
        Click += (_, _) => OpenMenu();
    }

    internal Form OpenedMenu => _menu ?? throw new InvalidOperationException("Menu has not been opened.");
    internal IReadOnlyList<MenuCommand> OpenedCommands => _openedCommands;
    internal IReadOnlyList<AntdUI.ContextMenuStripItem> OpenedItems => _openedItems;
    internal void OpenByKeyboardForSmoke()
    {
        var message = default(Message);
        if (!ProcessCmdKey(ref message, Keys.Alt | Keys.Down))
            throw new InvalidOperationException("The action menu did not handle its keyboard opening command.");
    }

    internal void OpenMenu()
    {
        if (!Enabled || !Visible || _executing) return;
        if (_menu is { IsDisposed: false, Visible: true }) { _menu.Close(); return; }
        _openedCommands = _commands();
        if (_openedCommands.Count == 0) return;
        var items = new List<AntdUI.IContextMenuStripItem>();
        foreach (var command in _openedCommands)
        {
            if (command.SeparateBefore && items.Count > 0) items.Add(new AntdUI.ContextMenuStripItemDivider());
            var enabled = command.CanExecute();
            items.Add(new AntdUI.ContextMenuStripItem(command.Text)
            {
                ID = command.Key, Tag = command, Enabled = enabled,
                SubText = enabled ? command.Shortcut : (command.DisabledReason ?? "当前状态下不可用").Split('；')[0],
                Fore = command.Dangerous ? AntdUI.Style.Get(AntdUI.Colour.Error, nameof(AntdUI.Button)) : null
            });
        }
        _openedItems = items.OfType<AntdUI.ContextMenuStripItem>().ToArray();
        _menu = AntdUI.ContextMenuStrip.open(new AntdUI.ContextMenuStrip.Config(this, async item =>
        {
            if (item is AntdUI.ContextMenuStripItem { Tag: MenuCommand command }) await ExecuteCommandAsync(command);
        }, items.ToArray())
        {
            Font = Font, Location = PointToScreen(new Point(0, Height)), Align = AntdUI.TAlign.BR,
            OnClose = () => { if (!IsDisposed) { ArrowProg = -1; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.StateChange, -1); } }
        });
        if (_menu is not null)
        {
            ArrowProg = 1; Invalidate();
            _menu.AccessibleName = Text + "操作菜单";
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    internal async Task ExecuteCommandAsync(MenuCommand command)
    {
        if (_executing || IsDisposed || !Enabled || !command.CanExecute()) return;
        _executing = true;
        try
        {
            if (_menu is { IsDisposed: false }) _menu.Close();
            await command.Execute();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _onError(exception); }
        finally { _executing = false; }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Down || keyData == (Keys.Alt | Keys.Down)) { OpenMenu(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Expose the captured commands through the trigger as well as the painted popup.
    // Command availability is rechecked on activation, including accessibility actions.
    protected override AccessibleObject CreateAccessibilityInstance() => new MenuAccessibleObject(this);
    private sealed class MenuAccessibleObject(CommandMenuButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.ButtonDropDown;
        public override AccessibleStates State => base.State | AccessibleStates.HasPopup
            | (owner._menu is { IsDisposed: false, Visible: true } ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
        public override string DefaultAction => "打开操作菜单";
        public override void DoDefaultAction() => owner.OpenMenu();
        public override int GetChildCount() => owner._menu is { IsDisposed: false, Visible: true } ? owner._openedCommands.Count : 0;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < GetChildCount()
            ? new CommandAccessibleObject(owner, owner._openedCommands[index], this) : null;
    }
    private sealed class CommandAccessibleObject(CommandMenuButton owner, MenuCommand command, AccessibleObject parent) : AccessibleObject
    {
        public override string? Name { get => command.Text; set { } }
        public override string? Description => command.CanExecute() ? command.Shortcut : command.DisabledReason;
        public override AccessibleObject Parent => parent;
        public override AccessibleRole Role => AccessibleRole.MenuItem;
        public override AccessibleStates State => command.CanExecute() ? AccessibleStates.Focusable : AccessibleStates.Unavailable;
        public override string DefaultAction => "执行";
        public override void DoDefaultAction() => _ = owner.ExecuteCommandAsync(command);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu?.Dispose();
        base.Dispose(disposing);
    }
}
