using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.Service;

public sealed partial class GatewayRuntime
{
    /// <summary>Read the requested point (or the first enabled point) once. A stopped target uses a temporary real connection.</summary>
    public Task<DeviceProbeResult> ProbeDeviceAsync(Guid deviceId, Guid? tagId = null,
        CancellationToken cancellationToken = default) => ProbeDeviceAsync(deviceId, tagId, new DeviceProbeOptions(), cancellationToken);

    public async Task<DeviceProbeResult> ProbeDeviceAsync(Guid deviceId, Guid? tagId,
        DeviceProbeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = Stopwatch.GetTimestamp();
        var result = new DeviceProbeResult
        {
            OperationId = Guid.NewGuid(), DeviceId = deviceId, TagId = tagId,
            Source = options.Source, StartedAt = DateTimeOffset.UtcNow, State = DeviceProbeState.Failed
        };
        DeviceSession? session = null;
        TagConfiguration? tag = null;
        var lifecycleHeld = false;
        var gateHeld = false;
        var temporary = false;
        var driverTouched = false;
        var discardDriver = false;
        CancellationTokenSource? linked = null;
        CancellationTokenSource? deadline = null;
        IAsyncDisposable? channelLease = null;
        try
        {
            ValidateOperationSource(options.Source);
            if (options.TimeoutMs is < 50 or > 60_000)
                throw new ArgumentOutOfRangeException(nameof(options), "诊断超时必须在 50–60000 ms 之间。");
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The running path never needs _lifecycle: Stop may hold it while awaiting an RPC worker.
            // Gate admission and the captured session token supply the necessary identity/lifetime check.
            if (!_sessions.TryGetValue(deviceId, out session) || !session.IsActive)
            {
                if (options.RequireRunning) throw new InvalidOperationException("设备未处于采集运行状态。");
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                lifecycleHeld = true;
                ObjectDisposedException.ThrowIf(_disposed, this);
                var device = FindDevice(deviceId);
                if (!_sessions.TryGetValue(deviceId, out session) || !session.IsActive)
                {
                    // A diagnostic may inspect a disabled device, but must not bypass enabled serial-port ownership.
                    var candidate = Clone(_configuration);
                    candidate.Devices.Single(d => d.Id == deviceId).Enabled = true;
                    var errors = ConfigurationValidator.Validate(candidate, _registry);
                    if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                    session = new DeviceSession(device, cancellationToken);
                    temporary = true;
                }
            }
            var configuration = session.Configuration;
            result = result with
            {
                DeviceName = configuration.Name, DriverId = configuration.DriverId, UsedRunningSession = !temporary
            };
            tag = tagId is Guid id
                ? configuration.Tags.FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException("点位不存在。", nameof(tagId))
                : configuration.Tags.FirstOrDefault(t => t.Enabled);
            if (tag is { Enabled: false }) throw new InvalidOperationException("点位未启用，不能执行读取诊断。");
            result = result with { TagId = tag?.Id, DataType = tag?.DataType };
            if (!temporary && lifecycleHeld) { _lifecycle.Release(); lifecycleHeld = false; }
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
            await session.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateHeld = true;
            if (!temporary) EnsureCurrentOperationSession(session);
            channelLease = await EnterChannelAsync(configuration, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(Math.Min(options.TimeoutMs ?? 60_000, CommunicationTimeout(configuration)));
            if (temporary)
            {
                // Keep the stable ID: legacy adapters retain their quarantine lease across temporary diagnostics.
                session.Driver = _registry.GetFactory(configuration.DriverId).Create(configuration);
                driverTouched = true;
                var connectStarted = Stopwatch.GetTimestamp();
                try
                {
                    await session.Driver.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (tag is null) RecordDeviceOperation(deviceId, false, true, Stopwatch.GetElapsedTime(connectStarted));
                }
                catch (Exception exception)
                {
                    RecordDeviceOperation(deviceId, false, false, Stopwatch.GetElapsedTime(connectStarted), exception.Message);
                    throw;
                }
            }
            if (session.Driver is null) throw new InvalidOperationException("设备尚未连接，请等待采集恢复后再诊断。");
            result = result with { Connected = true };
            if (tag is null)
            {
                // The explicit state and Uncertain quality distinguish connection-only evidence from a fresh point read.
                result = result with { State = DeviceProbeState.ConnectedOnly };
            }
            else
            {
                driverTouched = true;
                var sample = await ReadOperationSampleAsync(configuration, tag, session.Driver, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                result = result with
                {
                    Sample = sample,
                    State = sample.Quality == SampleQuality.Good ? DeviceProbeState.Succeeded : DeviceProbeState.Failed,
                    Error = sample.Quality == SampleQuality.Good ? null : sample.Error ?? "本次读取未返回 Good 质量。"
                };
            }
        }
        catch (Exception exception)
        {
            var cancelled = cancellationToken.IsCancellationRequested || session?.Token.IsCancellationRequested == true;
            var state = cancelled ? DeviceProbeState.Cancelled
                : exception is OperationCanceledException or TimeoutException ? DeviceProbeState.TimedOut : DeviceProbeState.Failed;
            var error = state == DeviceProbeState.Cancelled ? "诊断已取消，未取得有效的本次确认。"
                : state == DeviceProbeState.TimedOut ? $"诊断通信超时：{exception.Message}" : exception.Message;
            result = result with { State = state, Error = error };
            if (session is not null && tag is not null)
                result = result with { Sample = BuildOperationFailureSample(session.Configuration, tag, error, cancelled) };
            discardDriver = driverTouched;
        }
        finally
        {
            if (session is not null && (temporary || discardDriver) && gateHeld)
                await DisposeDriverAsync(session).ConfigureAwait(false);
            if (channelLease is not null) await channelLease.DisposeAsync().ConfigureAwait(false);
            deadline?.Dispose();
            linked?.Dispose();
            if (gateHeld) session!.Gate.Release();
            if (temporary) { session!.DisposeCancellation(); session.Gate.Dispose(); }
            if (lifecycleHeld) _lifecycle.Release();
            result = result with { CompletedAt = DateTimeOffset.UtcNow, Elapsed = Stopwatch.GetElapsedTime(started) };
            _log.Write(result.Success ? GatewayLogLevel.Information : GatewayLogLevel.Warning,
                $"设备诊断 [{result.OperationId}] 来源 {AuditText(result.Source)}，设备 [{deviceId}] 点位 [{result.TagId}]：{result.State}；{AuditText(result.Error)}");
        }
        return result;
    }

    /// <summary>Submit exactly one write, then explicitly read back under the same device/channel gates when requested.</summary>
    public Task<WriteCommandResult> WriteVerifiedAsync(Guid deviceId, Guid tagId, string engineeringValue,
        WriteVerificationOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteOperationAsync(deviceId, tagId, engineeringValue, options ?? new WriteVerificationOptions(), false, cancellationToken);

    private async Task SubmitWriteCompatibilityAsync(Guid deviceId, Guid tagId, string engineeringValue, CancellationToken cancellationToken)
    {
        // Existing callers require submission-only semantics and the original validation/transport exception types.
        await WriteOperationAsync(deviceId, tagId, engineeringValue,
            new WriteVerificationOptions { VerifyReadBack = false }, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WriteCommandResult> WriteOperationAsync(Guid deviceId, Guid tagId, string engineeringValue,
        WriteVerificationOptions options, bool propagateExceptions, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = new WriteCommandResult
        {
            CommandId = options.CommandId ?? Guid.NewGuid(), DeviceId = deviceId, TagId = tagId,
            RequestedEngineeringValue = engineeringValue, VerificationRequested = options.VerifyReadBack,
            Source = options.Source, StartedAt = DateTimeOffset.UtcNow, State = WriteCommandState.Failed
        };
        DeviceSession? session = null;
        var gateHeld = false;
        var discardDriver = false;
        CancellationTokenSource? linked = null;
        CancellationTokenSource? deadline = null;
        IAsyncDisposable? channelLease = null;
        try
        {
            ValidateWriteOptions(options);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(deviceId, out session)) throw new InvalidOperationException("设备未处于采集运行状态。");
            EnsureCurrentOperationSession(session);
            var device = session.Configuration;
            var tag = device.Tags.FirstOrDefault(t => t.Id == tagId) ?? throw new ArgumentException("点位不存在。", nameof(tagId));
            ValidateWritablePoint(device, tag, options);
            var rawValue = ConvertWriteValue(tag, engineeringValue);
            result = result with
            {
                WrittenRawValue = rawValue,
                ExpectedEngineeringValue = SampleValueConversion.ConvertReadValue(tag, rawValue)
            };
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
            await session.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateHeld = true;
            EnsureCurrentOperationSession(session);
            ValidateWritablePoint(device, tag, options);
            channelLease = await EnterChannelAsync(device, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (session.Driver is null) throw new InvalidOperationException("设备尚未连接，请等待采集恢复后再写入。");
            deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(propagateExceptions ? CommunicationTimeout(device) : options.TimeoutMs);
            result = result with { WriteAttempted = true };
            await WriteOperationValueAsync(device, tag, session.Driver, rawValue, deadline.Token).ConfigureAwait(false);
            result = result with { Submitted = true, State = WriteCommandState.Submitted };
            if (_latest.TryGetValue(tagId, out var previous))
                _latest[tagId] = previous with { Quality = SampleQuality.Uncertain, Error = $"已写入，待读回确认；命令 {result.CommandId}" };
            if (options.VerifyReadBack)
            {
                for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    result = result with { ReadBackAttempts = attempt };
                    var sample = await ReadOperationSampleAsync(device, tag, session.Driver, deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    EnsureCurrentOperationSession(session);
                    // This is an actual fresh read under the device gate, never the requested-value echo.
                    // Sharing it makes every consumer observe later writes, faults and session restarts consistently.
                    _latest[tagId] = sample;
                    var matched = sample.Quality == SampleQuality.Good
                        && OperationValuesMatch(tag, result.ExpectedEngineeringValue, sample.Value, options);
                    result = result with
                    {
                        ReadBack = sample,
                        State = matched ? WriteCommandState.Matched
                            : sample.Quality == SampleQuality.Good ? WriteCommandState.Mismatched : WriteCommandState.Failed,
                        Error = matched ? null : sample.Quality == SampleQuality.Good
                            ? "本次主动读回的工程值与已提交值不一致。" : sample.Error ?? "本次主动读回不是 Good 质量。"
                    };
                    if (matched || attempt == options.MaxAttempts) break;
                    await Task.Delay(options.RetryIntervalMs, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            var cancelled = cancellationToken.IsCancellationRequested || session?.Token.IsCancellationRequested == true;
            var state = cancelled ? WriteCommandState.Cancelled
                : exception is OperationCanceledException or TimeoutException ? WriteCommandState.TimedOut : WriteCommandState.Failed;
            result = result with
            {
                State = state,
                Error = state == WriteCommandState.Cancelled ? "命令已取消；已尝试的写入可能仍已生效，未确认读回一致。"
                    : state == WriteCommandState.TimedOut ? $"写入或读回核对超时；不得视为执行未发生：{exception.Message}" : exception.Message
            };
            discardDriver = result.WriteAttempted;
            if (propagateExceptions) throw;
        }
        finally
        {
            if (discardDriver && gateHeld) await DisposeDriverAsync(session!).ConfigureAwait(false);
            if (channelLease is not null) await channelLease.DisposeAsync().ConfigureAwait(false);
            deadline?.Dispose();
            linked?.Dispose();
            if (gateHeld) session!.Gate.Release();
            result = result with { CompletedAt = DateTimeOffset.UtcNow, Elapsed = Stopwatch.GetElapsedTime(started) };
            _log.Write(result.State is WriteCommandState.Submitted or WriteCommandState.Matched ? GatewayLogLevel.Information : GatewayLogLevel.Warning,
                $"写入命令 [{result.CommandId}] 来源 {AuditText(result.Source)}，设备 [{deviceId}] 点位 [{tagId}]，工程值 {AuditText(engineeringValue)}：{result.State}；已尝试={result.WriteAttempted}，已提交={result.Submitted}，主动读回={result.ReadBackAttempts}；{AuditText(result.Error)}");
        }
        return result;
    }

    private void EnsureCurrentOperationSession(DeviceSession session)
    {
        session.Token.ThrowIfCancellationRequested();
        if (!_running || !session.IsActive || !session.Configuration.Enabled
            || !_sessions.TryGetValue(session.Configuration.Id, out var current) || !ReferenceEquals(current, session))
            throw new InvalidOperationException("设备未处于采集运行状态。");
    }

    private void ValidateWritablePoint(DeviceConfiguration device, TagConfiguration tag, WriteVerificationOptions options)
    {
        if (!tag.Enabled || !tag.Writable || !_registry.GetFactory(device.DriverId).Descriptor.SupportsWrite)
            throw new InvalidOperationException("点位未启用或不允许写入。");
        if (!string.IsNullOrWhiteSpace(tag.Expression)) throw new InvalidOperationException("计算表达式点位不支持写入，不能自动反算表达式。");
        var mqtt = _configuration.Mqtt;
        if (options.RequireRemoteWritePermission && (!mqtt.Enabled || !mqtt.RpcEnabled || !mqtt.AllowRemoteWrite))
            throw new InvalidOperationException("当前已应用配置不允许远程写入。");
    }

    private static void ValidateWriteOptions(WriteVerificationOptions options)
    {
        ValidateOperationSource(options.Source);
        if (options.CommandId == Guid.Empty) throw new ArgumentException("命令 ID 不能为空。", nameof(options));
        if (options.TimeoutMs is < 50 or > 60_000 || options.MaxAttempts is < 1 or > 20 || options.RetryIntervalMs is < 0 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(options), "核对超时为 50–60000 ms，读取次数为 1–20，重试间隔为 0–5000 ms。");
        if (!double.IsFinite(options.AbsoluteTolerance) || options.AbsoluteTolerance < 0
            || !double.IsFinite(options.RelativeTolerance) || options.RelativeTolerance is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "绝对容差须为非负有限数，相对容差须在 0–1 之间。");
    }

    private static void ValidateOperationSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256 || source.Any(char.IsControl))
            throw new ArgumentException("操作来源须为 1–256 个非控制字符。", nameof(source));
    }

    private int CommunicationTimeout(DeviceConfiguration device) => checked(device.TimeoutMs
        + (_registry.GetFactory(device.DriverId).Descriptor.IsLegacy ? 1500 : 0));

    private async Task WriteOperationValueAsync(DeviceConfiguration device, TagConfiguration tag, IDeviceDriver driver,
        object rawValue, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommunicationTimeout(device));
        try
        {
            await driver.WriteAsync(tag, rawValue, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            RecordDeviceOperation(device.Id, true, true, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception exception)
        {
            RecordDeviceOperation(device.Id, true, false, Stopwatch.GetElapsedTime(started), exception.Message);
            throw;
        }
    }

    private async Task<TelemetrySample> ReadOperationSampleAsync(DeviceConfiguration device, TagConfiguration tag,
        IDeviceDriver driver, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommunicationTimeout(device));
        try
        {
            var responses = await driver.ReadAsync([tag], timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var matches = responses?.Where(item => item is not null && item.TagId == tag.Id).Take(2).ToArray();
            var response = matches is { Length: 1 } ? matches[0]
                : new TagReadResult(tag.Id, null, SampleQuality.Bad, "驱动未返回请求点位的唯一读取结果。");
            var sample = BuildSample(device, tag, response, DateTimeOffset.UtcNow);
            RecordDeviceOperation(device.Id, false, sample.Quality == SampleQuality.Good, Stopwatch.GetElapsedTime(started), sample.Error);
            return sample;
        }
        catch (Exception exception)
        {
            RecordDeviceOperation(device.Id, false, false, Stopwatch.GetElapsedTime(started), exception.Message);
            throw;
        }
    }

    private static TelemetrySample BuildOperationFailureSample(DeviceConfiguration device, TagConfiguration tag, string error, bool cancelled) =>
        new(device.Id, device.Name, tag.Id, tag.Name, tag.Address, null, null,
            cancelled ? SampleQuality.Uncertain : SampleQuality.Bad, DateTimeOffset.UtcNow, tag.Unit, error);

    private static string AuditText(string? value) => JsonSerializer.Serialize(value is { Length: > 512 } ? value[..512] + "…" : value);

    private static bool OperationValuesMatch(TagConfiguration tag, object? expected, object? actual, WriteVerificationOptions options)
    {
        if (expected is null || actual is null) return false;
        if (TagDataTypes.IsString(tag.DataType)) return expected is string text && actual is string read && string.Equals(text, read, StringComparison.Ordinal);
        if (tag.DataType == TagDataType.Boolean) return expected is bool boolean && actual is bool readBoolean && boolean == readBoolean;
        if (!ExactNumericValue.TryParse(expected, out var left) || !ExactNumericValue.TryParse(actual, out var right)) return false;
        var difference = ExactNumericValue.Distance(left, right);
        ExactNumericValue.TryParse(options.AbsoluteTolerance, out var absolute);
        ExactNumericValue.TryParse(options.RelativeTolerance, out var relative);
        var magnitude = ExactNumericValue.Max(left.Abs(), right.Abs());
        var tolerance = ExactNumericValue.Max(absolute, new(relative.Coefficient * magnitude.Coefficient, relative.Exponent + magnitude.Exponent));
        return ExactNumericValue.Compare(difference, tolerance) <= 0;
    }

}
