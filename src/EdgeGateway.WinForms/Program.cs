using EdgeGateway.Hosting;
using System.Security.Cryptography;
using System.Text;

namespace EdgeGateway.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var layoutMatrix = args.Contains("--layout-matrix", StringComparer.OrdinalIgnoreCase);
        var smokeTest = layoutMatrix || args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var screenshotIndex = Array.FindIndex(args, x => x.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null;
        var runtimeRoot = smokeTest
            ? Path.Combine(Path.GetTempPath(), "EdgeGateway.Smoke", Guid.NewGuid().ToString("N"))
            : AppContext.BaseDirectory;
        var dataDirectory = Path.Combine(runtimeRoot, "AppData");
        var driverDirectory = Path.Combine(runtimeRoot, "drivers");
        var mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory)))[..24];
        using var instanceLock = new Mutex(true, "Local\\EdgeGateway." + mutexKey, out var ownsLock);
        if (!ownsLock)
        {
            MessageBox.Show("采集网关已在当前用户会话中运行，请切换到已打开的窗口。", "采集网关", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        GatewayHost? host = null;
        try
        {
            host = GatewayHost.CreateAsync(dataDirectory, pluginDirectory: driverDirectory, enableVerificationFaults: smokeTest).GetAwaiter().GetResult();
            using var window = new MainForm(host.Application, host.DataDirectory, smokeTest, screenshotPath, layoutMatrix,
                smokeTest ? host.BeginConfigurationWriteFailureForVerification : null, driverDirectory);
            System.Windows.Forms.Application.Run(window);
            return window.SmokeTestFailed ? 1 : 0;
        }
        catch (Exception exception)
        {
            var message = "启动失败：" + exception.Message + Environment.NewLine + Environment.NewLine
                + "数据目录：" + dataDirectory + Environment.NewLine
                + "请检查 AppData/gateway.config.db 配置数据库、drivers 驱动目录及其写入权限后重试。";
            if (smokeTest) Console.Error.WriteLine(exception);
            else MessageBox.Show(message, "采集网关启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Preserve original startup error. */ }
            if (smokeTest)
            {
                try { Directory.Delete(dataDirectory, true); } catch { }
            }
            instanceLock.ReleaseMutex();
        }
    }
}
