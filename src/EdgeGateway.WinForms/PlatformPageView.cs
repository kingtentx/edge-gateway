namespace EdgeGateway.WinForms;

/// <summary>
/// Fixed platform settings view. It can be constructed without an application,
/// database, driver catalog, worker process, or runtime configuration.
/// </summary>
internal sealed partial class PlatformPageView : UserControl
{
    private DpiAwareForm? _dpiOwner;
    private bool _arranging;

    public PlatformPageView()
    {
        InitializeComponent();
        Fields = new Dictionary<string, Control>(StringComparer.Ordinal)
        {
            ["mqttEnabled"] = mqttEnabledInput,
            ["platform"] = platformInput,
            ["host"] = hostInput,
            ["port"] = portInput,
            ["tls"] = tlsInput,
            ["clientId"] = clientIdInput,
            ["mqttTimeout"] = mqttTimeoutInput,
            ["username"] = usernameInput,
            ["accessToken"] = accessTokenInput,
            ["password"] = passwordInput,
            ["topic"] = topicInput,
            ["deviceType"] = deviceTypeInput,
            ["typeKey"] = typeKeyInput,
            ["platformGatewayId"] = platformGatewayIdInput,
            ["reportMode"] = reportModeInput,
            ["publishInterval"] = publishIntervalInput,
            ["heartbeatInterval"] = heartbeatIntervalInput,
            ["maxOutbox"] = maxOutboxInput,
            ["rpcEnabled"] = rpcEnabledInput,
            ["remoteWrite"] = remoteWriteInput,
            ["rpcTimeout"] = rpcTimeoutInput,
            ["rpcRequest"] = rpcRequestInput,
            ["rpcResponse"] = rpcResponseInput,
            ["gatewayId"] = gatewayIdInput,
            ["name"] = gatewayNameInput,
            ["autoStart"] = autoStartInput
        };
        connectionViewport.SizeChanged += OnLayoutSizeChanged;
        reportingViewport.SizeChanged += OnLayoutSizeChanged;
        rpcViewport.SizeChanged += OnLayoutSizeChanged;
        gatewayViewport.SizeChanged += OnLayoutSizeChanged;
        applyBar.SizeChanged += OnLayoutSizeChanged;
        draftState.TextChanged += OnLayoutSizeChanged;
    }

    internal IReadOnlyDictionary<string, Control> Fields { get; }
    internal TabControl PlatformTabs => platformTabs;
    internal TableLayoutPanel ConnectionFields => connectionFields;
    internal TableLayoutPanel ReportingFields => reportingFields;
    internal TableLayoutPanel RpcFields => rpcFields;
    internal TableLayoutPanel GatewayFields => gatewayFields;
    internal Label DraftState => draftState;
    internal AntdUI.Button ApplyButton => applyButton;
    internal AntdUI.Button ResetButton => resetButton;
    internal AntdUI.Button ManageDriversButton => manageDriversButton;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dpiOwner = FindForm() as DpiAwareForm;
        if (_dpiOwner is not null) _dpiOwner.DpiLayoutCompleted += OnLayoutSizeChanged;
        ArrangeFields();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_dpiOwner is not null) _dpiOwner.DpiLayoutCompleted -= OnLayoutSizeChanged;
        _dpiOwner = null;
        base.OnHandleDestroyed(e);
    }

    private void OnLayoutSizeChanged(object? sender, EventArgs e) => ArrangeFields();

    private void ArrangeFields()
    {
        if (_arranging || IsDisposed || FindForm() is DpiAwareForm { IsDpiLayoutReady: false }) return;
        _arranging = true;
        try
        {
            FitFields(connectionFields);
            FitFields(reportingFields);
            FitFields(rpcFields);
            FitFields(gatewayFields);
            draftState.MaximumSize = new Size(Math.Max(1, applyBar.ClientSize.Width
                - applyBar.Padding.Horizontal - draftState.Margin.Horizontal), 0);
        }
        finally { _arranging = false; }
    }

    private static void FitFields(TableLayoutPanel table)
    {
        var available = Math.Max(1, table.ClientSize.Width - table.Padding.Horizontal);
        var labelWidth = Math.Min(UiTheme.Scale(table, 180), Math.Max(1, (int)(available * .42)));
        if (table.ColumnStyles[0].Width != labelWidth) table.ColumnStyles[0].Width = labelWidth;
        foreach (var label in table.Controls.OfType<Label>())
        {
            var width = table.GetColumnSpan(label) > 1 ? available : labelWidth;
            label.MaximumSize = new Size(Math.Max(1, width - label.Margin.Horizontal), 0);
        }
    }
}
