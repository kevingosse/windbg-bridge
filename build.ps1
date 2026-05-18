[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Deploy,

    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Path $MyInvocation.MyCommand.Path -Parent
$projectPath = Join-Path $repoRoot "src\WinDbgBridge\WinDbgBridge.csproj"
$clientProjectPath = Join-Path $repoRoot "src\WinDbgBridge.Cli\WinDbgBridge.Cli.csproj"
$extensionPublishDir = Join-Path $repoRoot "artifacts\publish\WinDbgBridge\$Configuration"
$cliPublishDir = Join-Path $repoRoot "artifacts\publish\windbg-bridge\$Configuration"

New-Item -ItemType Directory -Force -Path $extensionPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliPublishDir | Out-Null

Write-Host "Publishing WinDbgBridge extension..."
& dotnet publish $projectPath --configuration $Configuration --output $extensionPublishDir --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Extension publish failed." }

Write-Host "Publishing windbg-bridge CLI..."
& dotnet publish $clientProjectPath --configuration $Configuration --output $cliPublishDir --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

Write-Host ""
Write-Host "Build output:"
Write-Host "  Extension: $extensionPublishDir"
Write-Host "  CLI:       $cliPublishDir"

if (-not $Deploy) {
    return
}

Write-Host ""
Write-Host "Deploying..."

$extensionDir = Join-Path $env:LOCALAPPDATA "DBG\UIExtensions"
$cliInstallDir = Join-Path $extensionDir "windbg-bridge"

New-Item -ItemType Directory -Force -Path $extensionDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliInstallDir | Out-Null

$filesToRemove = @(
    (Join-Path $extensionDir "WinDbgBridge.dll"),
    (Join-Path $extensionDir "WinDbgBridge.pdb"),
    (Join-Path $extensionDir "WinDbgBridge.deps.json")
)

foreach ($path in $filesToRemove) {
    if (Test-Path $path) {
        Remove-Item -Force $path
    }
}

Copy-Item -Path (Join-Path $extensionPublishDir "WinDbgBridge.dll") -Destination (Join-Path $extensionDir "WinDbgBridge.dll")

if ($IncludeSymbols) {
    $pdbPath = Join-Path $extensionPublishDir "WinDbgBridge.pdb"
    if (Test-Path $pdbPath) {
        Copy-Item -Path $pdbPath -Destination (Join-Path $extensionDir "WinDbgBridge.pdb")
    }
}

Copy-Item -Path (Join-Path $cliPublishDir "*") -Destination $cliInstallDir -Force

Write-Host "  Extension deployed to $extensionDir"
Write-Host "  CLI deployed to $cliInstallDir"
