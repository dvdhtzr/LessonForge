#!/usr/bin/env bash
# Run the .NET unit tests (offline — no Ollama or network required).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
echo "==> dotnet test"
dotnet test LessonForge.Tests/LessonForge.Tests.csproj --configuration Release
