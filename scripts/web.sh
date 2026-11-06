#!/usr/bin/env bash
# Verify the frontend; reinstall only when the lockfile or Node version changes.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/web"

if [ ! -f package-lock.json ]; then
  echo "package-lock.json is missing; restore the checked-in lockfile." >&2
  exit 1
fi

lock_hash="$(node -p "require('crypto').createHash('sha256').update(require('fs').readFileSync('package-lock.json')).digest('hex')")"
node_version="$(node --version)"
stamp="$lock_hash|$node_version"
stamp_file="node_modules/.lessonforge-install-stamp"
installed_stamp=""
if [ -f "$stamp_file" ]; then
  installed_stamp="$(cat "$stamp_file")"
fi

if [ ! -d node_modules ] || [ "$installed_stamp" != "$stamp" ]; then
  echo "==> npm ci (dependencies missing or lockfile/Node version changed)"
  npm ci
  printf '%s' "$stamp" > "$stamp_file"
else
  echo "==> Frontend dependencies unchanged; skipping npm ci"
fi

echo "==> npm run lint"
npm run lint
echo "==> npm run build"
npm run build
