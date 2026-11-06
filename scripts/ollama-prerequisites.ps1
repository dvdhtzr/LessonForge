# Validate the models declared in LessonForge.Api/appsettings.json.
# Use -InstallModels during initial setup to pull missing models automatically.
param([switch]$InstallModels)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $root 'LessonForge.Api/appsettings.json'

if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
    throw 'Ollama was not found. Install it from https://ollama.com/download/windows, launch the app, then rerun setup.'
}

if (-not (Test-Path $configPath)) {
    throw "Missing committed application settings: $configPath"
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$chatModel = $config.LessonForge.Llm.ChatModel
$embeddingModel = $config.LessonForge.Llm.EmbeddingModel

if ([string]::IsNullOrWhiteSpace($chatModel) -or [string]::IsNullOrWhiteSpace($embeddingModel)) {
    throw 'The ChatModel and EmbeddingModel settings are required in LessonForge.Api/appsettings.json.'
}

# Ollama on Windows normally serves its local API automatically when the app is running.
# Do not call "ollama serve" when that service is already active.
& ollama list *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Cannot connect to Ollama. Launch the Ollama Windows app, then run "ollama list" before retrying.'
}

foreach ($model in @($chatModel, $embeddingModel) | Select-Object -Unique) {
    & ollama show $model *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "==> Ollama model available: $model"
        continue
    }

    if (-not $InstallModels) {
        throw "Required Ollama model is missing: $model. Run .\scripts\setup.ps1 to download it."
    }

    Write-Host "==> Pulling Ollama model: $model (this may download several GB)"
    & ollama pull $model
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to download model $model (exit code $LASTEXITCODE)."
    }
}
