# Restore and build the .NET solution (Release).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
Write-Host '==> dotnet build (Release)'
dotnet build LessonForge.slnx --configuration Release
