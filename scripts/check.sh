#!/usr/bin/env bash
# Full local validation: .NET build + tests, then the web build. Run this before pushing.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
"$ROOT/scripts/build.sh"
"$ROOT/scripts/test.sh"
"$ROOT/scripts/web.sh"
echo ""
echo "==> All checks passed."
