#!/usr/bin/env bash
#
# Verify the dashboard (React + Vite + Vitest).
#
#   1. npm ci     only when node_modules is missing (fresh worktree)
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

if [ ! -d node_modules ]; then
  echo "verify: npm ci (dashboard)"
  npm ci
fi

echo "verify: npm test (dashboard)"
npm test
