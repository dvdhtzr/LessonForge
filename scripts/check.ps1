# LessonForge local verification gate. Run before merging a pull request.
# Each child script throws on any failed dotnet/npm command; no false "all green".
$ErrorActionPreference = 'Stop'

Write-Host '==> LessonForge local verification'

# Validate the syntax of every Windows startup/setup script. This catches broken
# PowerShell before a clean-clone user needs to execute the one-command launcher.
Write-Host '==> Checking PowerShell script syntax'
foreach ($script in Get-ChildItem -Path $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        $details = ($parseErrors | ForEach-Object { $_.Message }) -join '; '
        throw "PowerShell syntax error in $($script.Name): $details"
    }
}
& (Join-Path $PSScriptRoot 'build.ps1')
& (Join-Path $PSScriptRoot 'test.ps1')
& (Join-Path $PSScriptRoot 'web.ps1')

Write-Host ''
Write-Host '==> All local checks passed.'
