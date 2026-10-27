[CmdletBinding()]
param(
    [string]$Executable = 'src/EdgeGateway.WinForms/bin/Release/net8.0-windows/EdgeGateway.WinForms.exe',
    [string]$OutputDirectory = ('artifacts/ui-background-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [ValidateSet('Full', 'DatabaseMapping', 'ServerResources', 'Workbooks', 'Logging', 'UiRedesign')][string]$Scenario = 'Full',
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = if ([IO.Path]::IsPathRooted($Executable)) { [IO.Path]::GetFullPath($Executable) }
    else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Executable)) }
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) { throw "Missing executable: $applicationPath" }
# Keep all failed/interrupted evidence; never recursively delete another run's files.
if (Test-Path -LiteralPath $outputRoot) { throw "Use a new output directory: $outputRoot" }
New-Item -ItemType Directory -Path $outputRoot | Out-Null

if (-not ('EdgeGateway.BackgroundUiSmoke' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EdgeGateway {
    public static class BackgroundUiSmoke {
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes {
            public int Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo {
            public int Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public short Show, ReservedBytes;
            public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx {
            public StartupInfo Startup;
            public IntPtr Attributes;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr desktop);
        private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint sharing, ref SecurityAttributes security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity,
            IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory,
            ref StartupInfoEx startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
            IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint WaitForInputIdle(IntPtr process, uint milliseconds);

        private static string Quote(string value) {
            if (value.IndexOf('"') >= 0) throw new ArgumentException("Path contains a quote.");
            return "\"" + value + "\"";
        }
        private static IntPtr OpenOutput(string path) {
            var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
            var handle = CreateFileW(path, 0x40000000, 3, ref security, 1, 0x80, IntPtr.Zero);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }
        public static uint Run(string executable, string directory, string output, int timeout, string scenario) {
            // Do not call SwitchDesktop, activate the input desktop, or use SendInput.
            // All real HWND/focus checks stay on this private, non-visible desktop.
            var name = "EdgeGateway.Validation." + Guid.NewGuid().ToString("N");
            var desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr stdout = IntPtr.Zero, stderr = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero;
            bool attributesInitialized = false;
            ProcessInfo process = new ProcessInfo();
            bool exited = false;
            try {
                stdout = OpenOutput(Path.Combine(output, "ui-smoke.stdout.log"));
                stderr = OpenOutput(Path.Combine(output, "ui-smoke.stderr.log"));
                // An inherited desktop handle takes precedence over lpDesktop.
                // Whitelist only the two log handles, never caller desktop handles.
                IntPtr bytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(bytes);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                attributesInitialized = true;
                handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(handles, 0, stdout);
                Marshal.WriteIntPtr(handles, IntPtr.Size, stderr);
                if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles,
                    new IntPtr(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new StartupInfoEx {
                    Startup = new StartupInfo {
                        Size = Marshal.SizeOf(typeof(StartupInfoEx)), Desktop = name,
                        Flags = 0x101, Show = 1, Output = stdout, Error = stderr
                    },
                    Attributes = attributes
                };
                var arguments = scenario == "UiRedesign" ? " --smoke-test --screenshot "
                    : scenario == "Logging" ? " --smoke-logging --screenshot "
                    : scenario == "Workbooks" ? " --smoke-workbooks --screenshot "
                    : scenario == "DatabaseMapping" ? " --smoke-database-mapping --screenshot "
                    : scenario == "ServerResources" ? " --smoke-server-resources --screenshot " : " --smoke-test --layout-matrix --screenshot ";
                var command = new StringBuilder(Quote(executable) + arguments
                    + Quote(Path.Combine(output, "ui-smoke.png")));
                if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000,
                    IntPtr.Zero, directory, ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var idle = WaitForInputIdle(process.Process, 10000);
                var found = false;
                EnumWindow inspect = (window, parameter) => {
                    uint owner;
                    GetWindowThreadProcessId(window, out owner);
                    if (owner == process.ProcessId) found = true;
                    return true;
                };
                if (!EnumDesktopWindows(desktop, inspect, IntPtr.Zero) || !found)
                    throw new InvalidOperationException("No test-process window on the isolated desktop; idle=" + idle + ".");
                File.WriteAllText(Path.Combine(output, "desktop.txt"), "Desktop=" + name + "\nProcessWindowVerified=true"
                    + "\nProcessId=" + process.ProcessId + "\nInputDesktopSwitch=false\n");
                var wait = WaitForSingleObject(process.Process, checked((uint)timeout * 1000));
                if (wait == 258) throw new TimeoutException("Background WinForms smoke timed out.");
                if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                exited = true;
                uint code;
                if (!GetExitCodeProcess(process.Process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return code;
            }
            finally {
                if (process.Process != IntPtr.Zero) {
                    if (!exited) { TerminateProcess(process.Process, 1); WaitForSingleObject(process.Process, 10000); }
                    CloseHandle(process.Process);
                }
                if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
                if (stdout != IntPtr.Zero) CloseHandle(stdout);
                if (stderr != IntPtr.Zero) CloseHandle(stderr);
                if (attributes != IntPtr.Zero) {
                    if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                }
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                CloseDesktop(desktop);
            }
        }
    }
}
'@
}
$code = [EdgeGateway.BackgroundUiSmoke]::Run($applicationPath, $repositoryRoot, $outputRoot, $TimeoutSeconds, $Scenario)
[IO.File]::WriteAllText((Join-Path $outputRoot 'exit-code.txt'), [string]$code)
if ($code -ne 0) {
    Get-Content -LiteralPath (Join-Path $outputRoot 'ui-smoke.stderr.log')
    throw "Background WinForms smoke failed: $code"
}
if ($Scenario -eq 'Logging') {
    $outputText = Get-Content -LiteralPath (Join-Path $outputRoot 'ui-smoke.stdout.log') -Raw
    if ($outputText -notmatch 'Separated logging smoke passed:') { throw 'Separated logging completion marker is missing.' }
    $screenshots = @(Get-ChildItem -LiteralPath $outputRoot -Filter 'logging-*.png' -File)
    if ($screenshots.Count -ne 6 -or @($screenshots | Where-Object Length -eq 0).Count -ne 0) { throw 'Logging screenshots are missing or empty.' }
    Write-Output 'Background separated logging smoke passed.'
    return
}
if ($Scenario -eq 'Workbooks') {
    $outputText = Get-Content -LiteralPath (Join-Path $outputRoot 'ui-smoke.stdout.log') -Raw
    if ($outputText -notmatch 'Excel workbook smoke passed:') { throw 'Excel workbook completion marker is missing.' }
    $screenshots = @(Get-ChildItem -LiteralPath $outputRoot -Filter 'workbook-*.png' -File)
    if ($screenshots.Count -ne 6 -or @($screenshots | Where-Object Length -eq 0).Count -ne 0) { throw 'Excel workbook screenshots are missing or empty.' }
    Write-Output 'Background Excel workbook smoke passed.'
    return
}
if ($Scenario -eq 'ServerResources') {
    $outputText = Get-Content -LiteralPath (Join-Path $outputRoot 'ui-smoke.stdout.log') -Raw
    if ($outputText -notmatch 'Server resource charts smoke passed:') { throw 'Server resource charts completion marker is missing.' }
    $screenshots = @(Get-ChildItem -LiteralPath $outputRoot -Filter '*server-resources-*.png' -File)
    if ($screenshots.Count -ne 4 -or @($screenshots | Where-Object Length -eq 0).Count -ne 0) { throw 'Server resource chart screenshots are missing or empty.' }
    Write-Output 'Background server resource charts smoke passed.'
    return
}
if ($Scenario -eq 'DatabaseMapping') {
    $outputText = Get-Content -LiteralPath (Join-Path $outputRoot 'ui-smoke.stdout.log') -Raw
    if ($outputText -notmatch 'Custom point mapping smoke passed:') { throw 'Database mapping completion marker is missing.' }
    $screenshots = @(Get-ChildItem -LiteralPath $outputRoot -Filter '*custom-point-mapping*.png' -File)
    if ($screenshots.Count -ne 1 -or $screenshots[0].Length -eq 0) { throw 'Database mapping screenshot is missing or empty.' }
    Write-Output 'Background database mapping smoke passed.'
    return
}
if ($Scenario -eq 'UiRedesign') {
    $outputText = [IO.File]::ReadAllText((Join-Path $outputRoot 'ui-smoke.stdout.log'))
    $errorText = [IO.File]::ReadAllText((Join-Path $outputRoot 'ui-smoke.stderr.log'))
    if (-not [string]::IsNullOrWhiteSpace($errorText)) { throw ('UI smoke wrote errors: ' + $errorText) }
    $markers = @(
        'Initial overview scroll passed', 'AntdUI controls passed:',
        'Overview health smoke passed:', 'Deferred tab navigation passed:',
        'WinForms editor smoke passed: 15 drivers',
        'Device configuration smoke passed: 15 dedicated driver windows',
        'Existing device driver change smoke passed:', 'Device row entry smoke passed:',
        'Device enabled configuration smoke passed:', 'Unified device dialog smoke passed:',
        'Dynamic driver switch passed at actual', 'Device import smoke passed:',
        'Device-action/autosave smoke passed:', 'Live configuration smoke passed: A kept collecting',
        'Completed capabilities smoke passed at actual', 'Usability smoke passed at actual',
        'Data recording configuration smoke passed:'
    )
    foreach ($marker in $markers) {
        if (-not $outputText.Contains($marker)) { throw ('Missing UI smoke completion marker: ' + $marker) }
    }
    foreach ($page in @('', '-devices', '-realtime', '-history', '-diagnostics', '-database', '-cloud', '-logs')) {
        $imagePath = Join-Path $outputRoot ('ui-smoke' + $page + '.png')
        if (-not (Test-Path -LiteralPath $imagePath) -or (Get-Item -LiteralPath $imagePath).Length -eq 0) {
            throw ('Missing page screenshot: ' + $imagePath)
        }
    }
    Write-Output 'Background UI redesign baseline passed (eight pages and editor behavior; no DPI matrix).'
    return
}
# Reuse the same assertions, marker counts and image checks as the interactive runner.
& (Join-Path $PSScriptRoot 'smoke-test-winforms.ps1') -OutputDirectory $outputRoot -VerifyOnly
