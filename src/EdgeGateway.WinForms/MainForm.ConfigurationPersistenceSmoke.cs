namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private readonly Func<IDisposable>? _configurationWriteFailureForSmoke;

    private IDisposable RejectConfigurationWritesForSmoke()
    {
        if (!_smokeTest || _configurationWriteFailureForSmoke is null)
            throw new InvalidOperationException("Persistence fault injection requires an isolated smoke-test host.");
        return _configurationWriteFailureForSmoke();
    }

    private static bool IsConfigurationWriteFailureForSmoke(Exception exception) =>
        exception.Message.Contains("smoke_configuration_write_rejected", StringComparison.Ordinal);
}
