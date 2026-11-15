#!/usr/bin/env bash
#
# Sequential end-to-end test runner.
#
#   1. discovers every scripts/e2e-*.sh file (one level deep, no recursion) and
#      orders them with LC_ALL=C sort; the runner itself is excluded so it
#      never recurses into itself
#   2. runs each script with `bash <script>` from the repository root, one
#      after another, while showing its combined stdout/stderr and teeing it to
#      .artifact/e2e-all/<script name>.log
#   3. keeps going after a failing script so the whole suite always runs
#   4. prints an aligned summary table with Script, Result and Duration; a
#      non-zero exit is a FAIL, exit 0 with the literal text "(skipped)" in the
#      output is a SKIP, and exit 0 otherwise is a PASS (SKIP is not a failure)
#   5. prints the log path and the last lines for every FAIL and exits 1 when
#      at least one script failed; otherwise prints "e2e-all: ok" and exits 0
#
# The end-to-end scripts share fixed development ports (5080 Relay, 5090/5091/
# 5092 Processors, 5173 dashboard, 55432 PostgreSQL, ...) and Docker Compose
# projects, so they must not run concurrently: this runner is deliberately
# sequential and should be the only e2e suite running on the machine.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-all.sh            # run every e2e script sequentially
#   bash scripts/e2e-all.sh --list     # print the scripts that would run
#
# Requires: bash, Docker with Compose v2, the .NET 10 SDK and Node.js 22. The
# Unity scripts additionally need a locally installed Unity editor; they exit 0
# with "(skipped)" when it is missing, which the runner reports as SKIP. A
# Docker-based script that fails because Docker is unavailable is a FAIL - it
# is never pre-skipped here.
#
# The runner uses only bash and standard coreutils (find, sort, grep, tee,
# tail, sed, basename); no jq or other optional tools are required. All logs
# are overwritten on every run and stay below .artifact/e2e-all/, which is
# git-ignored.
#
# Windows Git Bash compatible.

# -e is deliberately omitted: one failing script must not stop the run. Errors
# are handled explicitly and the exit code is computed from the results.
set -uo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root" || {
    echo "e2e-all: could not change to the repository root $repo_root" >&2
    exit 1
}

# Repository-relative path of this runner; it must never be discovered as a
# script to run.
self="scripts/e2e-all.sh"
log_dir=".artifact/e2e-all"

usage() {
    cat >&2 <<'EOF'
usage: bash scripts/e2e-all.sh [--list]

Runs every scripts/e2e-*.sh end-to-end script sequentially, from the
repository root, and writes each combined output to
.artifact/e2e-all/<script name>.log.

  --list   print the scripts that would run, one per line, and exit
EOF
}

# Prints the repo-relative e2e scripts in LC_ALL=C order, excluding this
# runner. The list is discovered, not hard-coded.
list_scripts() {
    local script

    if [ ! -d scripts ]; then
        return 0
    fi

    while IFS= read -r script; do
        [ -n "$script" ] || continue
        [ "$script" = "$self" ] && continue
        printf '%s\n' "$script"
    done < <(find scripts -maxdepth 1 -type f -name 'e2e-*.sh' -print | LC_ALL=C sort)
}

# Formats an integer number of seconds as "1m 05s".
format_duration() {
    local total="$1"

    printf '%dm %02ds' "$((total / 60))" "$((total % 60))"
}

list_only=0
if [ "$#" -gt 0 ]; then
    if [ "$#" -eq 1 ] && [ "$1" = "--list" ]; then
        list_only=1
    else
        usage
        exit 2
    fi
fi

scripts=()
mapfile -t scripts < <(list_scripts)

if [ "${#scripts[@]}" -eq 0 ]; then
    echo "e2e-all: no scripts/e2e-*.sh scripts were found" >&2
    exit 1
fi

if [ "$list_only" -eq 1 ]; then
    printf '%s\n' "${scripts[@]}"
    exit 0
fi

mkdir -p "$log_dir" || {
    echo "e2e-all: could not create the log directory $log_dir" >&2
    exit 1
}

names=()
results=()
durations=()
logs=()

for script in "${scripts[@]}"; do
    name="$(basename -- "$script")"
    log_file="$log_dir/$name.log"

    echo "e2e-all: running $script (log: $log_file)"

    # SECONDS is an integer bash counter; reset it to measure this script only.
    SECONDS=0
    bash "$script" 2>&1 | tee "$log_file"
    status=$?
    elapsed=$SECONDS

    if [ "$status" -ne 0 ]; then
        result="FAIL"
    elif grep -Fq -- '(skipped)' "$log_file"; then
        result="SKIP"
    else
        result="PASS"
    fi

    names+=("$script")
    results+=("$result")
    durations+=("$(format_duration "$elapsed")")
    logs+=("$log_file")

    echo "e2e-all: $script -> $result ($(format_duration "$elapsed"))"
    echo
done

# Aligned summary table. The Script column is as wide as its longest value.
script_width="${#names[0]}"
for script in "${names[@]}"; do
    if [ "${#script}" -gt "$script_width" ]; then
        script_width="${#script}"
    fi
done
if [ "$script_width" -lt 6 ]; then
    script_width=6
fi
separator="$(printf '%*s' "$script_width" '' | tr ' ' '-')"

echo "e2e-all: summary"
printf '%-*s  %-6s  %s\n' "$script_width" "Script" "Result" "Duration"
printf '%-*s  %-6s  %s\n' "$script_width" "$separator" "------" "--------"
for index in "${!names[@]}"; do
    printf '%-*s  %-6s  %s\n' "$script_width" "${names[$index]}" \
        "${results[$index]}" "${durations[$index]}"
done

failed=0
for index in "${!names[@]}"; do
    if [ "${results[$index]}" = "FAIL" ]; then
        failed=$((failed + 1))
        echo "e2e-all: ${names[$index]} FAIL; log: ${logs[$index]}" >&2
        if [ -s "${logs[$index]}" ]; then
            echo "e2e-all: last 20 lines of ${logs[$index]}:" >&2
            tail -n 20 "${logs[$index]}" 2>/dev/null | sed 's/^/    /' >&2 || true
        fi
    fi
done

if [ "$failed" -ne 0 ]; then
    echo "e2e-all: $failed of ${#names[@]} scripts failed" >&2
    exit 1
fi

echo "e2e-all: ok"
exit 0
