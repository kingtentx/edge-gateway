using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using EdgeGateway.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace EdgeGateway.OpcUa;

/// <summary>A secure OPC UA TCP endpoint whose lifetime is independent of acquisition sessions.</summary>
public sealed partial class GatewayOpcUaServer : IAsyncDisposable
{
    public const string NamespaceUri = "urn:kingtentx:edge-gateway:opcua";
    public static string DeviceNodeIdentifier(Guid deviceId) => $"device/{deviceId:N}";
    public static string TagNodeIdentifier(Guid deviceId, Guid tagId) => $"device/{deviceId:N}/tag/{tagId:N}";
    private readonly GatewayRuntime _runtime;
    private readonly IGatewayLog _log;
    private readonly string _pkiDirectory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ITelemetryContext _telemetry = new UaTelemetry();
    private UaServer? _server;
    private ApplicationConfiguration? _applicationConfiguration;
    private CancellationTokenSource? _lifetime;
    private Task? _updates;
    private string? _appliedOptions;
    private bool _disposed;
    private OpcUaServerStatus _status;

    public GatewayOpcUaServer(GatewayRuntime runtime, IGatewayLog log, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        _runtime = runtime;
        _log = log;
        _pkiDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "opcua", "pki");
        _status = new() { CertificateDirectory = _pkiDirectory, NamespaceUri = NamespaceUri };
    }

    public OpcUaServerStatus GetStatus()
    {
        var current = _runtime.GetOpcUaConfigurationSnapshot();
        var server = Volatile.Read(ref _server);
        var status = Volatile.Read(ref _status);
        return status with
        {
            Enabled = current.Enabled, EndpointUrl = current.EndpointUrl, IsRunning = server is not null,
            NodeCount = server?.NodeCount ?? 0, SessionCount = server?.SessionCount ?? 0
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = _runtime.GetOpcUaConfigurationSnapshot();
            if (!options.Enabled) throw new InvalidOperationException("OPC UA Server 尚未启用，请先应用设置。");
            if (_server is not null) return;
            _appliedOptions = JsonSerializer.Serialize(options);
            await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // Remember the current settings: subsequent ordinary device edits cannot undo a manual stop.
            _appliedOptions = JsonSerializer.Serialize(_runtime.GetOpcUaConfigurationSnapshot());
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ReconfigureAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = _runtime.GetOpcUaConfigurationSnapshot();
            var fingerprint = JsonSerializer.Serialize(options);
            if (_appliedOptions == fingerprint) return;
            _appliedOptions = fingerprint;
            await StopCoreAsync().ConfigureAwait(false);
            if (options.Enabled) await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(OpcUaServerConfiguration options, CancellationToken cancellationToken)
    {
        UaServer? server = null;
        CancellationTokenSource? lifetime = null;
        try
        {
            var errors = OpcUaConfigurationValidator.Validate(options);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            if (options.EnableUserAuthentication && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable)))
                throw new InvalidOperationException($"OPC UA 密码环境变量 {options.PasswordEnvironmentVariable} 未设置或为空。");
            Directory.CreateDirectory(_pkiDirectory);
            var configuration = CreateApplicationConfiguration(options);
            await configuration.ValidateAsync(ApplicationType.Server, cancellationToken).ConfigureAwait(false);
            // No callback accepts an untrusted certificate. The SDK stores rejected certificates for explicit review.
            configuration.CertificateValidator.CertificateValidation += (_, args) =>
                _log.Write(GatewayLogLevel.Warning, $"OPC UA 拒绝客户端证书 [{args.Certificate?.Thumbprint}]：{args.Error.StatusCode}");
            var application = new ApplicationInstance(configuration, _telemetry)
            { ApplicationName = configuration.ApplicationName, ApplicationType = ApplicationType.Server };
            await EnsureServerCertificateAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (!await application.CheckApplicationInstanceCertificatesAsync(true, ct: cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("OPC UA Server 应用证书无效，未启动端点。");
            var certificate = await configuration.SecurityConfiguration.ApplicationCertificates[0]
                .FindAsync(true, configuration.ApplicationUri, _telemetry, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("未找到 OPC UA Server 应用证书。");
            if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("OPC UA Server 证书尚未生效或已过期，请明确重建服务端证书并重新分发公共证书。");
            lifetime = new CancellationTokenSource();
            server = new UaServer(_runtime, _log, options, lifetime.Token);
            await server.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _applicationConfiguration = configuration;
            _lifetime = lifetime;
            Volatile.Write(ref _server, server);
            Volatile.Write(ref _status, _status with
            {
                Enabled = true, IsRunning = true, EndpointUrl = options.EndpointUrl,
                CertificateThumbprint = certificate.Thumbprint, StartedAt = DateTimeOffset.UtcNow, LastError = null
            });
            var ownedServer = server;
            _updates = Task.Run(() => RefreshAddressSpaceAsync(ownedServer, options.UpdateIntervalMs, lifetime.Token));
            _log.Write(GatewayLogLevel.Information, $"OPC UA Server 已启动：{options.EndpointUrl}；Basic256Sha256 / SignAndEncrypt，证书 {certificate.Thumbprint}。");
        }
        catch (Exception exception)
        {
            lifetime?.Cancel();
            if (server is not null)
            {
                try { await server.StopAsync().ConfigureAwait(false); } catch { }
                server.Dispose();
            }
            lifetime?.Dispose();
            Volatile.Write(ref _server, null);
            Volatile.Write(ref _status, _status with { IsRunning = false, LastError = exception.Message, StartedAt = null });
            _log.Write(GatewayLogLevel.Error, $"OPC UA Server 启动失败：{exception.Message}");
            throw;
        }
    }

    private async Task StopCoreAsync()
    {
        var server = Interlocked.Exchange(ref _server, null);
        _lifetime?.Cancel();
        try
        {
            if (_updates is not null)
            {
                try { await _updates.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    // A worker fault racing with cancellation must not leave the TCP listener alive.
                    Volatile.Write(ref _status, _status with { LastError = exception.Message });
                    _log.Write(GatewayLogLevel.Error, $"OPC UA 地址空间任务结束异常，继续关闭端点：{exception.Message}");
                }
            }
            if (server is not null) await server.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status, _status with { LastError = exception.Message });
            _log.Write(GatewayLogLevel.Error, $"OPC UA Server 停止异常：{exception.Message}");
            throw;
        }
        finally
        {
            server?.Dispose();
            _lifetime?.Dispose();
            _lifetime = null;
            _updates = null;
            Volatile.Write(ref _status, _status with { IsRunning = false, StartedAt = null, SessionCount = 0, NodeCount = 0 });
        }
        if (server is not null) _log.Write(GatewayLogLevel.Information, "OPC UA Server 已停止；设备采集保持原状态。");
    }

    private async Task RefreshAddressSpaceAsync(UaServer server, int interval, CancellationToken cancellationToken)
    {
        long revision = -1;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        do
        {
            try
            {
                var current = _runtime.ConfigurationRevision;
                GatewayConfiguration? configuration = current == revision ? null : _runtime.GetConfigurationSnapshot();
                server.Refresh(configuration, _runtime.GetLatest());
                revision = current;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _status, _status with { LastError = exception.Message });
                _log.Write(GatewayLogLevel.Warning, $"OPC UA 地址空间更新失败：{exception.Message}");
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private ApplicationConfiguration CreateApplicationConfiguration(OpcUaServerConfiguration options)
    {
        var policies = new UserTokenPolicyCollection();
        if (options.AllowAnonymous) policies.Add(new UserTokenPolicy(UserTokenType.Anonymous) { PolicyId = "anonymous" });
        if (options.EnableUserAuthentication) policies.Add(new UserTokenPolicy(UserTokenType.UserName)
        { PolicyId = "username", SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
        var applicationUri = $"urn:{Dns.GetHostName()}:kingtentx:edge-gateway:opcua-server";
        return new ApplicationConfiguration(_telemetry)
        {
            ApplicationName = "EdgeGateway OPC UA Server", ApplicationUri = applicationUri,
            ProductUri = NamespaceUri, ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates = [new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(_pkiDirectory, "own"),
                    SubjectName = "CN=EdgeGateway OPC UA Server, O=EdgeGateway",
                    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
                }],
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = StoreDirectory(OpcUaCertificateStore.Trusted) },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(_pkiDirectory, "issuers") },
                RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = CertificateStoreType.Directory, StorePath = StoreDirectory(OpcUaCertificateStore.Rejected) },
                AutoAcceptUntrustedCertificates = false, AddAppCertToTrustedStore = false,
                RejectSHA1SignedCertificates = true, RejectUnknownRevocationStatus = true,
                MinimumCertificateKeySize = 2048, MaxRejectedCertificates = 100
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = options.OperationTimeoutMs, MaxStringLength = 1_048_576,
                MaxByteStringLength = 4_194_304, MaxArrayLength = 65_536, MaxMessageSize = 4_194_304,
                MaxBufferSize = 65_536, ChannelLifetime = 600_000, SecurityTokenLifetime = 3_600_000
            },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [options.EndpointUrl],
                SecurityPolicies = [new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }],
                UserTokenPolicies = policies, DiagnosticsEnabled = true, MaxSessionCount = options.MaxSessions,
                MaxChannelCount = options.MaxSessions * 2, MinSessionTimeout = 10_000, MaxSessionTimeout = 3_600_000,
                MinPublishingInterval = 50, MaxPublishingInterval = 60_000, PublishingResolution = 50,
                MaxSubscriptionCount = options.MaxSessions * 10, MaxMessageQueueSize = 100,
                MaxNotificationQueueSize = 1000, MaxNotificationsPerPublish = 1000, ShutdownDelay = 0,
                OperationLimits = new OperationLimits { MaxNodesPerRead = 2000, MaxNodesPerBrowse = 1000, MaxNodesPerWrite = 100, MaxMonitoredItemsPerCall = 2000 }
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class UaTelemetry() : TelemetryContextBase(NullLoggerFactory.Instance) { }

    private sealed class UaServer(GatewayRuntime runtime, IGatewayLog log, OpcUaServerConfiguration options,
        CancellationToken lifetime) : StandardServer
    {
        private GatewayNodeManager? _nodes;
        public int NodeCount => _nodes?.NodeCount ?? 0;
        public int SessionCount
        {
            get { try { return CurrentInstance.SessionManager.GetSessions().Count; } catch (ServiceResultException) { return 0; } }
        }
        public void Refresh(GatewayConfiguration? configuration, IReadOnlyList<TelemetrySample> samples) => _nodes?.Refresh(configuration, samples);
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            _nodes = new GatewayNodeManager(server, configuration, runtime, options, lifetime);
            return new MasterNodeManager(server, configuration, null, [_nodes]);
        }
        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "EdgeGateway", ProductName = "EdgeGateway OPC UA Server", ProductUri = NamespaceUri,
            SoftwareVersion = "1.0.0", BuildNumber = "1", BuildDate = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc)
        };
        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);
            server.SessionManager.ImpersonateUser += Authenticate;
        }
        private void Authenticate(ISession session, ImpersonateEventArgs args)
        {
            if (args.NewIdentity is AnonymousIdentityToken or null)
            {
                if (!options.AllowAnonymous) throw new ServiceResultException(StatusCodes.BadIdentityTokenRejected, "匿名会话未启用。");
                args.Identity = new RoleBasedIdentity(new UserIdentity(), [Role.Anonymous]);
                return;
            }
            if (args.NewIdentity is not UserNameIdentityToken token || !options.EnableUserAuthentication)
                throw new ServiceResultException(StatusCodes.BadIdentityTokenRejected, "此身份类型未启用。");
            var password = Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable);
            var supplied = token.DecryptedPassword;
            if (!string.Equals(token.UserName, options.Username, StringComparison.Ordinal) || string.IsNullOrEmpty(password)
                || supplied is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(supplied), SHA256.HashData(Encoding.UTF8.GetBytes(password))))
            {
                log.Write(GatewayLogLevel.Warning, "OPC UA 用户验证失败。");
                throw new ServiceResultException(StatusCodes.BadUserAccessDenied, "OPC UA 用户名或密码无效。");
            }
            args.Identity = new RoleBasedIdentity(new UserIdentity(token), [Role.AuthenticatedUser]);
        }
    }
}
