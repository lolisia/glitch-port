#!/usr/bin/env bash
#
# Verify the dashboard (React + Vite + Vitest).
#
#   1. npm ci     when node_modules is missing or package-lock.json changed
#   2. npm test   vitest run
#
# The dashboard build is already part of scripts/verify.sh; this hook only adds
# the component test suite so it runs for every verification.
#
# Non-interactive; Node 22 and npm are required.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root/dashboard"

lock_hash="$(sha256sum package-lock.json | awk '{print $1}')"
hash_file="node_modules/.gp-lock-hash"

if [ ! -d node_modules ] ||
  [ ! -f "$hash_file" ] ||
  [ "$(cat "$hash_file")" != "$lock_hash" ]; then
  echo "verify: npm ci (dashboard)"
  npm ci
  printf '%s' "$lock_hash" >"$hash_file"
fi

echo "verify: npm test (dashboard)"
npm test
