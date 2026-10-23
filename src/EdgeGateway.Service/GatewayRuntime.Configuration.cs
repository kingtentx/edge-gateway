using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    /// <summary>
    /// Validates and durably saves one complete configuration before replacing affected
    /// sessions. Unchanged sessions and the shared history/MQTT workers remain alive.
    /// Cancellation applies until the save commits; reconciliation then runs to completion.
    /// A restarted session connects asynchronously and reports connection failures through
    /// its normal status, log and reconnect policy, without reverting the saved settings.
    /// </summary>
    public async Task<ConfigurationApplyResult> ApplyConfigurationAsync(GatewayConfiguration candidate,
        IConfigurationStore configurationStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(configurationStore);
        cancellationToken.ThrowIfCancellationRequested();
        // Capture the caller's draft before awaiting another lifecycle operation. Neither
        // subsequent caller edits nor the store receive our live configuration object.
        GatewayConfiguration next;
        try { next = Clone(candidate); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("配置无法序列化，尚未保存或应用。", exception); }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var errors = ConfigurationValidator.Validate(next, _registry);
            if (errors.Count > 0) throw new InvalidDataException("配置检查未通过：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
            if (_running && !ConfigurationValuesEqual(_configuration, next, nameof(GatewayConfiguration.Devices)))
                throw new InvalidOperationException("设备运行期间可以修改设备与点位；修改网关或平台全局设置，请先停止全部设备。");

            var previousDevices = _configuration.Devices.ToDictionary(device => device.Id);
            var nextDevices = next.Devices.ToDictionary(device => device.Id);
            var added = next.Devices.Where(device => !previousDevices.ContainsKey(device.Id)).Select(device => device.Id).ToArray();
            var updated = next.Devices.Where(device => previousDevices.TryGetValue(device.Id, out var previous)
                && !ConfigurationValuesEqual(previous, device)).Select(device => device.Id).ToArray();
            var removed = _configuration.Devices.Where(device => !nextDevices.ContainsKey(device.Id)).Select(device => device.Id).ToArray();
            var affected = updated.Concat(removed).ToHashSet();
            var sessions = _sessions.Values.Where(session => affected.Contains(session.Configuration.Id)).ToArray();
            var restarted = sessions.Where(session => nextDevices.TryGetValue(session.Configuration.Id, out var device) && device.Enabled)
                .Select(session => session.Configuration.Id).ToArray();
            var stopped = sessions.Select(session => session.Configuration.Id).Except(restarted).ToArray();

            // Hold the same lock used by start/stop across the atomic save. A failed or
            // cancelled write must not stop a driver, erase a sample, or publish a draft.
            await configurationStore.SaveAsync(Clone(next), cancellationToken).ConfigureAwait(false);

            // Persistence is now committed. Do not observe the caller's cancellation
            // again: the live model must finish catching up with the durable model.
            foreach (var session in sessions)
            {
                try { session.RequestStop(); }
                catch (Exception exception)
                {
                    // Cancellation callbacks can throw after setting the token. Still
                    // join the worker and its write gate before disposing/replacing it.
                    _log.Write(GatewayLogLevel.Warning, $"取消设备 [{session.Configuration.Name}] 的旧采集时发生错误：{exception.Message}");
                }
            }
            foreach (var session in sessions)
            {
                await FinishDeviceStopAsync(session).ConfigureAwait(false);
                _sessions.TryRemove(session.Configuration.Id, out _);
            }

            _configuration = next;
            foreach (var sample in _latest.Where(pair => affected.Contains(pair.Value.DeviceId)))
                _latest.TryRemove(sample.Key, out _);
            foreach (var tag in previousDevices.Values.Where(device => affected.Contains(device.Id)).SelectMany(device => device.Tags))
                _alarmStates.TryRemove(tag.Id, out _);
            foreach (var deviceId in removed) _statuses.TryRemove(deviceId, out _);
            foreach (var deviceId in added.Concat(updated))
            {
                var device = nextDevices[deviceId];
                foreach (var tag in device.Tags) _alarmStates.TryRemove(tag.Id, out _);
                _statuses[deviceId] = new(deviceId, device.Name, DeviceState.Stopped, null, 0, null);
            }

            // Even a sole device restart keeps the existing shared channel and MQTT
            // workers. Only deleting/disabling the last session shuts those down.
            foreach (var deviceId in restarted) StartDeviceCore(nextDevices[deviceId]);
            if (_runCancellation is not null && _sessions.IsEmpty) await StopSharedServicesAsync().ConfigureAwait(false);
            _log.Write(GatewayLogLevel.Information,
                $"配置已自动保存并应用：新增 {added.Length}，更新 {updated.Length}，删除 {removed.Length}；重启 {restarted.Length}，停止 {stopped.Length} 台设备。未变更设备继续采集。");
            return new(added, updated, removed, restarted, stopped);
        }
        finally { _lifecycle.Release(); }
    }

    private static bool ConfigurationValuesEqual<T>(T first, T second, string? ignoredRootProperty = null) =>
        JsonValuesEqual(JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(second), ignoredRootProperty);

    // Object members (including driver property dictionaries) have no semantic order.
    // Array order remains significant, including the configured sequence of point reads.
    private static bool JsonValuesEqual(JsonElement first, JsonElement second, string? ignoredProperty = null)
    {
        if (first.ValueKind != second.ValueKind) return false;
        switch (first.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = first.EnumerateObject().Where(property => property.Name != ignoredProperty).ToArray();
                return properties.Length == second.EnumerateObject().Count(property => property.Name != ignoredProperty)
                    && properties.All(property => second.TryGetProperty(property.Name, out var value) && JsonValuesEqual(property.Value, value));
            case JsonValueKind.Array:
                return first.GetArrayLength() == second.GetArrayLength()
                    && first.EnumerateArray().Zip(second.EnumerateArray()).All(pair => JsonValuesEqual(pair.First, pair.Second));
            case JsonValueKind.String: return first.GetString() == second.GetString();
            case JsonValueKind.Number: return first.GetRawText() == second.GetRawText();
            default: return true;
        }
    }
}
