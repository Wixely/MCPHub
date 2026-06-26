#!/usr/bin/env pwsh
# Publishes MCPHub as a single self-contained executable per runtime into artifacts/<rid>/.
# Usage:  ./scripts/publish.ps1            (win-x64 + linux-x64)
#         ./scripts/publish.ps1 win-x64    (one rid)
param([string[]] $Rids = @('win-x64', 'linux-x64'))

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$proj = Join-Path $repo 'src/MCPHub.App/MCPHub.App.csproj'

foreach ($rid in $Rids) {
    $out = Join-Path $repo "artifacts/$rid"
    Write-Host "Publishing $rid -> $out" -ForegroundColor Cyan
    dotnet publish $proj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
}

Write-Host "Done. See artifacts/." -ForegroundColor Green
