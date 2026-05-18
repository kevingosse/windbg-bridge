[CmdletBinding()]
param(
    [string]$ExtensionDir = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Path $MyInvocation.MyCommand.Path -Parent

$extensionDll = Join-Path $scriptDir "WinDbgBridge.dll"
$cliDir = Join-Path $scriptDir "windbg-bridge"
$skillFile = Join-Path $scriptDir "SKILL.md"

if (-not (Test-Path $extensionDll)) {
    throw "WinDbgBridge.dll not found in $scriptDir. Run this script from the release directory."
}

if (-not (Test-Path (Join-Path $cliDir "windbg-bridge.exe"))) {
    throw "windbg-bridge\windbg-bridge.exe not found in $scriptDir. Run this script from the release directory."
}

# --- Install extension ---

if ([string]::IsNullOrWhiteSpace($ExtensionDir)) {
    $ExtensionDir = Join-Path $env:LOCALAPPDATA "DBG\UIExtensions"
}

$cliInstallDir = Join-Path $ExtensionDir "windbg-bridge"

New-Item -ItemType Directory -Force -Path $ExtensionDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliInstallDir | Out-Null

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

Copy-Item -Path $extensionDll -Destination (Join-Path $ExtensionDir "WinDbgBridge.dll")
Copy-Item -Path (Join-Path $cliDir "*") -Destination $cliInstallDir -Force

Write-Host "WinDbg extension installed to $ExtensionDir"
Write-Host "Bridge CLI installed to $cliInstallDir"

# --- Install skill ---

if (-not (Test-Path $skillFile)) {
    Write-Host "SKILL.md not found, skipping skill installation."
    Write-Host ""
    Write-Host "Done!"
    return
}

Write-Host ""
Write-Host "The WinDbg bridge includes a SKILL.md that lets AI coding assistants use it."
Write-Host "Where would you like to install it?"
Write-Host ""
Write-Host "  [1] .claude/skills/  (Claude Code)"
Write-Host "  [2] .agents/skills/  (Codex, Copilot, Cursor, Windsurf, ...)"
Write-Host "  [3] Both"
Write-Host "  [4] Skip"
Write-Host ""

$choice = Read-Host "Choice [3]"
if ([string]::IsNullOrWhiteSpace($choice)) { $choice = "3" }

$skillTargets = @()

switch ($choice) {
    "1" { $skillTargets = @(Join-Path $HOME ".claude\skills\windbg-bridge") }
    "2" { $skillTargets = @(Join-Path $HOME ".agents\skills\windbg-bridge") }
    "3" { $skillTargets = @(
            (Join-Path $HOME ".claude\skills\windbg-bridge"),
            (Join-Path $HOME ".agents\skills\windbg-bridge")
          )
        }
    "4" { }
    default { Write-Host "Unknown choice, skipping skill installation." }
}

foreach ($target in $skillTargets) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path $skillFile -Destination (Join-Path $target "SKILL.md") -Force
    Write-Host "Skill installed to $target"
}

Write-Host ""
Write-Host "Done!"
