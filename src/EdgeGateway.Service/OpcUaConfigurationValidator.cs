using System.Text.RegularExpressions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public static class OpcUaConfigurationValidator
{
    public static IReadOnlyList<string> Validate(OpcUaServerConfiguration? configuration)
    {
        var errors = new List<string>();
        if (configuration is null) return ["OPC UA Server 配置缺失。"];
        if (!Uri.TryCreate(configuration.EndpointUrl, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            errors.Add("OPC UA 端点必须为含有效主机与端口的 opc.tcp 地址，不得包含凭据、查询或片段。");
        if (!configuration.AllowAnonymous && !configuration.EnableUserAuthentication)
            errors.Add("OPC UA 至少需要启用匿名只读或用户名验证一种身份方式。");
        if (configuration.EnableUserAuthentication && (string.IsNullOrWhiteSpace(configuration.Username) || configuration.Username.Length > 128
            || configuration.Username.Any(char.IsControl)))
            errors.Add("OPC UA 用户名须为 1–128 个非控制字符。");
        if (!Regex.IsMatch(configuration.PasswordEnvironmentVariable ?? "", "^[A-Za-z_][A-Za-z0-9_]*$"))
            errors.Add("OPC UA 密码只允许配置合法的环境变量名称。");
        if (configuration.AllowRemoteWrite && !configuration.EnableUserAuthentication)
            errors.Add("允许 OPC UA 远程写入前必须启用用户名验证；匿名会话永远只读。");
        if (configuration.UpdateIntervalMs is < 50 or > 60_000) errors.Add("OPC UA 更新周期必须为 50–60000 ms。");
        if (configuration.MaxSessions is < 1 or > 200) errors.Add("OPC UA 最大会话数必须为 1–200。");
        if (configuration.OperationTimeoutMs is < 100 or > 60_000) errors.Add("OPC UA 操作超时必须为 100–60000 ms。");
        return errors;
    }
}
