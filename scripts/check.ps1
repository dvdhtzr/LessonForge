# Full local validation: .NET build + tests, then the web build. Run this before pushing.
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/build.ps1"
& "$PSScriptRoot/test.ps1"
& "$PSScriptRoot/web.ps1"
Write-Host ''
Write-Host '==> All checks passed.'
