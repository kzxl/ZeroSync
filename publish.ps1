<#
    publish.ps1 — Publish script for ZeroSync (Dual Mode: Full & Lite)
    Adheres to AgentOption .NET Publish Release standard & ZeroUniverse rules.
#>
[CmdletBinding()]
param(
    [ValidateSet('Full', 'Lite', 'All')]
    [string]$Mode = 'All',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Proj = Join-Path $Root "ZeroSync\ZeroSync.csproj"
$Dist = Join-Path $Root "publish"

if (Test-Path $Dist) {
    Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Mode -eq 'Full' -or $Mode -eq 'All') {
    Write-Host ">>> Publishing Full (Self-Contained Single File)..." -ForegroundColor Cyan
    $outFull = Join-Path $Dist "full"
    dotnet publish $Proj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $outFull
    Write-Host "  ✔ Full build generated at: $outFull\ZeroSync.exe" -ForegroundColor Green
}

if ($Mode -eq 'Lite' -or $Mode -eq 'All') {
    Write-Host ">>> Publishing Lite (Framework-Dependent Single File)..." -ForegroundColor Cyan
    $outLite = Join-Path $Dist "lite"
    dotnet publish $Proj -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -o $outLite
    Write-Host "  ✔ Lite build generated at: $outLite\ZeroSync.exe" -ForegroundColor Green
}

Write-Host ">>> Publish completed successfully!" -ForegroundColor Green
