[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$WindbgXDir = "",

    [string]$ExtensionDir = "",

    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Path $MyInvocation.MyCommand.Path -Parent
$projectPath = Join-Path $repoRoot "src\WinDbgBridge\WinDbgBridge.csproj"
$clientProjectPath = Join-Path $repoRoot "src\WinDbgBridge.Cli\WinDbgBridge.Cli.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\WinDbgBridge\$Configuration"
$clientPublishDir = Join-Path $repoRoot "artifacts\publish\windbg-bridge\$Configuration"

if ([string]::IsNullOrWhiteSpace($ExtensionDir)) {
    $ExtensionDir = Join-Path $env:LOCALAPPDATA "DBG\UIExtensions"
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $clientPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $ExtensionDir | Out-Null

$publishArgs = @(
    "publish",
    $projectPath,
    "--configuration", $Configuration,
    "--output", $publishDir,
    "--nologo",
    "--verbosity", "minimal"
)

$clientPublishArgs = @(
    "publish",
    $clientProjectPath,
    "--configuration", $Configuration,
    "--output", $clientPublishDir,
    "--nologo",
    "--verbosity", "minimal"
)

if (-not [string]::IsNullOrWhiteSpace($WindbgXDir)) {
    $publishArgs += "-p:WindbgXDir=$WindbgXDir"
}

Write-Host "Publishing WinDbgBridge..."
& dotnet @publishArgs

Write-Host "Publishing windbg-bridge..."
& dotnet @clientPublishArgs

$mainAssembly = Join-Path $publishDir "WinDbgBridge.dll"

if (-not (Test-Path $mainAssembly)) {
    throw "Publish did not produce $mainAssembly"
}

$filesToRemove = @(
    (Join-Path $ExtensionDir "WinDbgBridge.dll"),
    (Join-Path $ExtensionDir "WinDbgBridge.pdb"),
    (Join-Path $ExtensionDir "WinDbgBridge.deps.json")
)

foreach ($path in $filesToRemove) {
    if (Test-Path $path) {
        Remove-Item -Force $path
    }
}

Copy-Item -Path $mainAssembly -Destination (Join-Path $ExtensionDir "WinDbgBridge.dll")

if ($IncludeSymbols) {
    $pdbPath = Join-Path $publishDir "WinDbgBridge.pdb"
    if (Test-Path $pdbPath) {
        Copy-Item -Path $pdbPath -Destination (Join-Path $ExtensionDir "WinDbgBridge.pdb")
    }
}

Write-Host ""
Write-Host "Installed files:"
Get-ChildItem -Path $ExtensionDir -File | Where-Object { $_.Name -like "WinDbgBridge*" } | Select-Object Name, Length | Format-Table -AutoSize

Write-Host ""
Write-Host "WinDbgBridge installed to $ExtensionDir"
Write-Host "windbg-bridge published to $clientPublishDir"
