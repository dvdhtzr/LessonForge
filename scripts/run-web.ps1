# Launched by start.ps1 in its own terminal.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath (Join-Path $root 'web')
Write-Host '==> LessonForge frontend (http://localhost:5173)'
& npm run dev
if ($LASTEXITCODE -ne 0) { throw "LessonForge frontend exited with code $LASTEXITCODE." }
