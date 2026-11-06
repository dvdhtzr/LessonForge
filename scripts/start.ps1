# One command to prepare and launch LessonForge on Windows:
# .\scripts\start.ps1
# Requires the .NET 10 SDK, Node.js 22.12+ and Ollama to be installed (not configured).
param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $root 'LessonForge.Api/appsettings.json'

function Assert-Command([string]$name, [string]$guidance) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Missing '$name'. $guidance"
    }
}

function Test-HttpReady([string]$url) {
    try {
        $null = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
        return $true
    }
    catch {
        return $false
    }
}

function Wait-ForService([string]$url, [string]$service, [int]$attempts) {
    for ($i = 0; $i -lt $attempts; $i++) {
        if (Test-HttpReady $url) {
            Write-Host "==> $service is ready."
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "$service did not become ready at $url. Inspect its PowerShell terminal for errors."
}

if (-not (Test-Path $configPath)) {
    throw "Missing committed application configuration: $configPath"
}

Write-Host '==> Checking installed prerequisites'
Assert-Command 'dotnet' 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0.'
Assert-Command 'node' 'Install Node.js 22.12+ from https://nodejs.org/.'
Assert-Command 'npm' 'Install Node.js with npm from https://nodejs.org/.'
Assert-Command 'ollama' 'Install Ollama for Windows from https://ollama.com/download/windows.'

$sdks = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw 'The .NET 10 SDK is required. Install the SDK rather than only the runtime.'
}

$nodeVersion = (& node --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not determine the Node.js version.' }
$nodeParts = $nodeVersion.TrimStart('v').Split('.')
$major = [int]$nodeParts[0]
$minor = [int]$nodeParts[1]
if ($major -lt 22 -or ($major -eq 22 -and $minor -lt 12)) {
    throw "Node.js 22.12 or newer is required (found $nodeVersion)."
}

# Launch Ollama only when no service is already serving its usual local address.
$ollamaUrl = 'http://127.0.0.1:11434/api/version'
if (Test-HttpReady $ollamaUrl) {
    Write-Host '==> Ollama is already running.'
}
else {
    Write-Host '==> Starting Ollama in its own terminal'
    $ollamaScript = Join-Path $PSScriptRoot 'run-ollama.ps1'
    Start-Process -FilePath 'powershell.exe' -WorkingDirectory $root `
        -ArgumentList "-NoExit -NoProfile -File `"$ollamaScript`""
    Wait-ForService $ollamaUrl 'Ollama' 30
}

# Read the exact required model names from the committed appsettings.json;
# download on first run, reuse on all subsequent runs.
& (Join-Path $PSScriptRoot 'ollama-prerequisites.ps1') -InstallModels

# Install frontend dependencies only if missing or its lockfile/Node version changed.
# Reuse the same installation stamp as the local verification gate.
$web = Join-Path $root 'web'
$lockfile = Join-Path $web 'package-lock.json'
if (-not (Test-Path $lockfile)) { throw 'Missing web/package-lock.json.' }
$modules = Join-Path $web 'node_modules'
$stampFile = Join-Path $modules '.lessonforge-install-stamp'
$expectedStamp = "$((Get-FileHash $lockfile -Algorithm SHA256).Hash)|$nodeVersion"
$actualStamp = if (Test-Path $stampFile) { (Get-Content $stampFile -Raw).Trim() } else { '' }
if (-not (Test-Path $modules) -or $actualStamp -ne $expectedStamp) {
    Write-Host '==> Preparing frontend dependencies (first run or lockfile changed)'
    # web.ps1 validates lint/build as well as installing dependencies; this is
    # only run when dependencies need refreshing, not on every application start.
    & (Join-Path $PSScriptRoot 'web.ps1')
}
else {
    Write-Host '==> Frontend dependencies already installed.'
}

# Never silently start on alternate ports or mix our runs with stale processes.
foreach ($port in @(5173, 5174)) {
    $listener = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
    if ($listener.Count -gt 0) {
        throw "Port $port is already in use. Stop the existing API/Vite instance before restarting LessonForge."
    }
}

$apiScript = Join-Path $PSScriptRoot 'run-api.ps1'
$webScript = Join-Path $PSScriptRoot 'run-web.ps1'

Write-Host '==> Starting LessonForge API and frontend in separate terminals'
Start-Process -FilePath 'powershell.exe' -WorkingDirectory $root `
    -ArgumentList "-NoExit -NoProfile -File `"$apiScript`""
Start-Process -FilePath 'powershell.exe' -WorkingDirectory $root `
    -ArgumentList "-NoExit -NoProfile -File `"$webScript`""

Wait-ForService 'http://127.0.0.1:5174/' 'LessonForge API' 90
Wait-ForService 'http://127.0.0.1:5173/' 'LessonForge frontend' 30

Write-Host ''
Write-Host '==> LessonForge is ready at http://localhost:5173'
if (-not $NoBrowser) {
    Start-Process 'http://localhost:5173'
}
