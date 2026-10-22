using EdgeGateway.Abstractions;
using EdgeGateway.DataAccess;
using EdgeGateway.Driver.Modbus;
using EdgeGateway.Driver.Simulator;
using EdgeGateway.IotGateway;
using EdgeGateway.Service;
using System.Security.Cryptography;
using System.Text;

namespace EdgeGateway.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var screenshotIndex = Array.FindIndex(args, x => x.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null;
        var dataDirectory = smokeTest
            ? Path.Combine(Path.GetTempPath(), "EdgeGateway.Smoke", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeGateway");
        var mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory)))[..24];
        using var instanceLock = new Mutex(true, "Local\\EdgeGateway." + mutexKey, out var ownsLock);
        if (!ownsLock)
        {
            MessageBox.Show("采集网关已在当前用户会话中运行，请切换到已打开的窗口。", "采集网关", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        SqliteGatewayStore? store = null;
        GatewayRuntime? runtime = null;
        MqttTelemetryPublisher? publisher = null;
        FileStream? dataLock = null;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            try
            {
                dataLock = new FileStream(Path.Combine(dataDirectory, "gateway.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("数据目录已被其他采集网关进程使用，请关闭对应进程后重试。", exception);
            }
            var configurationStore = new JsonConfigurationStore(Path.Combine(dataDirectory, "gateway.json"), DemoConfiguration.Create);
            var configuration = configurationStore.LoadAsync().GetAwaiter().GetResult();
            store = new SqliteGatewayStore(Path.Combine(dataDirectory, "gateway.db"));
            store.InitializeAsync().GetAwaiter().GetResult();
            var log = new GatewayLog(Path.Combine(dataDirectory, "logs"));
            var builtInDrivers = new IDriverFactory[]
            {
                new SimulatorDriverFactory(), new ModbusTcpDriverFactory(), new ModbusRtuDriverFactory()
            };
            var drivers = new DriverRegistry(builtInDrivers.Concat(IotGatewayDriverCatalog.CreateFactories(log)), log);
            drivers.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "plugins"));
            var errors = ConfigurationValidator.Validate(configuration, drivers);
            if (errors.Count > 0) throw new InvalidDataException("配置检查未通过：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
            publisher = new MqttTelemetryPublisher();
            runtime = new GatewayRuntime(configuration, drivers, store, publisher, log);
            using var window = new MainForm(configuration, configurationStore, store, drivers, runtime, log, dataDirectory, smokeTest, screenshotPath);
            Application.Run(window);
            return window.SmokeTestFailed ? 1 : 0;
        }
        catch (Exception exception)
        {
            var message = "启动失败：" + exception.Message + Environment.NewLine + Environment.NewLine
                + "数据目录：" + dataDirectory + Environment.NewLine
                + "已有配置和历史数据未被重置。请检查 gateway.json 或从 gateway.json.bak 恢复配置后重试。";
            if (smokeTest) Console.Error.WriteLine(exception);
            else MessageBox.Show(message, "采集网关启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Preserve original startup error. */ }
            try { publisher?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { store?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            dataLock?.Dispose();
            if (smokeTest)
            {
                try { Directory.Delete(dataDirectory, true); } catch { }
            }
            instanceLock.ReleaseMutex();
        }
    }
}
