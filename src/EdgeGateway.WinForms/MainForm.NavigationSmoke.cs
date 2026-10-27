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
            // A deliberately stale metric makes this lifecycle regression reproducible
            // at 96 DPI as well. This does not change or simulate the window's DPI.
            foreach (var (_, tabs) in pages)
            {
                if (tabs.IsHandleCreated)
                    throw new InvalidOperationException(tabs.Name + ": navigation regression must run before first display.");
                tabs.Padding = Point.Empty;
            }

            for (var pass = 0; pass < 3; pass++)
            foreach (var (key, tabs) in pages)
            {
                _navigation[key].PerformClick();
                PerformLayoutRecursively(this);
                if (_activePage != key || !_pages[key].Visible || !tabs.Visible)
                    throw new InvalidOperationException(key + ": navigation did not display the selected page.");
                var expected = new Point(UiTheme.Scale(tabs, 14), UiTheme.Scale(tabs, 6));
                if (!tabs.IsHandleCreated || !IsNavigationWindow(tabs.Handle) || tabs.Padding != expected)
                    throw new InvalidOperationException(key + ": deferred tab creation lost its native window or current DPI padding.");

                var handle = tabs.Handle;
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab;
                    PerformLayoutRecursively(tabs);
                    if (!tab.Visible || !tab.IsHandleCreated || !IsNavigationWindow(tab.Handle) || tabs.Handle != handle)
                        throw new InvalidOperationException(key + ": tab selection lost its native window or recreated the tab control.");
                }
                tabs.SelectedIndex = 0;
                _navigation["overview"].PerformClick();
            }
            SmokeCredentialInputs();
            Console.WriteLine($"Deferred tab navigation passed: database/platform/logs first display with stale padding, three menu cycles and all tabs, masked literal credential inputs at actual {DeviceDpi} DPI.");
        }
        finally { _navigation[originalPage].PerformClick(); }
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
            _navigation["cloud"].PerformClick();
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
