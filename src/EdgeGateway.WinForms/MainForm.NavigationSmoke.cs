using System.Runtime.InteropServices;
using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsNavigationWindow(IntPtr window);

    private void SmokeDeferredPageNavigation()
    {
        var originalPage = _activePage;
        var pages = new[] { ("database", _databaseTabs), ("cloud", _cloudTabs), ("logs", _logTabs) };
        try
        {
            // Every AntdUI page must survive deferred creation and repeated selection.
            foreach (var (_, tabs) in pages)
            {
                if (tabs.IsHandleCreated)
                    throw new InvalidOperationException(tabs.Name + ": navigation regression must run before first display.");
            }

            for (var pass = 0; pass < 3; pass++)
            foreach (var (key, tabs) in pages)
            {
                navigationMenu.Select(_navigation[key]);
                PerformLayoutRecursively(this);
                if (_activePage != key || !_pages[key].Visible || !tabs.Visible)
                    throw new InvalidOperationException(key + ": navigation did not display the selected page.");
                if (!tabs.IsHandleCreated || !IsNavigationWindow(tabs.Handle) || tabs is not AntdUI.Tabs)
                    throw new InvalidOperationException(key + ": deferred AntdUI tab creation failed.");

                var handle = tabs.Handle;
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab;
                    System.Windows.Forms.Application.DoEvents();
                    PerformLayoutRecursively(tabs);
                    if (!tab.Visible || !tab.IsHandleCreated || !IsNavigationWindow(tab.Handle) || tabs.Handle != handle)
                        throw new InvalidOperationException(key + ": tab selection lost its native window or recreated the tab control.");
                    AssertVisibleBounds(tab, key + " AntdUI tab content");
                }
                tabs.SelectedIndex = 0;
                var command = typeof(GatewayTabs).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                command.Invoke(tabs, [new Message(), Keys.Control | Keys.Tab]);
                if (tabs.SelectedIndex != 1) throw new InvalidOperationException(key + ": Ctrl+Tab did not select the next AntdUI tab.");
                command.Invoke(tabs, [new Message(), Keys.Control | Keys.Shift | Keys.Tab]);
                if (tabs.SelectedIndex != 0) throw new InvalidOperationException(key + ": Ctrl+Shift+Tab did not restore the first AntdUI tab.");
                navigationMenu.Select(_navigation["overview"]);
            }
            SmokeCredentialInputs();
            Console.WriteLine($"Deferred tab navigation passed: AntdUI database/platform/logs first display, three menu cycles, Ctrl+Tab in both directions and all page bounds, masked literal credential inputs at actual {DeviceDpi} DPI.");
        }
        finally { navigationMenu.Select(_navigation[originalPage]); }
    }

    private void SmokeCredentialInputs()
    {
        const string literal = "  ui-test-secret;with quotes\"!  ";
        var source = new DatabaseConnectionConfiguration { Password = literal };
        using (var editor = new DatabaseConnectionDialog(source, _application))
        {
            editor.Show(this);
            try
            {
                var password = (TextBox)editor.SmokeInputs.Single(input => input.Name == "DatabasePassword");
                if (!password.UseSystemPasswordChar || password.Text != literal || editor.CommitForSmoke().Password != literal)
                    throw new InvalidOperationException("Database password input did not mask and preserve the literal credential.");
                password.Text = "";
                if (editor.CommitForSmoke().Password != "" || source.Password != literal)
                    throw new InvalidOperationException("Empty password or cancelled draft isolation failed.");
            }
            finally { editor.Close(); }
        }
        var passwordInput = (TextBox)_cloudFields["password"];
        var tokenInput = (TextBox)_cloudFields["accessToken"];
        var savedPassword = passwordInput.Text;
        var savedToken = tokenInput.Text;
        var wasDirty = _dirty;
        try
        {
            navigationMenu.Select(_navigation["cloud"]);
            passwordInput.Text = tokenInput.Text = literal;
            if (!passwordInput.UseSystemPasswordChar || !tokenInput.UseSystemPasswordChar
                || CloudDraft.Mqtt.Password != literal || CloudDraft.Mqtt.AccessToken != literal)
                throw new InvalidOperationException("Platform credential inputs did not mask and preserve literal values.");
        }
        finally
        {
            passwordInput.Text = savedPassword; tokenInput.Text = savedToken;
            _dirty = wasDirty; UpdateCommands();
        }
    }
}
