# Verification hooks

`scripts/verify.d/` contains small, component-specific verification steps that
run as part of `scripts/verify.sh`. Keeping them here lets a component add its
own checks without editing the repository-wide entry point (and without
conflicting with other components doing the same).

## Contract

- **Discovery.** `scripts/verify.sh` runs every file matching
  `scripts/verify.d/*.sh` after all built-in stages (build, tests, dashboard,
  Unity). Files are executed in **name order** (byte-wise glob order), so
  prefix names with a number when order matters, for example
  `10-protocol-json.sh`.
- **Invocation.** Each hook is run with `bash <path>` from the repository root.
  Hooks must not assume the caller's current directory or any command-line
  arguments.
- **Empty directory.** When `scripts/verify.d/` does not exist or contains no
  `*.sh` files, the stage prints `verify: no scripts/verify.d/*.sh hooks
  (skipped)` and succeeds.
- **Exit status.** A hook must exit `0` on success. Any non-zero exit fails
  `scripts/verify.sh` immediately (`set -e`); later hooks are not run. The
  first failure therefore determines the exit status of the whole run.
- **No side effects.** Hooks must be non-interactive and deterministic, must
  not require Docker, a network connection, fixed ports or long-running
  servers, and must not modify tracked files or git state. Write temporary
  output under `.artifact/` and clean it up when practical.
- **Output.** Print a short line starting with the hook's component name and
  write diagnostics to stderr. `scripts/verify.sh` already echoes
  `verify: bash <path>` before invoking the hook.

## Writing a hook

```bash
#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

# ... component checks ...

echo "my-component: ok"
```

Mark the file executable (`chmod +x`) for convenience; `verify.sh` invokes it
through `bash` regardless.

## Current hooks

| Hook | Purpose |
| --- | --- |
| `protocol-json.sh` | Parses every `*.json` file under `sdk/protocol/` with Node.js (schema and examples). |
