#!/usr/bin/env bash
# Restore and build the .NET solution (Release).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# Build the .NET projects explicitly: the solution also contains web/web.esproj
# (the front end), which needs Node. Use scripts/web.sh for the front end.
echo "==> dotnet build (Release)"
dotnet build LessonForge.Api/LessonForge.Api.csproj --configuration Release
dotnet build LessonForge.Tests/LessonForge.Tests.csproj --configuration Release
