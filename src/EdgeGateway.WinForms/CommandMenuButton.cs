namespace EdgeGateway.WinForms;

internal sealed record MenuCommand(string Key, string Text, Func<Task> Execute, Func<bool> CanExecute,
    string? DisabledReason = null, bool SeparateBefore = false, bool Dangerous = false);

/// <summary>A keyboard accessible native menu with actions created for the selected target when it opens.</summary>
internal sealed class CommandMenuButton : ModernButton
{
    private Func<IReadOnlyList<MenuCommand>> _commands = () => [];
    private Action<Exception> _onError = _ => { };
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false, ShowCheckMargin = false };
    private IReadOnlyList<MenuCommand> _openedCommands = [];

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
        Cursor = Cursors.Hand;
        _menu.BackColor = Color.White; _menu.ForeColor = UiTheme.Ink;
        _menu.Renderer = new MenuRenderer();
        Click += (_, _) => OpenMenu();
    }

    internal ContextMenuStrip OpenedMenu => _menu;
    internal IReadOnlyList<MenuCommand> OpenedCommands => _openedCommands;
    internal void OpenByKeyboardForSmoke()
    {
        var message = default(Message);
        if (!ProcessCmdKey(ref message, Keys.Alt | Keys.Down))
            throw new InvalidOperationException("The action menu did not handle its keyboard opening command.");
    }

    internal void OpenMenu()
    {
        if (!Enabled || !Visible) return;
        foreach (var old in _menu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        _menu.Items.Clear(); _menu.Font = Font;
        _openedCommands = _commands();
        foreach (var command in _openedCommands)
        {
            if (command.SeparateBefore && _menu.Items.Count > 0) _menu.Items.Add(new ToolStripSeparator());
            var item = new ToolStripMenuItem(command.Text)
            {
                Name = command.Key, Enabled = command.CanExecute(),
                Padding = UiTheme.Scale(this, new Padding(14, 7, 20, 7)),
                ForeColor = command.Dangerous ? UiTheme.Red : UiTheme.Ink,
                ToolTipText = command.CanExecute() ? command.Text : command.DisabledReason ?? "当前状态下不可用"
            };
            item.Click += async (_, _) =>
            {
                if (!command.CanExecute()) return;
                try { await command.Execute(); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { _onError(exception); }
            };
            _menu.Items.Add(item);
        }
        _menu.Show(this, new Point(0, Height));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Down || keyData == (Keys.Alt | Keys.Down)) { OpenMenu(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => UiTheme.BlueSoft;
            public override Color MenuItemBorder => UiTheme.BlueSoft;
            public override Color MenuBorder => UiTheme.Border;
            public override Color ToolStripDropDownBackground => Color.White;
            public override Color SeparatorDark => UiTheme.Border;
            public override Color SeparatorLight => Color.White;
        }
    }
}
