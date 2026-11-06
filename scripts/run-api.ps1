# Launched by start.ps1 in its own terminal.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
Write-Host '==> LessonForge API (http://localhost:5174)'
& dotnet run --project LessonForge.Api --launch-profile http
if ($LASTEXITCODE -ne 0) { throw "LessonForge API exited with code $LASTEXITCODE." }
