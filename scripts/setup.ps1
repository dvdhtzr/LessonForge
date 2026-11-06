# Windows-first, reproducible LessonForge setup.
# Installs required Ollama models named in appsettings.json and verifies the app.
$ErrorActionPreference = 'Stop'

$required = @('dotnet', 'node', 'npm', 'ollama')
foreach ($command in $required) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Missing prerequisite '$command'. Install .NET 10 SDK, Node.js 22+ and Ollama, then rerun .\scripts\setup.ps1."
    }
}

$sdks = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw '.NET 10 SDK was not found. Install the .NET 10 SDK (not just the runtime) and retry.'
}

$nodeVersion = (& node --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read Node.js version.' }
$nodeParts = $nodeVersion.TrimStart('v').Split('.')
$nodeMajor = [int]$nodeParts[0]
$nodeMinor = [int]$nodeParts[1]
if ($nodeMajor -lt 22 -or ($nodeMajor -eq 22 -and $nodeMinor -lt 12)) {
    throw "Node.js 22.12 or newer is required (detected $nodeVersion)."
}

& (Join-Path $PSScriptRoot 'ollama-prerequisites.ps1') -InstallModels

Write-Host ''
Write-Host '==> Verifying backend, tests and frontend'
& (Join-Path $PSScriptRoot 'check.ps1')

Write-Host ''
Write-Host '==> LessonForge setup complete.'
Write-Host '    Start the application with .\scripts\start.ps1'
