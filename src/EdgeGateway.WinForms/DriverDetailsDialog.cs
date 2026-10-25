using System.Text;
using EdgeGateway.Application;

namespace EdgeGateway.WinForms;

internal sealed class DriverDetailsDialog : DpiAwareForm
{
    public DriverDetailsDialog(DriverDetails details)
    {
        Text = "驱动详情 · " + details.Driver.Name;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterParent;
        ConfigureWindow(new Size(760, 600), new Size(520, 360));
        var text = new TextBox
        {
            Name = "DriverDetailsText", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White,
            BorderStyle = BorderStyle.None, Text = Describe(details)
        };
        var close = UiTheme.Button("关闭", (_, _) => Close());
        CancelButton = close;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(UiTheme.Toolbar(close), 0, 1);
        Controls.Add(layout);
    }

    private static string Describe(DriverDetails details)
    {
        var driver = details.Driver;
        var text = new StringBuilder();
        text.AppendLine($"驱动名称：{driver.Name}").AppendLine($"驱动标识：{driver.Id}")
            .AppendLine($"来源：{(driver.Source == "uploaded" ? "上传安装" : "随程序提供 / 已注册")}")
            .AppendLine($"状态：{driver.Status}").AppendLine($"版本：{driver.Version}")
            .AppendLine($"程序集：{driver.AssemblyName}").AppendLine($"SHA-256：{driver.Sha256}");
        if (details.Descriptor is { } descriptor)
        {
            text.AppendLine().AppendLine("功能说明：").AppendLine(descriptor.Description)
                .AppendLine($"写入能力：{(descriptor.SupportsWrite ? "支持（具体权限以点位配置为准）" : "只读")}")
                .AppendLine($"运行前提：{(string.IsNullOrWhiteSpace(descriptor.Prerequisites) ? "无额外声明" : descriptor.Prerequisites)}")
                .AppendLine().AppendLine($"配置参数（{descriptor.Parameters.Count}）：");
            foreach (var parameter in descriptor.Parameters)
                text.AppendLine($"• {parameter.Name} ({parameter.Key}) · {parameter.TypeName}：{parameter.Description}");
            text.AppendLine().AppendLine($"读取方法（{descriptor.Methods.Count}）：");
            foreach (var method in descriptor.Methods)
                text.AppendLine($"• {method.DisplayName} ({method.Name})：{method.Description}");
        }
        else text.AppendLine().AppendLine("插件未加载，无法读取能力和参数描述；可更新 DLL 或卸载安装记录。");
        text.AppendLine().AppendLine("同 DLL 受影响驱动：" + string.Join("、", details.RelatedDriverIds));
        text.AppendLine().AppendLine($"这些驱动的引用设备（{details.Devices.Count}）：");
        foreach (var device in details.Devices)
            text.AppendLine($"• {device.Name} · {(device.IsRunning ? "采集中" : "已停止")} · {device.Id}");
        if (details.Devices.Count == 0) text.AppendLine("无设备引用。");
        text.AppendLine().AppendLine("内置与上传驱动均支持更新、删除。同 DLL 的驱动一并处理；更新需停止引用设备、保持完整驱动标识与程序集名称并提高版本；删除需解除所有设备引用。");
        return text.ToString();
    }
}
