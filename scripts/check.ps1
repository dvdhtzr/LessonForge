# LessonForge local verification gate. Run before merging a pull request.
# Each child script throws on any failed dotnet/npm command; no false "all green".
$ErrorActionPreference = 'Stop'

Write-Host '==> LessonForge local verification'
& (Join-Path $PSScriptRoot 'build.ps1')
& (Join-Path $PSScriptRoot 'test.ps1')
& (Join-Path $PSScriptRoot 'web.ps1')

Write-Host ''
Write-Host '==> All local checks passed.'
