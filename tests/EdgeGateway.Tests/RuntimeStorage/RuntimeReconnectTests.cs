using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.Model;
using EdgeGateway.Service;

namespace EdgeGateway.Tests.RuntimeStorage;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task RealTcpDisconnectClearsStaleValuesRecoversAndDoesNotStopAnotherDevice()
    {
        // Real loopback TCP + production Modbus driver, not a physical PLC acceptance.
        await using var faulted = new ReconnectServer();
        await using var healthy = new ReconnectServer();
        var factory = new ModbusTcpDriverFactory();
        DeviceConfiguration Device(string name, int port) => new()
        {
            Name = name, DriverId = factory.Descriptor.Id, Host = "127.0.0.1", Port = port,
            PollIntervalMs = 100, TimeoutMs = 500, Tags = [new() { Name = "register", Address = "0", DataType = TagDataType.UInt16 }]
        };
        var a = Device("disconnect target", faulted.Port); var b = Device("healthy peer", healthy.Port);
        var config = new GatewayConfiguration { Devices = [a, b], Logging = new() { LogAcquisitionSamples = false } };
        var store = new FakeStore();
        await using var log = new GatewayLog();
        await using var runtime = new GatewayRuntime(config, new DriverRegistry([factory], log), store, new FakePublisher(), log, new RecordingDataSink(store.Samples));
        await runtime.StartAsync();
        await Until(() => runtime.GetLatest().Count == 2 && runtime.GetLatest().All(sample => sample.Quality == SampleQuality.Good));
        var healthyBefore = runtime.GetCommunicationStatistics().Single(item => item.DeviceId == b.Id).ReadRequests;
        faulted.Disconnect();
        await Until(() => runtime.GetLatest().Any(sample => sample.DeviceId == a.Id && sample.Quality == SampleQuality.Bad));
        var bad = runtime.GetLatest().Single(sample => sample.DeviceId == a.Id);
        Assert.Null(bad.Value); Assert.Null(bad.RawValue);
        Assert.Equal(DeviceState.Reconnecting, runtime.GetDeviceStatuses().Single(item => item.DeviceId == a.Id).State);
        await Until(() => runtime.GetCommunicationStatistics().Single(item => item.DeviceId == b.Id).ReadRequests >= healthyBefore + 3);
        Assert.True(runtime.IsDeviceRunning(b.Id));
        faulted.Restore(99);
        await Until(() => runtime.GetLatest().Any(sample => sample.DeviceId == a.Id && sample.Quality == SampleQuality.Good && Convert.ToInt32(sample.Value) == 99));
        var recovered = runtime.GetLatest().Single(sample => sample.DeviceId == a.Id);
        Assert.True(recovered.Timestamp > bad.Timestamp);
        Assert.True(faulted.AcceptedConnections >= 2);
        var errors = runtime.GetCommunicationStatistics().Single(item => item.DeviceId == a.Id);
        Assert.True(errors.FailedRequests > 0); Assert.Null(errors.LastError);
        Assert.Equal(0, runtime.GetCommunicationStatistics().Single(item => item.DeviceId == b.Id).FailedRequests);
        await runtime.StopDeviceAsync(a.Id);
        var before = runtime.GetLatest().Single(sample => sample.DeviceId == b.Id).Timestamp;
        await Until(() => runtime.GetLatest().Any(sample => sample.DeviceId == b.Id && sample.Timestamp > before));
        Assert.True(runtime.IsDeviceRunning(b.Id));
    }

    private sealed class ReconnectServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private readonly ConcurrentBag<Task> _sessions = [];
        private readonly Task _accept;
        private int _down, _value = 42, _accepted;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int AcceptedConnections => Volatile.Read(ref _accepted);
        public ReconnectServer() { _listener.Start(); _accept = AcceptAsync(); }
        public void Disconnect()
        {
            Volatile.Write(ref _down, 1);
            foreach (var client in _clients.Keys) client.Dispose();
        }
        public void Restore(int value) { Volatile.Write(ref _value, value); Volatile.Write(ref _down, 0); }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted); _clients[client] = 0;
                    _sessions.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    if (Volatile.Read(ref _down) != 0) return;
                    var stream = client.GetStream(); var request = new byte[12];
                    while (!_stop.IsCancellationRequested && Volatile.Read(ref _down) == 0)
                    {
                        await stream.ReadExactlyAsync(request, _stop.Token);
                        if (request[7] != 3 || BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)) != 1)
                            throw new InvalidOperationException("Unexpected production-driver request in reconnect test.");
                        var response = new byte[11]; request.AsSpan(0, 7).CopyTo(response);
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), 5);
                        response[7] = 3; response[8] = 2;
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), (ushort)Volatile.Read(ref _value));
                        await stream.WriteAsync(response, _stop.Token);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { _clients.TryRemove(client, out _); client.Dispose(); }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            foreach (var client in _clients.Keys) client.Dispose();
            await _accept; await Task.WhenAll(_sessions); _stop.Dispose();
        }
    }
}
