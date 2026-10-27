using System.Text;
using EdgeGateway.Application;

namespace EdgeGateway.WinForms;

internal sealed partial class DriverDetailsDialog : DpiAwareForm
{
    public DriverDetailsDialog() { InitializeComponent(); }

    public DriverDetailsDialog(string name, IReadOnlyList<DriverDetails> details)
    {
        Text = "驱动详情 · " + name;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterParent;
        ConfigureWindow(new Size(760, 600), new Size(520, 360));
        InitializeComponent();
        detailsText.Text = Describe(name, details);
        closeButton.Click += (_, _) => Close();
    }

    private static string Describe(string name, IReadOnlyList<DriverDetails> details)
    {
        var driver = details[0].Driver;
        var text = new StringBuilder();
        text.AppendLine($"驱动名称：{name}").AppendLine($"程序集：{driver.AssemblyName}");
        foreach (var item in details)
        {
            text.AppendLine().AppendLine($"{item.Driver.Name.Replace("（原版全模式）", "", StringComparison.Ordinal)} · {item.Driver.Id}")
                .AppendLine($"来源：{(item.Driver.Source == "uploaded" ? "上传安装" : "随程序提供 / 已注册")}")
                .AppendLine($"状态：{item.Driver.Status} · 版本：{item.Driver.Version}")
                .AppendLine($"SHA-256：{item.Driver.Sha256}");
            if (item.Descriptor is { } descriptor)
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
        }
        var relatedIds = details.SelectMany(item => item.RelatedDriverIds).Distinct(StringComparer.OrdinalIgnoreCase);
        var devices = details.SelectMany(item => item.Devices).DistinctBy(device => device.Id).ToArray();
        text.AppendLine().AppendLine("同 DLL 受影响驱动：" + string.Join("、", relatedIds));
        text.AppendLine().AppendLine($"这些驱动的引用设备（{devices.Length}）：");
        foreach (var device in devices)
            text.AppendLine($"• {device.Name} · {(device.IsRunning ? "采集中" : "已停止")} · {device.Id}");
        if (devices.Length == 0) text.AppendLine("无设备引用。");
        text.AppendLine().AppendLine("内置与上传驱动均支持更新、删除。同 DLL 的驱动一并处理；更新需停止引用设备、保持完整驱动标识与程序集名称并提高版本；删除需解除所有设备引用。");
        return text.ToString();
    }
}
