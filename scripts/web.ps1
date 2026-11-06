# Install web dependencies, lint, type-check, and build the frontend.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location (Join-Path $root 'web')
try {
    if (Test-Path 'package-lock.json') {
        Write-Host '==> Installing locked web dependencies (npm ci)'
        & npm ci
    }
    else {
        Write-Host '==> Installing web dependencies (npm install)'
        & npm install
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Web dependency installation failed with exit code $LASTEXITCODE."
    }

    Write-Host '==> Linting frontend'
    & npm run lint
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend lint failed with exit code $LASTEXITCODE."
    }

    Write-Host '==> Type-checking and building frontend'
    & npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
