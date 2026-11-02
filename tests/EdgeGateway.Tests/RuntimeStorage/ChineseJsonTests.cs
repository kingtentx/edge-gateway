using System.Text.Json;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed class ChineseJsonTests
{
    [Theory]
    [InlineData(MqttPlatform.Generic)]
    [InlineData(MqttPlatform.ThingsBoard)]
    [InlineData(MqttPlatform.ThingsCloud)]
    [InlineData(MqttPlatform.ThingsPanel)]
    [InlineData(MqttPlatform.IoTSharp)]
    public void TelemetryContainsReadableChineseNamesValuesAndErrors(MqttPlatform platform)
    {
        var configuration = new GatewayConfiguration { Mqtt = new() { Platform = platform } };
        var now = DateTimeOffset.UtcNow;
        TelemetrySample[] samples = [
            new(Guid.NewGuid(), "设备甲", Guid.NewGuid(), "压力状态", "DB1010.DBW60", "运行中", "运行中", SampleQuality.Good, now, ""),
            new(Guid.NewGuid(), "设备甲", Guid.NewGuid(), "采样故障", "1", null, null, SampleQuality.Uncertain, now, "", "设备采集已停止")];
        var message = Assert.Single(PlatformMqttAdapter.BuildTelemetry(configuration, samples, "message", now));
        var payload = message.Payload;
        Assert.Contains("设备甲", message.Topic + payload);
        foreach (var text in new[] { "压力状态", "运行中", "采样故障", "设备采集已停止" }) Assert.Contains(text, payload);
        Assert.DoesNotContain("\\u8BBE", payload, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(payload);
        if (platform == MqttPlatform.Generic)
        {
            var rows = document.RootElement.GetProperty("samples");
            Assert.Equal("压力状态", rows[0].GetProperty("tagName").GetString());
            Assert.Equal("Uncertain", rows[1].GetProperty("quality").GetString());
            Assert.Equal("设备采集已停止", rows[1].GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task ConfigurationExportKeepsChineseReadableAndSpecialCharactersRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-chinese-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new GatewayConfiguration { Name = "网关中文\"引号\\路径\n换行<标签>&", ReportMode = TelemetryReportMode.OnChange };
            var path = Path.Combine(directory, "config.json");
            var files = new JsonConfigurationFiles();
            await files.ExportAsync(path, configuration);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("网关中文", text);
            Assert.Contains("\"reportMode\": \"OnChange\"", text);
            Assert.Equal(configuration.Name, (await files.ReadAsync(path)).Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NestedJsonStringsKeepChineseAndRequiredEscaping()
    {
        const string message = "读取失败\"说明\\路径\n下一行";
        var inner = JsonSerializer.Serialize(new { Error = message }, GatewayJson.TextOptions);
        var outer = JsonSerializer.Serialize(new { Data = inner }, GatewayJson.TextOptions);
        Assert.Contains("读取失败", outer);
        using var envelope = JsonDocument.Parse(outer);
        using var content = JsonDocument.Parse(envelope.RootElement.GetProperty("Data").GetString()!);
        Assert.Equal(message, content.RootElement.GetProperty("Error").GetString());
    }
}
