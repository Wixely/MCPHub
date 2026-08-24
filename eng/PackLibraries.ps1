<#
.SYNOPSIS
Packs the three embeddable MCPHub libraries into ./artifacts/packages with symbols.

.DESCRIPTION
Used by CI on every PR (packability gate) and by the release workflow before pushing to the
Wixely GitHub Packages feed. Fails if the expected number of packages does not materialise,
so a project silently dropping out of the pack set breaks the build instead of the release.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output = 'artifacts/packages',
    [switch]$CI
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot $Output

$projects = @(
    'src/MCPHub.Proxy/MCPHub.Proxy.csproj',
    'src/MCPHub.Hosting/MCPHub.Hosting.csproj',
    'src/MCPHub.Processes/MCPHub.Processes.csproj'
)

if (Test-Path $outputDir) {
    Remove-Item -Recurse -Force $outputDir
}

$ciFlag = if ($CI) { '-p:ContinuousIntegrationBuild=true' } else { $null }

foreach ($project in $projects) {
    Write-Host "Packing $project…"
    dotnet pack (Join-Path $repoRoot $project) -c $Configuration -o $outputDir --nologo $ciFlag
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $project (exit $LASTEXITCODE)."
    }
}

$nupkgs = @(Get-ChildItem $outputDir -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' })
$snupkgs = @(Get-ChildItem $outputDir -Filter '*.snupkg')

Write-Host "Packed $($nupkgs.Count) packages, $($snupkgs.Count) symbol packages:"
$nupkgs + $snupkgs | ForEach-Object { Write-Host "  $($_.Name)" }

$expected = $projects.Count
if ($nupkgs.Count -ne $expected -or $snupkgs.Count -ne $expected) {
    throw "Expected $expected .nupkg + $expected .snupkg but found $($nupkgs.Count) + $($snupkgs.Count)."
}
