using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

internal sealed partial class ChannelEditor : EditorDialog
{
    public ChannelConfiguration Value { get; }
    public ChannelEditor() : base("通信通道", "设置名称与通信间隔。")
    { Value = new(); InitializeComponent(); }
    public ChannelEditor(ChannelConfiguration value) : base("通信通道", "同一通道的采集、诊断和写入依次执行；不同通道并行。设备连接参数仍在设备配置中设置。")
    {
        Value = value;
        InitializeComponent();
        _name.Text = value.Name; _description.Text = value.Description;
        _delay.Value = Math.Clamp(value.InterRequestDelayMs, 0, 60000);
    }
    protected override void Commit()
    {
        Value.Name = Required(_name, "通道名称");
        Value.InterRequestDelayMs = (int)_delay.Value;
        Value.Description = _description.Text.Trim();
    }
}
