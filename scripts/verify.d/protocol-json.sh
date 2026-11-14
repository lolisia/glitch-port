#!/usr/bin/env bash
#
# Verify that every JSON document under sdk/protocol/ parses.
#
# The protocol schema and its examples are consumed directly by the Relay and
# by SDK tests, so a syntax error here breaks several components. This hook
# uses Node.js only (no network) and validates every *.json file found below
# sdk/protocol/.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/verify.d/protocol-json.sh
#
# Windows Git Bash, macOS and Linux compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

if ! command -v node >/dev/null 2>&1; then
    echo "protocol-json: node is required but was not found" >&2
    exit 1
fi

if [ ! -d sdk/protocol ]; then
    echo "protocol-json: sdk/protocol not found" >&2
    exit 1
fi

json_files=()
while IFS= read -r file; do
    json_files+=("$file")
done < <(find sdk/protocol -type f -name '*.json' -print | sort)

if [ "${#json_files[@]}" -eq 0 ]; then
    echo "protocol-json: no *.json files under sdk/protocol" >&2
    exit 1
fi

echo "protocol-json: validating ${#json_files[@]} files"

node -e '
const fs = require("node:fs");

const files = process.argv.slice(1);
let failures = 0;

for (const file of files) {
  try {
    JSON.parse(fs.readFileSync(file, "utf8"));
  } catch (error) {
    failures += 1;
    console.error(`protocol-json: ${file}: ${error.message}`);
  }
}

if (failures > 0) {
  process.exit(1);
}

console.log(`protocol-json: ok (${files.length} files)`);
' "${json_files[@]}"
