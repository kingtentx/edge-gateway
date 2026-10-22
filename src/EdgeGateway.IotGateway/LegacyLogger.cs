using EdgeGateway.Abstractions;
using EdgeGateway.Model;
using Microsoft.Extensions.Logging;

namespace EdgeGateway.IotGateway;

internal sealed class LegacyLogger(IGatewayLog log, string prefix) : ILogger
{
    public string? LastError { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message += "：" + exception.GetBaseException().Message;
        if (logLevel >= LogLevel.Error) LastError = message;
        log.Write(logLevel switch
        {
            >= LogLevel.Error => GatewayLogLevel.Error,
            LogLevel.Warning => GatewayLogLevel.Warning,
            _ => GatewayLogLevel.Information
        }, $"[{prefix}] {message}");
    }
}
