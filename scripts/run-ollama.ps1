# Started only when no Ollama service is available on localhost:11434.
# Leave this terminal open while using LessonForge; Ctrl+C stops this Ollama server.
$ErrorActionPreference = 'Stop'
Write-Host '==> Ollama local server (http://127.0.0.1:11434)'
& ollama serve
if ($LASTEXITCODE -ne 0) {
    throw "Ollama server exited with code $LASTEXITCODE. Check whether another Ollama instance is already running."
}
