namespace EdgeGateway.Model;

public sealed class OpcUaServerConfiguration
{
    public bool Enabled { get; set; }
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840/EdgeGateway";
    public bool AllowAnonymous { get; set; } = true;
    public bool EnableUserAuthentication { get; set; }
    public string Username { get; set; } = "operator";
    public string PasswordEnvironmentVariable { get; set; } = "EDGE_OPCUA_PASSWORD";
    public bool AllowRemoteWrite { get; set; }
    public int UpdateIntervalMs { get; set; } = 250;
    public int MaxSessions { get; set; } = 20;
    public int OperationTimeoutMs { get; set; } = 5000;
}

public sealed record OpcUaServerStatus
{
    public bool Enabled { get; init; }
    public bool IsRunning { get; init; }
    public string EndpointUrl { get; init; } = "";
    public string NamespaceUri { get; init; } = "urn:kingtentx:edge-gateway:opcua";
    public int NodeCount { get; init; }
    public int SessionCount { get; init; }
    public string? CertificateThumbprint { get; init; }
    public string CertificateDirectory { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public string? LastError { get; init; }
}

public enum OpcUaCertificateStore { Trusted, Rejected }

public sealed record OpcUaCertificateInfo(OpcUaCertificateStore Store, string Thumbprint, string Subject,
    string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string FileName);
