using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Model;
using EdgeGateway.OpcUa;
using EdgeGateway.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace EdgeGateway.Tests.Integration;

/// <summary>Real encrypted OPC UA TCP sessions. Only the southbound device is a deterministic test fixture.</summary>
public sealed class OpcUaServerTests
{
    [Fact]
    public async Task SecureClientBrowsesReadsExactTypesAndReceivesRealSubscriptions()
    {
        await using var h = new UaHarness();
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var endpoints = await h.DiscoverAsync(client.Configuration);
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            Assert.Equal(SecurityPolicies.Basic256Sha256, endpoint.SecurityPolicyUri);
            Assert.Equal(MessageSecurityMode.SignAndEncrypt, endpoint.SecurityMode);
        });
        var session = await h.ConnectAsync(client);
        var references = await Browse(session, new NodeId("gateway", h.Namespace(session)));
        Assert.Contains(references, item => item.NodeId.Identifier.Equals(GatewayOpcUaServer.DeviceNodeIdentifier(h.Device.Id)));
        var tagReferences = await Browse(session, new NodeId(GatewayOpcUaServer.DeviceNodeIdentifier(h.Device.Id), h.Namespace(session)));
        Assert.Contains(tagReferences, item => item.NodeId.Identifier.Equals(GatewayOpcUaServer.TagNodeIdentifier(h.Device.Id, h.Tag.Id)));
        var unsigned = await Read(session, h.Node(session, h.Device.Tags[1]));
        var signed = await Read(session, h.Node(session, h.Device.Tags[2]));
        Assert.Equal(StatusCodes.Good, unsigned.StatusCode);
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(unsigned.Value));
        Assert.Equal(long.MinValue, Assert.IsType<long>(signed.Value));
        Assert.Equal(h.Runtime.GetLatestSample(h.Device.Tags[1].Id)!.Timestamp.UtcDateTime, unsigned.SourceTimestamp);
        Assert.Equal(true, (await Read(session, h.Node(session, h.Device.Tags[3]))).Value);
        Assert.Equal("工件 A", (await Read(session, h.Node(session, h.Device.Tags[4]))).Value);

        var notifications = new ConcurrentQueue<DataValue>();
        using var subscription = new Subscription(session.DefaultSubscription)
        { PublishingInterval = 50, KeepAliveCount = 10, LifetimeCount = 100, PublishingEnabled = true };
        var item = new MonitoredItem(subscription.DefaultItem)
        { StartNodeId = h.Node(session, h.Tag), AttributeId = Attributes.Value, SamplingInterval = 50, QueueSize = 10, DiscardOldest = true };
        item.Notification += (monitored, _) => { foreach (var value in monitored.DequeueValues()) notifications.Enqueue(value); };
        subscription.AddItem(item);
        Assert.True(session.AddSubscription(subscription));
        await subscription.CreateAsync();
        await Until(() => Task.FromResult(notifications.Any(value => StatusCode.IsGood(value.StatusCode) && Equals(value.Value, 2d))));
        var written = await h.Runtime.WriteVerifiedAsync(h.Device.Id, h.Tag.Id, "42.5");
        Assert.Equal(WriteCommandState.Matched, written.State);
        await Until(() => Task.FromResult(notifications.Any(value => StatusCode.IsGood(value.StatusCode) && Equals(value.Value, 42.5d))));
        Assert.Contains(notifications, value => value.SourceTimestamp == written.ReadBack!.Timestamp.UtcDateTime && Equals(value.Value, 42.5d));
        await subscription.DeleteAsync(true);
        Assert.True(h.Server.GetStatus().SessionCount >= 1);
    }

    [Fact]
    public async Task AuthenticatedWriteIsExplicitlyDisabledByDefaultAndAnonymousNeverWrites()
    {
        await using var h = new UaHarness(authentication: true, allowWrite: false);
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var user = await h.ConnectAsync(client, h.User());
        Assert.Equal(StatusCodes.BadUserAccessDenied, await Write(user, h.Node(user, h.Tag), 42d));
        Assert.Equal(0, h.State.Writes);
        await h.Runtime.StopAsync();
        var enabled = h.Runtime.GetConfigurationSnapshot();
        enabled.OpcUa.AllowRemoteWrite = true;
        await h.Runtime.ApplyConfigurationAsync(enabled, h.ConfigurationStore);
        await h.Server.ReconfigureAsync();
        await h.StartDeviceAsync();
        user = await h.ConnectAsync(client, h.User());
        var anonymous = await h.ConnectAsync(client);
        Assert.Equal(StatusCodes.BadUserAccessDenied, await Write(anonymous, h.Node(anonymous, h.Tag), 42d));
        Assert.Equal(0, h.State.Writes);
        Assert.Equal(StatusCodes.Good, await Write(user, h.Node(user, h.Tag), 42d));
        Assert.Equal(1, h.State.Writes);
        var actual = await Read(user, h.Node(user, h.Tag));
        Assert.Equal(StatusCodes.Good, actual.StatusCode);
        Assert.Equal(42d, actual.Value);
        Assert.Equal(h.Runtime.GetLatestSample(h.Tag.Id)!.Timestamp.UtcDateTime, actual.SourceTimestamp);
        Assert.Contains(h.Log.GetEntries(), entry => entry.Message.Contains("opcua:operator") && entry.Message.Contains("Matched"));
        Assert.Equal(StatusCodes.BadTypeMismatch, await Write(user, h.Node(user, h.Tag), "wrong-type"));
        Assert.Equal(1, h.State.Writes);
    }

    [Fact]
    public async Task BadPasswordAndUntrustedApplicationCertificatesCannotOpenSessions()
    {
        await using var h = new UaHarness(authentication: true, allowWrite: true);
        await h.StartAsync();
        var trusted = await h.CreateClientAsync();
        var badPassword = await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(trusted, new UserIdentity("operator", Encoding.UTF8.GetBytes("incorrect"))));
        Assert.Contains("BadUserAccessDenied", badPassword.ToString());
        var untrustedClient = await h.CreateClientAsync(trustClient: false);
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(untrustedClient));
        await Until(async () => (await h.Server.GetCertificatesAsync()).Any(item => item.Store == OpcUaCertificateStore.Rejected && item.Thumbprint == untrustedClient.Thumbprint));
        var untrustingClient = await h.CreateClientAsync(trustServer: false);
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(untrustingClient));
        Assert.Equal(0, h.State.Writes);
        Assert.DoesNotContain(h.Log.GetEntries(), entry => entry.Message.Contains(h.Password, StringComparison.Ordinal));
        Assert.False(trusted.Configuration.SecurityConfiguration.AutoAcceptUntrustedCertificates);
    }

    [Fact]
    public async Task ReadbackMismatchCannotReturnGoodOrInventTheRequestedValue()
    {
        await using var h = new UaHarness(authentication: true, allowWrite: true);
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var session = await h.ConnectAsync(client, h.User());
        h.State.IgnoreWrites = true;
        var status = await Write(session, h.Node(session, h.Tag), 99d);
        Assert.True(StatusCode.IsBad(status));
        Assert.Equal(1, h.State.Writes);
        var value = await Read(session, h.Node(session, h.Tag));
        Assert.Equal(2d, value.Value);
        Assert.Equal(StatusCodes.Good, value.StatusCode); // a real good read of 2, while the command was not matched
        Assert.Contains(h.Log.GetEntries(), entry => entry.Message.Contains("Mismatched"));
    }

    [Fact]
    public async Task LaterLocalSubmissionAndDeviceRestartCannotExposeAnOldVerifiedGoodValue()
    {
        await using var h = new UaHarness(authentication: true, allowWrite: true);
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var session = await h.ConnectAsync(client, h.User());
        var id = h.Node(session, h.Tag);
        Assert.Equal(StatusCodes.Good, await Write(session, id, 42d));
        await h.Runtime.WriteAsync(h.Device.Id, h.Tag.Id, "43");
        var submitted = await Read(session, id);
        Assert.True(StatusCode.IsUncertain(submitted.StatusCode));
        Assert.Equal(42d, submitted.Value);
        await h.Runtime.StopDeviceAsync(h.Device.Id);
        Assert.Equal(StatusCodes.BadOutOfService, (await Read(session, id)).StatusCode);
        Assert.True(h.Server.GetStatus().IsRunning);
        h.State.ReadBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Runtime.StartDeviceAsync(h.Device.Id);
        await Until(() => Task.FromResult(h.State.ActiveReads > 0));
        Assert.Equal(StatusCodes.BadWaitingForInitialData, (await Read(session, id)).StatusCode);
        h.State.ReadBlock.TrySetResult();
        await Until(async () => (await Read(session, id)) is { Value: double value, StatusCode: var quality } && value == 43 && StatusCode.IsGood(quality));
    }

    [Fact]
    public async Task FaultAndStoppedValuesAreBadAndEndpointRemainsAvailable()
    {
        await using var h = new UaHarness(pollInterval: 100);
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var session = await h.ConnectAsync(client);
        h.State.FailReads = true;
        await Until(async () => StatusCode.IsBad((await Read(session, h.Node(session, h.Tag))).StatusCode));
        var failed = await Read(session, h.Node(session, h.Tag));
        Assert.Null(failed.Value);
        await h.Runtime.StopAsync();
        Assert.Equal(StatusCodes.BadOutOfService, (await Read(session, h.Node(session, h.Tag))).StatusCode);
        Assert.True(h.Server.GetStatus().IsRunning);
        Assert.NotEmpty(await Browse(session, new NodeId("gateway", h.Namespace(session))));
    }

    [Fact]
    public async Task UaCancelWhileWaitingForTheDeviceLockCancelsEveryPendingWriteWithoutSubmission()
    {
        await using var h = new UaHarness(authentication: true, allowWrite: true);
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var session = await h.ConnectAsync(client, h.User());
        h.State.ReadBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = h.Runtime.ProbeDeviceAsync(h.Device.Id, h.Tag.Id);
        await Until(() => Task.FromResult(h.State.ActiveReads > 0));
        const uint requestHandle = 87654321;
        var id = h.Node(session, h.Tag);
        var pending = session.WriteAsync(new RequestHeader { RequestHandle = requestHandle, TimeoutHint = 10_000 },
            [new WriteValue { NodeId = id, AttributeId = Attributes.Value, Value = new DataValue(new Variant(21d)) },
             new WriteValue { NodeId = id, AttributeId = Attributes.Value, Value = new DataValue(new Variant(22d)) }], CancellationToken.None);
        try
        {
            await Until(async () => (await session.CancelAsync(null, requestHandle, CancellationToken.None)).CancelCount > 0);
            WriteResponse? response = null;
            var failure = await Record.ExceptionAsync(async () => response = await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            if (failure is null) Assert.All(response!.Results, result => Assert.Equal(StatusCodes.BadRequestCancelledByRequest, result));
            else Assert.Contains("BadRequestCancelledByRequest", failure.ToString());
            Assert.Equal(0, h.State.Writes);
        }
        finally { h.State.ReadBlock.TrySetResult(); await probe; }
        Assert.Equal(0, h.State.Writes);
    }

    [Fact]
    public async Task HotAddRenameAndDeleteKeepTheSameUaSessionAndStableNodeIdentifiers()
    {
        await using var h = new UaHarness();
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var session = await h.ConnectAsync(client);
        var sessionId = session.SessionId;
        var originalNode = h.Node(session, h.Tag);
        var candidate = h.Runtime.GetConfigurationSnapshot();
        var added = new DeviceConfiguration { Name = "added", DriverId = "ua-test", PollIntervalMs = 1000,
            Tags = [new TagConfiguration { Name = "new", Address = "double", DataType = TagDataType.Float64 }] };
        candidate.Devices.Add(added);
        await h.Runtime.ApplyConfigurationAsync(candidate, h.ConfigurationStore);
        var addedId = new NodeId(GatewayOpcUaServer.TagNodeIdentifier(added.Id, added.Tags[0].Id), h.Namespace(session));
        await Until(async () => (await Read(session, addedId)).StatusCode == StatusCodes.BadOutOfService);
        Assert.False(h.Runtime.IsDeviceRunning(added.Id));
        Assert.Equal(1, h.State.Connections);
        var deletedNotifications = new ConcurrentQueue<DataValue>();
        using var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 50, PublishingEnabled = true };
        var monitored = new MonitoredItem(subscription.DefaultItem)
        { StartNodeId = addedId, AttributeId = Attributes.Value, SamplingInterval = 50, QueueSize = 10, DiscardOldest = true };
        monitored.Notification += (item, _) => { foreach (var value in item.DequeueValues()) deletedNotifications.Enqueue(value); };
        subscription.AddItem(monitored);
        session.AddSubscription(subscription);
        await subscription.CreateAsync();
        await Until(() => Task.FromResult(deletedNotifications.Any(value => value.StatusCode == StatusCodes.BadOutOfService)));
        candidate = h.Runtime.GetConfigurationSnapshot();
        candidate.Devices[0].Tags[0].Name = "renamed";
        await h.Runtime.ApplyConfigurationAsync(candidate, h.ConfigurationStore);
        await Until(async () => Equals((await Read(session, originalNode, Attributes.DisplayName)).Value?.ToString(), "renamed"));
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(originalNode.Identifier, GatewayOpcUaServer.TagNodeIdentifier(h.Device.Id, h.Tag.Id));
        candidate = h.Runtime.GetConfigurationSnapshot();
        candidate.Devices.RemoveAll(device => device.Id == added.Id);
        await h.Runtime.ApplyConfigurationAsync(candidate, h.ConfigurationStore);
        await Until(async () => (await Read(session, addedId)).StatusCode == StatusCodes.BadNodeIdUnknown);
        await Until(() => Task.FromResult(deletedNotifications.Any(value => value.StatusCode == StatusCodes.BadNodeIdUnknown)));
        candidate = h.Runtime.GetConfigurationSnapshot();
        candidate.Devices.Add(added); // restoring a saved configuration retains the same Guid
        await h.Runtime.ApplyConfigurationAsync(candidate, h.ConfigurationStore);
        await Until(async () => (await Read(session, addedId)).StatusCode == StatusCodes.BadOutOfService);
        var restoredNotifications = new ConcurrentQueue<DataValue>();
        var restored = new MonitoredItem(subscription.DefaultItem)
        { StartNodeId = addedId, AttributeId = Attributes.Value, SamplingInterval = 50, QueueSize = 10, DiscardOldest = true };
        restored.Notification += (item, _) => { foreach (var value in item.DequeueValues()) restoredNotifications.Enqueue(value); };
        subscription.AddItem(restored);
        await subscription.ApplyChangesAsync();
        await h.Runtime.StartDeviceAsync(added.Id);
        await Until(() => Task.FromResult(restoredNotifications.Any(value => StatusCode.IsGood(value.StatusCode) && Equals(value.Value, 2d))));
        Assert.Contains(deletedNotifications, value => StatusCode.IsGood(value.StatusCode) && Equals(value.Value, 2d));
        await subscription.DeleteAsync(true);
        Assert.True(session.Connected);
    }

    [Fact]
    public async Task ManualStopPersistsAcrossDeviceEditsAndReleasesTheListeningPort()
    {
        await using var h = new UaHarness();
        await h.StartAsync();
        await h.Server.StopAsync();
        Assert.True(h.Runtime.IsDeviceRunning(h.Device.Id));
        var changed = h.Runtime.GetConfigurationSnapshot();
        changed.Devices.Add(new DeviceConfiguration { Name = "stopped", DriverId = "ua-test" });
        await h.Runtime.ApplyConfigurationAsync(changed, h.ConfigurationStore);
        await h.Server.ReconfigureAsync();
        Assert.False(h.Server.GetStatus().IsRunning);
        using (var listener = new TcpListener(IPAddress.Loopback, h.Port))
        { listener.Server.ExclusiveAddressUse = true; listener.Start(); listener.Stop(); }
        await h.Server.StartAsync();
        Assert.True(h.Server.GetStatus().IsRunning);
        Assert.Equal(1, h.State.Connections);
    }

    [Fact]
    public async Task CertificateManagementUsesExactPublicTrustAndRegenerationPreservesAcquisition()
    {
        await using var h = new UaHarness(authentication: true);
        await h.StartAsync();
        var client = await h.CreateClientAsync();
        var publicBytes = await h.Server.ExportCertificateAsync(OpcUaCertificateStore.Trusted, client.Thumbprint);
        using var publicCertificate = new X509Certificate2(publicBytes);
        Assert.False(publicCertificate.HasPrivateKey);
        Assert.Equal(client.Thumbprint, publicCertificate.Thumbprint);
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Server.TrustCertificateAsync(publicBytes.Concat(publicBytes).ToArray()));
        var clientCertificate = await client.Configuration.SecurityConfiguration.ApplicationCertificates[0].FindAsync(true, client.Configuration.ApplicationUri, h.Telemetry);
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Server.TrustCertificateAsync(clientCertificate.Export(X509ContentType.Pfx)));
        var old = h.Server.GetStatus().CertificateThumbprint;
        Environment.SetEnvironmentVariable(h.EnvironmentName, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Server.RegenerateServerCertificateAsync());
        Assert.True(h.Server.GetStatus().IsRunning);
        Assert.Equal(old, h.Server.GetStatus().CertificateThumbprint);
        Environment.SetEnvironmentVariable(h.EnvironmentName, h.Password);
        await h.Server.RegenerateServerCertificateAsync();
        Assert.NotEqual(old, h.Server.GetStatus().CertificateThumbprint);
        Assert.True(h.Server.GetStatus().IsRunning);
        Assert.Contains(await h.Server.GetCertificatesAsync(), cert => cert.Store == OpcUaCertificateStore.Trusted && cert.Thumbprint == client.Thumbprint);
        Assert.Equal(1, h.State.Connections);
        Assert.True(h.Runtime.IsDeviceRunning(h.Device.Id));
        await h.TrustServerOnClientAsync(client.Configuration);
        var session = await h.ConnectAsync(client);
        Assert.Equal(StatusCodes.Good, (await Read(session, h.Node(session, h.Tag))).StatusCode);
        await h.Server.RemoveTrustedCertificateAsync(client.Thumbprint);
        Assert.True(h.Server.GetStatus().IsRunning);
        Assert.DoesNotContain(await h.Server.GetCertificatesAsync(), cert => cert.Store == OpcUaCertificateStore.Trusted && cert.Thumbprint == client.Thumbprint);
        await Assert.ThrowsAnyAsync<Exception>(() => Read(session, h.Node(session, h.Tag)));
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(client));
        Assert.Equal(1, h.State.Connections);
    }

    [Fact]
    public async Task EndpointHostChangeRequiresExplicitNewCertificateAndDisabledServerDoesNotStart()
    {
        await using var h = new UaHarness();
        await h.StartAsync();
        await h.Runtime.StopAsync();
        var old = h.Server.GetStatus().CertificateThumbprint;
        var candidate = h.Runtime.GetConfigurationSnapshot();
        candidate.OpcUa.EndpointUrl = $"opc.tcp://127.0.0.2:{h.Port}/EdgeGateway";
        await h.Runtime.ApplyConfigurationAsync(candidate, h.ConfigurationStore);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Server.ReconfigureAsync());
        Assert.False(h.Server.GetStatus().IsRunning);
        Assert.NotNull(h.Server.GetStatus().LastError);
        await h.Server.RegenerateServerCertificateAsync();
        Assert.False(h.Server.GetStatus().IsRunning); // it was stopped after the failed restart
        Assert.NotEqual(old, h.Server.GetStatus().CertificateThumbprint);
        await h.Server.StartAsync();
        var client = await h.CreateClientAsync();
        var session = await h.ConnectAsync(client); // checkDomain=true verifies the new IP SAN
        Assert.True(session.Connected);
        candidate = h.Runtime.GetConfigurationSnapshot();
        candidate.OpcUa.Enabled = false;
        await h.Runtime.ApplyConfigurationAsync(candidate, h.ConfigurationStore);
        await h.Server.ReconfigureAsync();
        Assert.False(h.Server.GetStatus().IsRunning);
        Assert.False(h.Server.GetStatus().Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Server.StartAsync());
    }

    private static async Task<DataValue> Read(ISession session, NodeId id, uint attribute = Attributes.Value)
    {
        var result = await session.ReadAsync(null, 0, TimestampsToReturn.Both,
            [new ReadValueId { NodeId = id, AttributeId = attribute }], CancellationToken.None);
        return Assert.Single(result.Results);
    }
    private static async Task<StatusCode> Write(ISession session, NodeId id, object value)
    {
        var result = await session.WriteAsync(null, [new WriteValue { NodeId = id, AttributeId = Attributes.Value, Value = new DataValue(new Variant(value)) }], CancellationToken.None);
        return Assert.Single(result.Results);
    }
    private static async Task<ReferenceDescriptionCollection> Browse(ISession session, NodeId id)
    {
        var result = await session.BrowseAsync(null, null, 0, [new BrowseDescription
        { NodeId = id, BrowseDirection = BrowseDirection.Forward, ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true, ResultMask = (uint)BrowseResultMask.All }], CancellationToken.None);
        var browsed = Assert.Single(result.Results);
        Assert.Equal(StatusCodes.Good, browsed.StatusCode);
        return browsed.References;
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("OPC UA integration state was not reached.");
            await Task.Delay(25);
        }
    }

    private sealed record UaClient(ApplicationConfiguration Configuration, string Thumbprint);
    private sealed class UaHarness : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "edge-ua-test-" + Guid.NewGuid().ToString("N"));
        public readonly string EnvironmentName = "EDGE_UA_TEST_" + Guid.NewGuid().ToString("N");
        public readonly string Password = Guid.NewGuid().ToString("N");
        public readonly ITelemetryContext Telemetry = new TestTelemetry();
        public readonly GatewayLog Log = new();
        public readonly UaState State = new();
        public readonly GatewayRuntime Runtime;
        public readonly GatewayOpcUaServer Server;
        public readonly JsonConfigurationStore ConfigurationStore;
        private readonly SqliteGatewayStore _store;
        private readonly UaPublisher _publisher = new();
        private readonly List<ISession> _sessions = [];
        public DeviceConfiguration Device { get; }
        public TagConfiguration Tag => Device.Tags[0];
        public int Port { get; }
        public UaHarness(bool authentication = false, bool allowWrite = false, int pollInterval = 60_000)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Device = new DeviceConfiguration
            {
                Name = "UA test", DriverId = "ua-test", PollIntervalMs = pollInterval, TimeoutMs = 5000,
                Tags = [Point("double", TagDataType.Float64), Point("uint64", TagDataType.UInt64), Point("int64", TagDataType.Int64),
                    Point("bool", TagDataType.Boolean), Point("text", TagDataType.Utf8String)]
            };
            var configuration = new GatewayConfiguration { Devices = [Device], OpcUa = new()
            { Enabled = true, EndpointUrl = $"opc.tcp://127.0.0.1:{Port}/EdgeGateway", UpdateIntervalMs = 50, OperationTimeoutMs = 3000,
                EnableUserAuthentication = authentication, AllowRemoteWrite = allowWrite, PasswordEnvironmentVariable = EnvironmentName } };
            Environment.SetEnvironmentVariable(EnvironmentName, Password);
            _store = new SqliteGatewayStore(Path.Combine(DirectoryPath, "data.db"));
            ConfigurationStore = new JsonConfigurationStore(Path.Combine(DirectoryPath, "gateway.json"));
            Runtime = new GatewayRuntime(configuration, new DriverRegistry([new UaFactory(State)], Log), _store, _publisher, Log);
            Server = new GatewayOpcUaServer(Runtime, Log, DirectoryPath);
        }
        private static TagConfiguration Point(string name, TagDataType type) => new()
        { Name = name, Address = name, DataType = type, Writable = true, Unit = "unit", Description = "test point" };
        public async Task StartAsync() { await StartDeviceAsync(); await Server.StartAsync(); }
        public async Task StartDeviceAsync()
        {
            await Runtime.StartDeviceAsync(Device.Id);
            await Until(() => Task.FromResult(Runtime.GetLatestSample(Tag.Id)?.Quality == SampleQuality.Good));
        }
        public IUserIdentity User() => new UserIdentity("operator", Encoding.UTF8.GetBytes(Password));
        public ushort Namespace(ISession session) => checked((ushort)session.NamespaceUris.GetIndex(GatewayOpcUaServer.NamespaceUri));
        public NodeId Node(ISession session, TagConfiguration tag) => new(GatewayOpcUaServer.TagNodeIdentifier(Device.Id, tag.Id), Namespace(session));
        public async Task<UaClient> CreateClientAsync(bool trustClient = true, bool trustServer = true)
        {
            var name = "client-" + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(DirectoryPath, name);
            var configuration = new ApplicationConfiguration(Telemetry)
            {
                ApplicationName = name, ApplicationUri = "urn:" + name, ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificates = [new CertificateIdentifier { StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(directory, "own"), SubjectName = "CN=" + name,
                        CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType }],
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "trusted") },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "issuers") },
                    RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "rejected") },
                    AutoAcceptUntrustedCertificates = false, AddAppCertToTrustedStore = false, RejectSHA1SignedCertificates = true,
                    MinimumCertificateKeySize = 2048, RejectUnknownRevocationStatus = true
                },
                TransportQuotas = new TransportQuotas { OperationTimeout = 4000, MaxMessageSize = 4_194_304, MaxByteStringLength = 4_194_304 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
            };
            await configuration.ValidateAsync(ApplicationType.Client);
            var application = new ApplicationInstance(configuration, Telemetry);
            Assert.True(await application.CheckApplicationInstanceCertificatesAsync(true));
            var certificate = await configuration.SecurityConfiguration.ApplicationCertificates[0].FindAsync(true, configuration.ApplicationUri, Telemetry);
            if (trustClient) await Server.TrustCertificateAsync(certificate.Export(X509ContentType.Cert));
            if (trustServer) await TrustServerOnClientAsync(configuration);
            return new(configuration, certificate.Thumbprint);
        }
        public async Task TrustServerOnClientAsync(ApplicationConfiguration configuration)
        {
            using var certificate = new X509Certificate2(await Server.ExportServerCertificateAsync());
            using var store = configuration.SecurityConfiguration.TrustedPeerCertificates.OpenStore(Telemetry);
            await store.AddAsync(certificate);
            await configuration.CertificateValidator.UpdateAsync(configuration);
        }
        public async Task<EndpointDescriptionCollection> DiscoverAsync(ApplicationConfiguration configuration)
        {
            using var discovery = await DiscoveryClient.CreateAsync(new Uri(Runtime.GetOpcUaConfigurationSnapshot().EndpointUrl), EndpointConfiguration.Create(configuration), Telemetry);
            return await discovery.GetEndpointsAsync(null);
        }
        public async Task<ISession> ConnectAsync(UaClient client, IUserIdentity? identity = null)
        {
            var description = (await DiscoverAsync(client.Configuration)).Single(endpoint => endpoint.SecurityMode == MessageSecurityMode.SignAndEncrypt && endpoint.SecurityPolicyUri == SecurityPolicies.Basic256Sha256);
            var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(client.Configuration));
            var session = await new DefaultSessionFactory(Telemetry).CreateAsync(client.Configuration, endpoint, false, true,
                "Integration test", 60_000, identity ?? new UserIdentity(), null);
            _sessions.Add(session);
            return session;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var session in _sessions)
            {
                try { await session.CloseAsync(1000, true); } catch { }
                session.Dispose();
            }
            State.ReadBlock?.TrySetResult();
            await Server.DisposeAsync();
            await Runtime.DisposeAsync();
            await _store.DisposeAsync();
            await _publisher.DisposeAsync();
            Environment.SetEnvironmentVariable(EnvironmentName, null);
            try { Directory.Delete(DirectoryPath, true); } catch (IOException) { }
        }
    }
    private sealed class TestTelemetry() : TelemetryContextBase(NullLoggerFactory.Instance) { }
    private sealed class UaState
    {
        public readonly ConcurrentDictionary<string, object> Values = new(new Dictionary<string, object>
        { ["double"] = 2d, ["uint64"] = ulong.MaxValue, ["int64"] = long.MinValue, ["bool"] = true, ["text"] = "工件 A" });
        public int Connections, Writes, ActiveReads;
        public bool IgnoreWrites, FailReads;
        public TaskCompletionSource? ReadBlock;
    }
    private sealed class UaFactory(UaState state) : IDriverFactory
    {
        public DriverDescriptor Descriptor { get; } = new("ua-test", "UA fixture", "Deterministic southbound driver", true);
        public IReadOnlyList<string> Validate(DeviceConfiguration configuration) => [];
        public IDeviceDriver Create(DeviceConfiguration configuration) => new UaDriver(state);
    }
    private sealed class UaDriver(UaState state) : IDeviceDriver
    {
        public Task ConnectAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref state.Connections); return Task.CompletedTask; }
        public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<TagConfiguration> tags, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.ActiveReads);
            try
            {
                if (state.ReadBlock is { } block) await block.Task.WaitAsync(cancellationToken);
                if (state.FailReads) throw new IOException("UA test device fault");
                return tags.Select(tag => new TagReadResult(tag.Id, state.Values[tag.Address])).ToArray();
            }
            finally { Interlocked.Decrement(ref state.ActiveReads); }
        }
        public Task WriteAsync(TagConfiguration tag, object value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref state.Writes);
            if (!state.IgnoreWrites) state.Values[tag.Address] = value;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class UaPublisher : ITelemetryPublisher
    {
        public bool IsConnected => false;
        public Task PublishAsync(MqttConfiguration settings, string topic, string payload, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
