# Run the .NET unit tests (offline — no Ollama or network required).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
Write-Host '==> dotnet test'
dotnet test LessonForge.Tests/LessonForge.Tests.csproj --configuration Release
