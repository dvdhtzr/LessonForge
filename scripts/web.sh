#!/usr/bin/env bash
# Install web dependencies and type-check + build the frontend.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/web"
if [ -f package-lock.json ]; then
  echo "==> npm ci"
  npm ci
else
  echo "==> npm install"
  npm install
fi
echo "==> npm run build"
npm run build
