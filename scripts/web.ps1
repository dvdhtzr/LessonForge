# Install web dependencies and type-check + build the frontend.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location (Join-Path $root 'web')
if (Test-Path 'package-lock.json') {
  Write-Host '==> npm ci'
  npm ci
} else {
  Write-Host '==> npm install'
  npm install
}
Write-Host '==> npm run build'
npm run build
