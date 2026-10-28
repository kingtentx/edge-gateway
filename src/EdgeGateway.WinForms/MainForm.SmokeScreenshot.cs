using System.Runtime.InteropServices;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private static readonly List<SmokeScreenshotRestore> SmokeScreenshotRestores = [];

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetSmokeWindowStyle(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsSmokeWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsSmokeWindowEnabled(IntPtr window);

    private static void DrawSmokeWindow(Form window, Bitmap bitmap, string context)
    {
        Form? owner = window;
        while (owner is not null && owner is not MainForm) owner = owner.Owner;
        var main = owner as MainForm;
        if (main?._smokeTest != true || !window.IsHandleCreated)
            throw new InvalidOperationException("Window capture requires a shown, isolated smoke-test form.");
        var handle = window.Handle;
        var beforeStyle = unchecked((uint)GetSmokeWindowStyle(handle, -16));
        var beforeEnabled = IsSmokeWindowEnabled(handle);
        var beforeBounds = window.Bounds;
        if (!window.Visible || !IsSmokeWindowVisible(handle))
            throw new InvalidOperationException(context + ": window was already hidden before capture.");
        try
        {
            if (window is AntdUI.Window or AntdUI.BorderlessForm)
            {
                // These forms extend their client area into the caption. DrawToBitmap
                // requests PRF_NONCLIENT and adds a fictitious system title bar over the
                // AntdUI header on the isolated desktop. Capture the real client and
                // child HWNDs, without requesting a second non-client frame.
                using var graphics = Graphics.FromImage(bitmap);
                var dc = graphics.GetHdc();
                try { SendControlSmokeMessage(handle, 0x0317, dc, new IntPtr(0x14)); }
                finally { graphics.ReleaseHdc(dc); }
            }
            else window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        }
        finally
        {
            if (window.IsDisposed || !window.IsHandleCreated || window.Handle != handle)
                throw new InvalidOperationException(context + ": window lifetime changed during capture.");
            var afterStyle = unchecked((uint)GetSmokeWindowStyle(handle, -16));
            if (!window.Visible || window.Bounds != beforeBounds || IsSmokeWindowEnabled(handle) != beforeEnabled)
                throw new InvalidOperationException(context + ": window state changed during capture.");
            if (!IsSmokeWindowVisible(handle))
            {
                // Native WM_PRINT can leave only WS_VISIBLE cleared while WinForms
                // still reports Visible=true. The recorded before/after HWND trace
                // isolates this to synchronous DrawToBitmap, not an operator action.
                // Restore that one capture side effect, without focus, activation,
                // message pumping, redraw toggles or any general UI-state repair.
                if ((beforeStyle ^ afterStyle) != 0x10000000)
                    throw new InvalidOperationException(context + ": capture changed more than native visibility.");
                main._smokeFocusDiagnostics?.Capture(context + " capture lost native visibility", window, window, window);
                // SHOWWINDOW | NOMOVE | NOSIZE | NOZORDER | NOOWNERZORDER | NOACTIVATE.
                if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0257))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not restore capture visibility.");
                var restoredStyle = unchecked((uint)GetSmokeWindowStyle(handle, -16));
                if (!IsSmokeWindowVisible(handle) || restoredStyle != beforeStyle || window.Bounds != beforeBounds
                    || IsSmokeWindowEnabled(handle) != beforeEnabled)
                    throw new InvalidOperationException(context + ": capture visibility was not restored exactly.");
                SmokeScreenshotRestores.Add(new(context, $"0x{beforeStyle:X8}", $"0x{afterStyle:X8}", $"0x{restoredStyle:X8}"));
                Console.WriteLine($"Screenshot visibility restored: {context}; native style 0x{beforeStyle:X8} -> 0x{afterStyle:X8} -> 0x{restoredStyle:X8}; focus assertions remain required.");
                main._smokeFocusDiagnostics?.Capture(context + " capture visibility restored", window, window, window);
            }
        }
    }

    private sealed record SmokeScreenshotRestore(string Context, string BeforeStyle, string CapturedStyle, string RestoredStyle);
}
