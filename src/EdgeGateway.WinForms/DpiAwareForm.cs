namespace EdgeGateway.WinForms;

/// <summary>
/// Common DPI policy for all windows. WinForms owns scaling; this class only constrains
/// window geometry to the active monitor's work area (including taskbar and negative origins).
/// </summary>
public partial class DpiAwareForm : AntdUI.Window
{
    private Size _logicalMinimumClientSize = new(480, 280);
    private bool _initializing = true;
    private bool _initialScaleCompleted;
    private bool _fitting;
    private bool _dpiChanging;
    internal bool IsDpiLayoutReady => !_initializing && !_dpiChanging;
    internal event EventHandler? DpiLayoutCompleted;

    public DpiAwareForm()
    {
        // Keep the entire derived constructor at its original 96-DPI design baseline.
        // Resuming before all controls exist causes only a partial tree to scale.
        SuspendLayout();
        AutoHandDpi = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Microsoft YaHei UI", 10F);
        DoubleBuffered = true;
        InitializeWindowComponents();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        windowHeader?.SendToBack();
    }
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (windowHeader is not null) windowHeader.Text = Text;
    }

    protected void ConfigureWindow(Size logicalClientSize, Size logicalMinimumClientSize)
    {
        if (!_initializing) throw new InvalidOperationException("ConfigureWindow is only valid during construction.");
        _logicalMinimumClientSize = logicalMinimumClientSize;
        ClientSize = logicalClientSize;
        // Do not set a large, fixed MinimumSize before WinForms loads/scales the form.
        // .NET 8 also scales that property and could make a dialog larger than the monitor.
    }

    protected override void CreateHandle()
    {
        if (!_initialScaleCompleted)
        {
            _initialScaleCompleted = true;
            // Establish the design-to-system-DPI baseline before HandleCreated can
            // scale the font for a different monitor. OnFontChanged otherwise consumes
            // that baseline while the initial bounds are still marked for scaling.
            ResumeLayout(true);
            PerformAutoScale();
        }
        base.CreateHandle();
    }

    protected override void OnLoad(EventArgs e)
    {
        _initializing = false;
        windowHeader.MaximizeBox = MaximizeBox;
        windowHeader.MinimizeBox = MinimizeBox;
        base.OnLoad(e);
        FitToWorkingArea(center: true);
        CompleteDpiLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Let .NET scale fonts, nested controls and suggested bounds exactly once.
        _dpiChanging = true;
        try { base.OnDpiChanged(e); }
        finally { _dpiChanging = false; }
        if (!_initializing && !e.Cancel)
        {
            FitToWorkingArea();
            CompleteDpiLayout();
        }
    }

    private void CompleteDpiLayout()
    {
        DpiLayoutCompleted?.Invoke(this, EventArgs.Empty);
        LayoutChildren(this);
        static void LayoutChildren(Control control)
        {
            control.PerformLayout();
            foreach (Control child in control.Controls) LayoutChildren(child);
            control.PerformLayout();
        }
        Invalidate(true);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!_initializing) FitToWorkingArea();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Resolution, taskbar and remote-session work-area changes can occur without a
        // DPI change. Schedule after Windows has finished applying the new monitor data.
        if (!_initializing && !_fitting && IsHandleCreated && !IsDisposed
            && (m.Msg == 0x007E || m.Msg == 0x001A))
            BeginInvoke((Action)(() => { if (!IsDisposed) FitToWorkingArea(); }));
    }

    internal void FitToWorkingArea(bool center = false, Rectangle? testWorkArea = null)
    {
        if (_fitting || _initializing || IsDisposed) return;
        _fitting = true;
        try
        {
            var screen = center && Owner is not null ? Screen.FromControl(Owner) : Screen.FromHandle(Handle);
            var area = testWorkArea ?? screen.WorkingArea;
            var nonClient = new Size(Math.Max(0, Width - ClientSize.Width), Math.Max(0, Height - ClientSize.Height));
            var minimum = DpiLayoutMath.MinimumWindow(_logicalMinimumClientSize, nonClient, DeviceDpi, area);
            if (MinimumSize != minimum) MinimumSize = minimum;
            if (WindowState != FormWindowState.Normal) return;
            var target = DpiLayoutMath.Fit(Bounds, area);
            if (center)
            {
                var anchor = Owner?.Bounds ?? area;
                target.Location = new Point(anchor.Left + (anchor.Width - target.Width) / 2,
                    anchor.Top + (anchor.Height - target.Height) / 2);
                target = DpiLayoutMath.Fit(target, area);
            }
            if (Bounds != target) Bounds = target;
        }
        finally { _fitting = false; }
    }
}
