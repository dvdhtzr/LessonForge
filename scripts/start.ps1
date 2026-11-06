# Launch the API and Vite in two visible Windows PowerShell terminals.
# Run .\scripts\setup.ps1 once first; no environment variables are required.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'ollama-prerequisites.ps1')

if (-not (Test-Path (Join-Path $root 'web/node_modules'))) {
    throw 'Frontend dependencies are missing. Run .\scripts\setup.ps1 first.'
}

$apiScript = Join-Path $PSScriptRoot 'run-api.ps1'
$webScript = Join-Path $PSScriptRoot 'run-web.ps1'

Write-Host '==> Starting the LessonForge API and frontend in separate terminals.'
Write-Host '    Stop an older API/Vite instance first, if one is still running.'
Start-Process -FilePath 'powershell.exe' -WorkingDirectory $root `
    -ArgumentList "-NoExit -File `"$apiScript`""
Start-Process -FilePath 'powershell.exe' -WorkingDirectory $root `
    -ArgumentList "-NoExit -File `"$webScript`""

Write-Host '==> Open http://localhost:5173 once the frontend reports ready.'
