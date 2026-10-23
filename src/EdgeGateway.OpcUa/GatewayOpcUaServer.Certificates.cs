using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EdgeGateway.Model;
using EdgeGateway.Service;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace EdgeGateway.OpcUa;

public sealed partial class GatewayOpcUaServer
{
    public async Task<IReadOnlyList<OpcUaCertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<OpcUaCertificateInfo>();
            foreach (var kind in Enum.GetValues<OpcUaCertificateStore>())
            {
                using var store = OpenCertificateStore(kind);
                var certificates = await store.EnumerateAsync(cancellationToken).ConfigureAwait(false);
                foreach (var certificate in certificates)
                {
                    using (certificate)
                    {
                        var directory = Path.Combine(StoreDirectory(kind), "certs");
                        var file = Directory.Exists(directory) ? Directory.EnumerateFiles(directory)
                            .FirstOrDefault(path => Path.GetFileName(path).Contains(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)) : null;
                        result.Add(new(kind, certificate.Thumbprint, certificate.Subject, certificate.Issuer,
                            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
                            file is null ? certificate.Thumbprint + ".der" : Path.GetFileName(file)));
                    }
                }
            }
            return result.OrderBy(item => item.Store).ThenBy(item => item.Subject, StringComparer.Ordinal).ToArray();
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Trust one explicit public end-entity certificate. Private keys, PKCS#12, bundles and CA certificates are refused.</summary>
    public async Task TrustCertificateAsync(byte[] publicCertificate, CancellationToken cancellationToken = default)
    {
        using var certificate = LoadPublicCertificate(publicCertificate);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var trusted = OpenCertificateStore(OpcUaCertificateStore.Trusted);
            var existing = await trusted.FindByThumbprintAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false);
            var present = existing.Count > 0;
            foreach (var item in existing) item.Dispose();
            if (!present) await trusted.AddAsync(certificate, ct: cancellationToken).ConfigureAwait(false);
            // Persistence has succeeded. Refresh validation even if the initiating UI is subsequently cancelled.
            using var rejected = OpenCertificateStore(OpcUaCertificateStore.Rejected);
            await rejected.DeleteAsync(certificate.Thumbprint).ConfigureAwait(false);
            if (_applicationConfiguration is { } configuration)
                await configuration.CertificateValidator.UpdateAsync(configuration).ConfigureAwait(false);
            _log.Write(GatewayLogLevel.Information, $"OPC UA 已信任客户端公共证书 [{certificate.Thumbprint}]，主题 [{certificate.Subject}]。");
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Remove one peer certificate and restart a running UA endpoint to revoke already-open channels.</summary>
    public async Task RemoveTrustedCertificateAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        thumbprint = NormalizeThumbprint(thumbprint);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var trusted = OpenCertificateStore(OpcUaCertificateStore.Trusted);
            if (!await trusted.DeleteAsync(thumbprint, cancellationToken).ConfigureAwait(false))
                throw new ArgumentException("指定证书不在可信客户端列表中。", nameof(thumbprint));
            _log.Write(GatewayLogLevel.Information, $"OPC UA 已移除客户端信任 [{thumbprint}]；关闭已有安全通道使撤销立即生效。");
            var restart = _server is not null;
            // Revocation is committed: finish closing old channels even if the caller cancels.
            if (restart) await StopCoreAsync().ConfigureAwait(false);
            if (_applicationConfiguration is { } configuration)
                await configuration.CertificateValidator.UpdateAsync(configuration).ConfigureAwait(false);
            var options = _runtime.GetOpcUaConfigurationSnapshot();
            if (restart && options.Enabled)
            {
                _appliedOptions = JsonSerializer.Serialize(options);
                await StartCoreAsync(options, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Explicitly replace this application's own key/certificate for the latest endpoint host, preserving peer trust.</summary>
    public async Task RegenerateServerCertificateAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = _runtime.GetOpcUaConfigurationSnapshot();
            var errors = OpcUaConfigurationValidator.Validate(options);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            var restart = _server is not null && options.Enabled;
            if (restart && options.EnableUserAuthentication && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable)))
                throw new InvalidOperationException($"OPC UA 密码环境变量 {options.PasswordEnvironmentVariable} 未设置；未更换证书。");
            var configuration = CreateApplicationConfiguration(options);
            await configuration.ValidateAsync(ApplicationType.Server, cancellationToken).ConfigureAwait(false);
            var ownDirectory = Path.Combine(_pkiDirectory, "own");
            // Check both stores before stopping a healthy endpoint or deleting the old key.
            foreach (var directory in new[] { Path.Combine(ownDirectory, "certs"), Path.Combine(ownDirectory, "private") })
            {
                Directory.CreateDirectory(directory);
                using var probe = new FileStream(Path.Combine(directory, $".write-check-{Guid.NewGuid():N}"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                probe.WriteByte(0);
                probe.Flush(true);
            }
            var identifier = configuration.SecurityConfiguration.ApplicationCertificates[0];
            var previous = await identifier.FindAsync(false, configuration.ApplicationUri, _telemetry, cancellationToken).ConfigureAwait(false);
            var previousThumbprint = previous?.Thumbprint;
            cancellationToken.ThrowIfCancellationRequested();
            await StopCoreAsync().ConfigureAwait(false);
            // After the explicit replacement commits, finish even if the dialog subsequently cancels.
            if (previousThumbprint is not null)
            {
                using var own = new CertificateStoreIdentifier(ownDirectory, false).OpenStore(_telemetry);
                if (!await own.DeleteAsync(previousThumbprint).ConfigureAwait(false))
                    throw new IOException("未能删除原服务端应用证书；未改动可信及拒绝证书列表。");
            }
            _applicationConfiguration = null;
            Volatile.Write(ref _status, _status with { CertificateThumbprint = null });
            // A fresh configuration prevents the SDK reusing a cached old CertificateIdentifier.
            configuration = CreateApplicationConfiguration(options);
            await configuration.ValidateAsync(ApplicationType.Server).ConfigureAwait(false);
            var application = new ApplicationInstance(configuration, _telemetry)
            { ApplicationName = configuration.ApplicationName, ApplicationType = ApplicationType.Server };
            await EnsureServerCertificateAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            if (!await application.CheckApplicationInstanceCertificatesAsync(true).ConfigureAwait(false))
                throw new InvalidOperationException("新的 OPC UA 服务端证书验证失败。");
            var certificate = await configuration.SecurityConfiguration.ApplicationCertificates[0]
                .FindAsync(false, configuration.ApplicationUri, _telemetry).ConfigureAwait(false)
                ?? throw new InvalidOperationException("重建后未找到服务端公共证书。");
            _applicationConfiguration = configuration;
            _appliedOptions = JsonSerializer.Serialize(options);
            Volatile.Write(ref _status, _status with { CertificateThumbprint = certificate.Thumbprint, LastError = null });
            _log.Write(GatewayLogLevel.Information, $"OPC UA 已重建服务端证书：旧 [{previousThumbprint ?? "无"}]，新 [{certificate.Thumbprint}]；客户端须重新信任公共证书。");
            if (restart) await StartCoreAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status, _status with { LastError = exception.Message });
            _log.Write(GatewayLogLevel.Error, $"OPC UA 重建服务端证书失败：{exception.Message}");
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task EnsureServerCertificateAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken)
    {
        var identifier = configuration.SecurityConfiguration.ApplicationCertificates[0];
        if (await identifier.FindAsync(false, configuration.ApplicationUri, _telemetry, cancellationToken).ConfigureAwait(false) is not null)
            return; // Replacing an existing certificate requires the explicit regeneration operation.
        // The official client normalizes localhost/127.0.0.1/::1 to the machine name during domain checks.
        // Include both the real endpoint host/IP and its local aliases, without disabling hostname validation.
        var domains = configuration.GetServerDomainNames()
            .Concat(configuration.ServerConfiguration.BaseAddresses.Select(address => new Uri(address).IdnHost))
            .Concat([Utils.GetHostName(), Utils.GetFullQualifiedDomainName()])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var certificate = CertificateFactory.CreateCertificate(configuration.ApplicationUri, configuration.ApplicationName,
            identifier.SubjectName, domains).SetLifeTime(36).SetRSAKeySize(2048).CreateForRSA();
        await certificate.AddToStoreAsync(identifier.StoreType, identifier.StorePath, null, _telemetry, cancellationToken).ConfigureAwait(false);
        await configuration.CertificateValidator.UpdateAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ExportCertificateAsync(OpcUaCertificateStore store, string thumbprint,
        CancellationToken cancellationToken = default)
    {
        thumbprint = NormalizeThumbprint(thumbprint);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var certificates = OpenCertificateStore(store);
            var matches = await certificates.FindByThumbprintAsync(thumbprint, cancellationToken).ConfigureAwait(false);
            try { return matches.Count > 0 ? matches[0].Export(X509ContentType.Cert) : throw new FileNotFoundException("未找到指定公共证书。"); }
            finally { foreach (var item in matches) item.Dispose(); }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<byte[]> ExportServerCertificateAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_applicationConfiguration is { } configuration)
            {
                var active = await configuration.SecurityConfiguration.ApplicationCertificates[0]
                    .FindAsync(false, configuration.ApplicationUri, _telemetry, cancellationToken).ConfigureAwait(false);
                if (active is not null) return active.Export(X509ContentType.Cert);
            }
            using var own = new CertificateStoreIdentifier(Path.Combine(_pkiDirectory, "own"), true).OpenStore(_telemetry);
            var certificates = await own.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return certificates.Cast<X509Certificate2>().OrderByDescending(item => item.NotBefore).FirstOrDefault()?.Export(X509ContentType.Cert)
                    ?? throw new InvalidOperationException("尚未生成服务端证书，请先启动 OPC UA Server 一次。");
            }
            finally { foreach (var item in certificates) item.Dispose(); }
        }
        finally { _lifecycle.Release(); }
    }

    private string StoreDirectory(OpcUaCertificateStore kind) => Path.Combine(_pkiDirectory, kind switch
    {
        OpcUaCertificateStore.Trusted => "trusted", OpcUaCertificateStore.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });
    private ICertificateStore OpenCertificateStore(OpcUaCertificateStore kind) =>
        new CertificateStoreIdentifier(StoreDirectory(kind), true).OpenStore(_telemetry);

    private static string NormalizeThumbprint(string thumbprint)
    {
        var normalized = (thumbprint ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 40 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("证书指纹必须为 40 位十六进制字符。", nameof(thumbprint));
        return normalized;
    }

    private static X509Certificate2 LoadPublicCertificate(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is 0 or > 1_048_576) throw new InvalidDataException("公共证书文件须为 1 字节到 1 MiB。");
        var data = bytes;
        var text = Encoding.UTF8.GetString(bytes);
        if (text.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            if (!PemEncoding.TryFind(text, out var fields) || text[fields.Label] != "CERTIFICATE")
                throw new InvalidDataException("只允许单个 PUBLIC CERTIFICATE PEM，不允许私钥。");
            var (offset, length) = fields.Location.GetOffsetAndLength(text.Length);
            if (!string.IsNullOrWhiteSpace(text[..offset]) || !string.IsNullOrWhiteSpace(text[(offset + length)..]))
                throw new InvalidDataException("证书文件不能包含私钥、多个证书或其他 PEM 内容。");
            data = Convert.FromBase64String(text[fields.Base64Data]);
        }
        if (X509Certificate2.GetCertContentType(data) != X509ContentType.Cert)
            throw new InvalidDataException("只允许公共 DER/CER/PEM 证书；禁止 PFX、PKCS#12、私钥及证书集合。");
        var certificate = new X509Certificate2(data);
        try
        {
            if (!data.AsSpan().SequenceEqual(certificate.RawData))
                throw new InvalidDataException("只允许一张完整 DER 证书，不能包含尾随数据或第二张证书。");
            if (certificate.HasPrivateKey) throw new InvalidDataException("不得导入私钥。");
            if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
                throw new InvalidDataException("此入口只信任明确的客户端应用证书，不能导入 CA 证书扩大信任范围。");
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is null || rsa.KeySize < 2048) throw new InvalidDataException("Basic256Sha256 客户端应用证书必须使用至少 2048 位 RSA 公钥。");
            if (certificate.SignatureAlgorithm.Value is "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1")
                throw new InvalidDataException("不允许 SHA-1 签名证书。");
            if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
                throw new InvalidDataException("证书尚未生效或已经过期。");
            return certificate;
        }
        catch { certificate.Dispose(); throw; }
    }
}
