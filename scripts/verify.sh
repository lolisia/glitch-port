#!/usr/bin/env bash
#
# Repository verification entry point.
#
#   1. dotnet build -c Release   every *.csproj under sdk/ and collector/
#      (projects under sdk/unity/ are owned by scripts/build-unity-plugins.sh
#      and scripts/unity-test.sh and are never built here)
#   2. dotnet test  -c Release --no-build   every *.Tests.csproj under the same roots
#   3. npm ci (when needed) + npm run build   in dashboard/, when dashboard/package.json exists
#
# The tree is verified incrementally while the repository is being bootstrapped:
# missing components are skipped instead of failing the run. Any command that
# does run is allowed to fail the whole script (set -e).
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/verify.sh
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Directories that may contain .NET projects. Only existing directories are
# searched so the script works before those components have been created.
dotnet_roots=()
for candidate in sdk collector; do
  if [ -d "$candidate" ]; then
    dotnet_roots+=("$candidate")
  fi
done

# Print every file matching the given glob (for example '*.csproj') below the
# discovered roots, one path per line and sorted for deterministic output.
find_projects() {
  local pattern="$1"

  if [ "${#dotnet_roots[@]}" -eq 0 ]; then
    return 0
  fi

  # Unity batch mode generates *.csproj/*.sln files inside the test project;
  # they must not be picked up by the regular .NET build/test pass.
  find "${dotnet_roots[@]}" -type f -name "$pattern" \
    -not -path 'sdk/unity/*' -not -path '*/sdk/unity/*' | sort
}

build_projects="$(find_projects '*.csproj')"
if [ -z "$build_projects" ]; then
  echo "verify: no dotnet projects (skipped)"
else
  while IFS= read -r project; do
    echo "verify: dotnet build -c Release $project"
    dotnet build -c Release "$project"
  done <<< "$build_projects"
fi

test_projects="$(find_projects '*.Tests.csproj')"
if [ -z "$test_projects" ]; then
  echo "verify: no dotnet test projects (skipped)"
else
  while IFS= read -r project; do
    echo "verify: dotnet test -c Release --no-build $project"
    dotnet test -c Release --no-build "$project"
  done <<< "$test_projects"
fi

if [ -f dashboard/package.json ]; then
  if [ ! -d dashboard/node_modules ]; then
    echo "verify: npm ci (dashboard)"
    (cd dashboard && npm ci)
  fi
  echo "verify: npm run build (dashboard)"
  (cd dashboard && npm run build)
else
  echo "verify: no dashboard/package.json (skipped)"
fi

echo "verify: ok"
