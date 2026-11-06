# Restore and build the .NET solution (Release).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
# Build the .NET projects explicitly: the solution also contains web/web.esproj
# (the front end), which needs Node. Use scripts/web.ps1 for the front end.
Write-Host '==> dotnet build (Release)'
dotnet build LessonForge.Api/LessonForge.Api.csproj --configuration Release
dotnet build LessonForge.Tests/LessonForge.Tests.csproj --configuration Release
