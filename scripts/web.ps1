# Verify the frontend without reinstalling dependencies on every run.
# A lockfile/Node-version stamp forces npm ci only when dependencies need refreshing.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location (Join-Path $root 'web')
try {
    $modules = Join-Path (Get-Location).Path 'node_modules'
    $stampPath = Join-Path $modules '.lessonforge-install-stamp'
    $lockfile = Join-Path (Get-Location).Path 'package-lock.json'

    if (-not (Test-Path $lockfile)) {
        throw 'package-lock.json is missing. Restore the checked-in lockfile before verifying.'
    }

    $nodeVersion = (& node --version)
    if ($LASTEXITCODE -ne 0) { throw "Cannot determine Node.js version (exit code $LASTEXITCODE)." }
    $lockHash = (Get-FileHash -Path $lockfile -Algorithm SHA256).Hash
    $expectedStamp = "$lockHash|$nodeVersion"
    $installedStamp = if (Test-Path $stampPath) { (Get-Content $stampPath -Raw).Trim() } else { '' }

    if (-not (Test-Path $modules) -or $installedStamp -ne $expectedStamp) {
        Write-Host '==> Refreshing locked frontend dependencies (npm ci)'
        Write-Host '    Stop the Vite dev server first if Windows reports EPERM on a native .node file.'
        & npm ci
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend dependency installation failed with exit code $LASTEXITCODE. Stop Vite and retry."
        }
        Set-Content -Path $stampPath -Value $expectedStamp -NoNewline
    }
    else {
        Write-Host '==> Frontend dependencies unchanged; skipping npm ci'
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
