[CmdletBinding()]
param([string]$OutputDirectory = "artifacts")

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
    # A clean staging directory prevents old plugin/DLL files entering a new package.
    $publishDirectory = Join-Path $outputRoot ("win-x64-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    & dotnet publish src/EdgeGateway.WinForms/EdgeGateway.WinForms.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    # Fail publication if any original upstream driver was left out of the application.
    $migration = Get-Content 'docs/driver-migration-manifest.json' -Raw | ConvertFrom-Json
    $requiredAssemblies = @('EdgeGateway.IotGateway', 'PluginInterface', 'Interop.OPCAutomation', 'FontAwesome.Sharp') + @($migration.drivers | ForEach-Object { $_.assembly })
    foreach ($assembly in $requiredAssemblies) {
        if (-not (Test-Path (Join-Path $publishDirectory ($assembly + '.dll')))) {
            throw "Required migrated driver/dependency missing from publish output: $assembly.dll"
        }
    }
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $publishDirectory
    Copy-Item LICENSES -Destination $publishDirectory -Recurse -Force
    Copy-Item examples -Destination $publishDirectory -Recurse -Force
    Copy-Item docs -Destination $publishDirectory -Recurse -Force
    # README links to the reusable development skill and its reference documents.
    Copy-Item .agents -Destination $publishDirectory -Recurse -Force
    # Bind a downloaded package to its actual source and the verification performed
    # by this Windows job. A manual local publish does not claim CI verification.
    $verification = $null
    if ($env:GITHUB_ACTIONS -eq 'true') {
        [xml]$testReport = Get-Content (Join-Path $outputRoot 'test-results/tests.trx') -Raw
        $counts = $testReport.TestRun.ResultSummary.Counters
        $layout = Get-Content (Join-Path $outputRoot 'ui-smoke-layout-matrix/layout-matrix.json') -Raw | ConvertFrom-Json
        $verification = [ordered]@{
            testsTotal = [int]$counts.total
            testsPassed = [int]$counts.passed
            testsFailed = [int]$counts.failed
            layoutCasesPassed = $layout.passedCases
            layoutCasesExpected = $layout.expectedCases
            actualDeviceDpi = $layout.actualDeviceDpi
            viewportSimulation = $layout.viewportSimulation
            operatingSystem = $layout.operatingSystem
            focusRecoveryCount = $layout.focusRecoveryCount
        }
    }
    [ordered]@{
        sourceCommit = $env:GITHUB_SHA
        repository = $env:GITHUB_REPOSITORY
        workflowRunId = $env:GITHUB_RUN_ID
        workflowRunAttempt = $env:GITHUB_RUN_ATTEMPT
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        verification = $verification
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $publishDirectory 'build-info.json') -Encoding utf8
    $archive = Join-Path $outputRoot "EdgeGateway-win-x64.zip"
    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -Force
    Write-Host "Published: $archive"
}
finally { Pop-Location }
