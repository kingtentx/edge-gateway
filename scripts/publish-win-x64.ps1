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
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $publishDirectory
    Copy-Item LICENSES -Destination $publishDirectory -Recurse -Force
    Copy-Item examples -Destination $publishDirectory -Recurse -Force
    $archive = Join-Path $outputRoot "EdgeGateway-win-x64.zip"
    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -Force
    Write-Host "Published: $archive"
}
finally { Pop-Location }
