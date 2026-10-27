using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private SmokeFocusDiagnostics? _smokeFocusDiagnostics;

    private SmokeFocusDiagnostics? StartSmokeFocusDiagnostics(string directory)
    {
        if (!_smokeTest || !_layoutMatrix) return null;
        var diagnostics = new SmokeFocusDiagnostics(this, Path.Combine(directory, "focus-native.jsonl"));
        try
        {
            diagnostics.Start();
            _smokeFocusDiagnostics = diagnostics;
            diagnostics.Capture("matrix diagnostics started");
            return diagnostics;
        }
        catch (Exception exception)
        {
            diagnostics.Dispose();
            Console.Error.WriteLine("Could not start native focus diagnostics: " + exception.Message);
            return null;
        }
    }

    /// <summary>Smoke-only observation. Never changes window styles, focus, redraw, visibility or enablement.</summary>
    private sealed class SmokeFocusDiagnostics(MainForm main, string path) : IDisposable
    {
        private const int MaximumRecords = 256, MaximumMessages = 256, MaximumBytes = 8 * 1024 * 1024;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<Control, WatchedWindow> _watched = [];
        private readonly Queue<object> _messages = new();
        private readonly Control _navigation = main.navigationMenu;
        private readonly Control? _deviceCommand = Descendants(main._pages["devices"]).OfType<Button>().FirstOrDefault();
        private StreamWriter? _writer;
        private string _stage = "initialization";
        private long _messageSequence, _droppedMessages;
        private int _records, _bytes;
        private bool _disposed, _reportedError;

        public void Start()
        {
            _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            WatchChain(main); WatchChain(_navigation); WatchChain(_deviceCommand);
        }

        public void Capture(string stage, Control? target = null, Control? requested = null, Form? window = null,
            bool failure = false, int? attempts = null, double? elapsedMilliseconds = null)
        {
            if (_disposed || _records >= MaximumRecords) return;
            try
            {
                _stage = stage;
                target ??= _deviceCommand;
                window ??= main;
                var controls = new List<Control>();
                foreach (var root in new Control?[] { main, _navigation, target, requested, window })
                {
                    WatchChain(root);
                    for (var control = root; control is not null; control = control.Parent)
                        if (!controls.Contains(control)) controls.Add(control);
                }
                var mainState = ReadNative(ExistingHandle(main));
                var navigationState = ReadNative(ExistingHandle(_navigation));
                var targetState = ReadNative(ExistingHandle(target));
                var record = new
                {
                    Kind = failure ? "focus-failure" : "checkpoint", Stage = stage,
                    AtUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = _clock.Elapsed.TotalMilliseconds,
                    Attempts = attempts, FocusRetryElapsedMilliseconds = elapsedMilliseconds,
                    Target = Label(target), Requested = Label(requested), Window = Label(window),
                    Thread = ReadThread(mainState.ThreadId),
                    Controls = controls.Take(32).Select(SnapshotControl).ToArray(),
                    OpenForms = System.Windows.Forms.Application.OpenForms.Cast<Form>().Take(16).Select(form => new
                    {
                        Control = SnapshotControl(form), form.Modal, form.TopLevel, WindowState = form.WindowState.ToString(),
                        Owner = Label(form.Owner), OwnerHandle = Hex(ExistingHandle(form.Owner)),
                        ActiveControl = Label(form.ActiveControl), OwnedForms = form.OwnedForms.Length
                    }).ToArray(),
                    MessageSequence = _messageSequence, DroppedMessages = _droppedMessages,
                    RecentMessages = failure ? _messages.ToArray() : null
                };
                var json = JsonSerializer.Serialize(record);
                var size = Encoding.UTF8.GetByteCount(json) + 1;
                _records++;
                if (_writer is not null && _bytes + size <= MaximumBytes)
                {
                    _writer.WriteLine(json); _bytes += size;
                }
                Console.WriteLine($"Focus native [{stage}]: main={Summary(mainState)}; navigation={Summary(navigationState)}; target={Summary(targetState)}; "
                    + $"messages={_messageSequence}; record={_records}; file={Path.GetFileName(path)}.");
                if (failure)
                {
                    // Full ancestor/OpenForms/message evidence stays in the bounded artifact; native root state is also visible in CI output.
                    Console.WriteLine("Focus native failure thread: " + JsonSerializer.Serialize(ReadThread(mainState.ThreadId)));
                    foreach (var control in controls.Take(32))
                        Console.WriteLine("Focus native failure control: " + JsonSerializer.Serialize(SnapshotControl(control)));
                }
            }
            catch (Exception exception) { ReportError(exception); }
        }

        private void WatchChain(Control? control)
        {
            for (var current = control; current is not null; current = current.Parent)
                if (!_watched.ContainsKey(current))
                {
                    var watch = new WatchedWindow(this, current, ReferenceEquals(current, main));
                    _watched.Add(current, watch);
                    watch.Start();
                }
        }

        private void Record(Control control, string message, IntPtr handle, IntPtr wParam, IntPtr lParam,
            string? detail, NativeState? before, NativeState? after, string? stack = null, double? startedMilliseconds = null)
        {
            if (_disposed) return;
            if (_messages.Count == MaximumMessages) { _messages.Dequeue(); _droppedMessages++; }
            _messages.Enqueue(new
            {
                Sequence = ++_messageSequence, ElapsedMilliseconds = _clock.Elapsed.TotalMilliseconds,
                StartedMilliseconds = startedMilliseconds,
                Stage = _stage, Control = Label(control), Handle = Hex(handle), Message = message,
                WParam = Hex(wParam), LParam = Hex(lParam), Detail = detail, Before = before, After = after, Stack = stack
            });
        }

        private static object SnapshotControl(Control control) => new
        {
            Control = Label(control), Parent = Label(control.Parent), control.Visible, control.Enabled,
            control.IsHandleCreated, control.IsDisposed, control.Disposing, control.CanFocus, control.ContainsFocus,
            Bounds = control.Bounds.ToString(), Native = ReadNative(ExistingHandle(control))
        };

        private static object ReadThread(uint threadId)
        {
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            var available = Native.GetGUIThreadInfo(threadId, ref info);
            var error = available ? 0 : Marshal.GetLastWin32Error();
            return new
            {
                ThreadId = threadId, Active = Hex(Native.GetActiveWindow()), Focus = Hex(Native.GetFocus()),
                Foreground = ReadNative(Native.GetForegroundWindow()), GuiInfoAvailable = available,
                GuiInfoError = error, info.Flags,
                GuiActive = Hex(info.Active), GuiFocus = Hex(info.Focus), GuiCapture = Hex(info.Capture),
                GuiMenuOwner = Hex(info.MenuOwner), GuiMoveSize = Hex(info.MoveSize), GuiCaret = Hex(info.Caret)
            };
        }

        private static NativeState ReadNative(IntPtr handle)
        {
            var valid = handle != IntPtr.Zero && Native.IsWindow(handle);
            var style = valid ? unchecked((uint)Native.GetWindowLong(handle, -16)) : 0;
            uint processId = 0;
            var threadId = valid ? Native.GetWindowThreadProcessId(handle, out processId) : 0;
            return new(Hex(handle), valid, valid && Native.IsWindowVisible(handle), valid && Native.IsWindowEnabled(handle),
                $"0x{style:X8}", (style & 0x10000000) != 0, (style & 0x08000000) != 0,
                Hex(valid ? Native.GetProp(handle, "SysSetRedraw") : IntPtr.Zero),
                Hex(valid ? Native.GetParent(handle) : IntPtr.Zero), Hex(valid ? Native.GetWindow(handle, 4) : IntPtr.Zero),
                Hex(valid ? Native.GetAncestor(handle, 2) : IntPtr.Zero), threadId, processId);
        }

        private static IntPtr ExistingHandle(Control? control) => control is { IsHandleCreated: true } ? control.Handle : IntPtr.Zero;
        private static string Label(Control? control) => control is null ? "<none>" : control.GetType().Name + ":" + control.Name;
        private static string Hex(IntPtr value) => "0x" + unchecked((ulong)value.ToInt64()).ToString("X");
        private static string Summary(NativeState value) => $"{value.Handle}/valid={value.Valid}/visible={value.Visible}/enabled={value.Enabled}/style={value.Style}/redraw={value.SysSetRedraw}";
        private sealed record NativeState(string Handle, bool Valid, bool Visible, bool Enabled, string Style,
            bool StyleVisible, bool StyleDisabled, string SysSetRedraw, string Parent, string Owner, string Root, uint ThreadId, uint ProcessId);

        private void ReportError(Exception exception)
        {
            if (_reportedError) return;
            _reportedError = true;
            Console.Error.WriteLine("Native focus diagnostics could not record evidence: " + exception.Message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(main._smokeFocusDiagnostics, this)) main._smokeFocusDiagnostics = null;
            foreach (var watch in _watched.Values)
                try { watch.Dispose(); } catch (Exception exception) { ReportError(exception); }
            try { _writer?.Dispose(); } catch (Exception exception) { ReportError(exception); }
        }

        private sealed class WatchedWindow(SmokeFocusDiagnostics trace, Control control, bool mainWindow) : NativeWindow, IDisposable
        {
            public void Start()
            {
                control.HandleCreated += HandleCreated;
                control.HandleDestroyed += HandleDestroyed;
                Attach();
            }
            private void Attach()
            {
                if (!control.IsHandleCreated || control.IsDisposed) return;
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(control.Handle);
                trace.Record(control, "HandleAttached", Handle, IntPtr.Zero, IntPtr.Zero, null, null, ReadNative(Handle));
            }
            private void HandleCreated(object? sender, EventArgs args)
            {
                try { Attach(); } catch (Exception exception) { trace.ReportError(exception); }
            }
            private void HandleDestroyed(object? sender, EventArgs args)
            {
                try { trace.Record(control, "HandleDestroyed", Handle, IntPtr.Zero, IntPtr.Zero, null, ReadNative(Handle), null); }
                catch (Exception exception) { trace.ReportError(exception); }
            }

            protected override void WndProc(ref Message message)
            {
                string? name = null, detail = null, stack = null;
                NativeState? before = null;
                var handle = message.HWnd; var wParam = message.WParam; var lParam = message.LParam;
                var startedMilliseconds = trace._clock.Elapsed.TotalMilliseconds;
                try
                {
                    name = message.Msg switch
                    {
                        0x0002 => "WM_DESTROY", 0x0006 => "WM_ACTIVATE", 0x0007 => "WM_SETFOCUS", 0x0008 => "WM_KILLFOCUS",
                        0x000A => "WM_ENABLE", 0x000B => "WM_SETREDRAW", 0x0018 => "WM_SHOWWINDOW",
                        0x007C => "WM_STYLECHANGING", 0x007D => "WM_STYLECHANGED", 0x0082 => "WM_NCDESTROY",
                        0x0086 => "WM_NCACTIVATE", 0x02E0 => "WM_DPICHANGED", 0x0317 => "WM_PRINT", 0x0318 => "WM_PRINTCLIENT", _ => null
                    };
                    if (message.Msg is 0x0046 or 0x0047 && lParam != IntPtr.Zero)
                    {
                        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                        if (mainWindow || (position.Flags & 0xC0) != 0)
                        {
                            name = message.Msg == 0x0046 ? "WM_WINDOWPOSCHANGING" : "WM_WINDOWPOSCHANGED";
                            detail = $"flags=0x{position.Flags:X}; x={position.X}; y={position.Y}; width={position.Width}; height={position.Height}";
                        }
                    }
                    if (name is not null)
                    {
                        before = ReadNative(handle);
                        if (message.Msg is 0x007C or 0x007D && lParam != IntPtr.Zero)
                        {
                            var style = Marshal.PtrToStructure<StyleChange>(lParam);
                            detail = $"index={wParam.ToInt64()}; old=0x{style.Old:X8}; new=0x{style.New:X8}";
                        }
                        if (message.Msg == 0x000B && wParam == IntPtr.Zero)
                        {
                            stack = Environment.StackTrace;
                            if (stack.Length > 6000) stack = stack[..6000];
                        }
                    }
                }
                catch (Exception exception) { trace.ReportError(exception); }
                try { base.WndProc(ref message); }
                finally
                {
                    if (name is not null)
                        try { trace.Record(control, name, handle, wParam, lParam, detail, before, ReadNative(handle), stack, startedMilliseconds); }
                        catch (Exception exception) { trace.ReportError(exception); }
                }
            }
            public void Dispose()
            {
                control.HandleCreated -= HandleCreated;
                control.HandleDestroyed -= HandleDestroyed;
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPosition
        {
            public IntPtr Window, InsertAfter;
            public int X, Y, Width, Height;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StyleChange { public uint Old, New; }
        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int CaretLeft, CaretTop, CaretRight, CaretBottom;
        }
        private static class Native
        {
            [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowEnabled(IntPtr window);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr window, int index);
            [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] public static extern IntPtr GetProp(IntPtr window, string name);
            [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
            [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
            [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
            [DllImport("user32.dll")] public static extern IntPtr GetFocus();
            [DllImport("user32.dll")] public static extern IntPtr GetActiveWindow();
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        }
    }
}
