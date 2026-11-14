#!/usr/bin/env bash
#
# Verify the Supabase Edge Function (Deno) TypeScript SDK in sdk/supabase.
#
#   1. npm ci            only when node_modules is missing (fresh worktree)
#   2. npm run typecheck tsc --noEmit over src/, tests/ and examples/
#   3. npm test          vitest run
#
# Node 22 + npm are test tooling only; the SDK runtime targets Deno/Supabase.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root/sdk/supabase"

if [ ! -d node_modules ]; then
  echo "verify: npm ci (sdk/supabase)"
  npm ci
fi

echo "verify: npm run typecheck (sdk/supabase)"
npm run typecheck

echo "verify: npm test (sdk/supabase)"
npm test
