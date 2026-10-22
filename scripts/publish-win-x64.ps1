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
    $requiredAssemblies = @('EdgeGateway.IotGateway', 'PluginInterface', 'Interop.OPCAutomation') + @($migration.drivers | ForEach-Object { $_.assembly })
    foreach ($assembly in $requiredAssemblies) {
        if (-not (Test-Path (Join-Path $publishDirectory ($assembly + '.dll')))) {
            throw "Required migrated driver/dependency missing from publish output: $assembly.dll"
        }
    }
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $publishDirectory
    Copy-Item LICENSES -Destination $publishDirectory -Recurse -Force
    Copy-Item examples -Destination $publishDirectory -Recurse -Force
    Copy-Item docs -Destination $publishDirectory -Recurse -Force
    $archive = Join-Path $outputRoot "EdgeGateway-win-x64.zip"
    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -Force
    Write-Host "Published: $archive"
}
finally { Pop-Location }
