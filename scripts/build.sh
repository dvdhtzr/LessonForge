#!/usr/bin/env bash
# Restore and build the .NET solution (Release).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
echo "==> dotnet build (Release)"
dotnet build LessonForge.slnx --configuration Release
