# Build the .NET API and tests in Release mode.
# The frontend is validated separately by scripts/web.ps1 (the solution includes web.esproj).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    Write-Host '==> Building LessonForge API (Release)'
    & dotnet build LessonForge.Api/LessonForge.Api.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "LessonForge API build failed with exit code $LASTEXITCODE."
    }

    Write-Host '==> Building LessonForge tests (Release)'
    & dotnet build LessonForge.Tests/LessonForge.Tests.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "LessonForge test build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
