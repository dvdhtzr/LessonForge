# Run the .NET unit tests (tests require neither Ollama nor live Wikipedia).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    Write-Host '==> Running LessonForge xUnit tests (Release)'
    & dotnet test LessonForge.Tests/LessonForge.Tests.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "LessonForge tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
