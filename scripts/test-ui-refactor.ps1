[CmdletBinding()]
param(
    [ValidateSet('General', 'Logging')][string]$Mode = 'General',
    [string]$Executable = 'src/EdgeGateway.WinForms/bin/Release/net8.0-windows/EdgeGateway.WinForms.exe',
    [string]$OutputDirectory = 'artifacts/ui-regression',
    [ValidateRange(1, 900)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $applicationPath = [IO.Path]::GetFullPath($Executable)
    if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) { throw "Executable not found: $applicationPath" }
    $outputRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $Mode.ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    # Only replace this invocation's isolated evidence. Never touch AppData or production logs.
    Get-ChildItem -LiteralPath $outputRoot -Filter 'ui-smoke*' | Remove-Item -Recurse -Force
    $screenshot = Join-Path $outputRoot 'ui-smoke.png'
    $stdout = Join-Path $outputRoot 'ui-smoke.stdout.log'
    $stderr = Join-Path $outputRoot 'ui-smoke.stderr.log'
    $flag = if ($Mode -eq 'Logging') { '--smoke-logging' } else { '--smoke-test' }
    $process = Start-Process -FilePath $applicationPath -ArgumentList @($flag, '--screenshot', ('"' + $screenshot + '"')) -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Mode WinForms smoke timed out. Evidence retained in $outputRoot"
        }
        $process.WaitForExit()
        Get-Content -LiteralPath $stdout
        $errors = Get-Content -LiteralPath $stderr -Raw
        if ($errors) { Write-Output $errors }
        if ($process.ExitCode -ne 0) { throw "$Mode WinForms smoke failed with exit code $($process.ExitCode)." }
        if (-not [string]::IsNullOrWhiteSpace($errors)) { throw "$Mode WinForms smoke wrote errors." }
        if (-not (Get-ChildItem -LiteralPath $outputRoot -Filter '*.png')) { throw "$Mode smoke produced no screenshots." }
        Write-Output "$Mode WinForms smoke passed at the runner's actual DPI; no multi-monitor or Visual Studio Designer acceptance is implied."
    }
    finally { $process.Dispose() }
}
finally { Pop-Location }
