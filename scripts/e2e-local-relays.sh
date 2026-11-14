#!/usr/bin/env bash
#
# End-to-end check for the local .NET SDK relays. It needs neither the collector
# nor Docker and never touches the network:
#
#   1. builds the HelloClient sample in Release; the terminal renderer
#      (glitchport-terminal) is copied next to the sample by the project
#   2. captures "hello local" at warning level through a `terminal:` and a
#      `file:` relay configured in GLITCHPORT_DSN and asserts that the terminal
#      rendered the event and that both relays report failed=0 dropped=0
#   3. asserts that the file relay wrote the event to a GLITCHPORT_DSN file
#      (<temp>/glitchport-*.ndjson)
#   4. starts the sample again with only a terminal relay and a 60 s hold,
#      force-kills only the parent process and asserts that the terminal child
#      disappears within 5 s (Windows job object / stdin close)
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-local-relays.sh
#
# Requires: bash and the .NET 10 SDK. Windows Git Bash and Linux are supported.
# The script is non-interactive, always stops the processes it started and
# removes its temporary directory, and exits non-zero with a diagnostic as soon
# as a step fails.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

sample_project="sdk/dotnet/samples/HelloClient/HelloClient.csproj"
sample_dll="sdk/dotnet/samples/HelloClient/bin/Release/net10.0/HelloClient.dll"
message="hello local"

temp_dir=""
parent_pid=""
terminal_pid=""

is_windows=0
case "$(uname -s 2>/dev/null || echo unknown)" in
    MINGW* | MSYS* | CYGWIN*) is_windows=1 ;;
esac

fail() {
    echo "e2e-local-relays: $*" >&2
    exit 1
}

# Maps an MSYS/Cygwin pid to the native Windows pid, the same way
# scripts/e2e-hello.sh does. On Linux the pid is returned unchanged.
native_pid() {
    local pid="$1"

    if [ -r "/proc/$pid/winpid" ]; then
        cat "/proc/$pid/winpid" 2>/dev/null || printf '%s\n' "$pid"
    else
        printf '%s\n' "$pid"
    fi
}

# Returns success while the given native pid is visible to the operating
# system. Windows uses tasklist because kill -0 does not work for pids that were
# not started by the shell; Linux uses kill -0 as usual.
process_alive() {
    local pid="$1"

    [ -n "$pid" ] || return 1

    if [ "$is_windows" -eq 1 ]; then
        tasklist //FI "PID eq $pid" //NH 2>/dev/null | grep -Eq "(^|[^0-9])$pid([^0-9]|$)"
    else
        kill -0 "$pid" >/dev/null 2>&1
    fi
}

# Stops a process started by this script (an MSYS pid on Windows).
stop_background_process() {
    local pid="$1"

    [ -n "$pid" ] || return 0
    kill -0 "$pid" >/dev/null 2>&1 || return 0

    if [ "$is_windows" -eq 1 ]; then
        taskkill //F //PID "$(native_pid "$pid")" >/dev/null 2>&1 || true
    else
        kill -KILL "$pid" >/dev/null 2>&1 || true
    fi
}

# Stops the terminal child, whose pid is already a native pid (it is the value
# reported by the .NET process).
stop_native_process() {
    local pid="$1"

    [ -n "$pid" ] || return 0
    process_alive "$pid" || return 0

    if [ "$is_windows" -eq 1 ]; then
        taskkill //F //PID "$pid" >/dev/null 2>&1 || true
    else
        kill -KILL "$pid" >/dev/null 2>&1 || true
    fi
}

cleanup() {
    local status=$?
    trap - EXIT

    stop_background_process "$parent_pid"
    if [ -n "$parent_pid" ]; then
        wait "$parent_pid" 2>/dev/null || true
    fi
    stop_native_process "$terminal_pid"

    if [ -n "$temp_dir" ] && [ -d "$temp_dir" ]; then
        rm -rf "$temp_dir" || true
    fi

    if [ "$status" -ne 0 ]; then
        echo "e2e-local-relays: FAILED (exit $status)" >&2
    fi
}

trap cleanup EXIT

# Also unwind on Ctrl+C and on CI cancellation: the EXIT trap performs the
# actual teardown.
trap 'exit 130' INT
trap 'exit 143' TERM

command -v dotnet >/dev/null 2>&1 \
    || fail "dotnet was not found on PATH; install the .NET 10 SDK"

echo "e2e-local-relays: dotnet build -c Release $sample_project"
dotnet build -c Release "$sample_project"
[ -f "$sample_dll" ] || fail "the build did not produce $sample_dll"

temp_dir="$(mktemp -d)" || fail "could not create a temporary directory"
run_log="$temp_dir/hello.log"

# The file relay resolves a relative path against the .NET current directory,
# so pass an absolute path. Git Bash exposes /tmp as a Windows path that .NET
# does not understand; cygpath -m produces the C:/... form.
file_dir="$temp_dir"
if [ "$is_windows" -eq 1 ] && command -v cygpath >/dev/null 2>&1; then
    file_dir="$(cygpath -m "$temp_dir")"
fi

echo "e2e-local-relays: capturing '$message' through terminal and file relays"
if ! GLITCHPORT_DSN="terminal:?window=inherit&format=compact;file:?dir=$file_dir&format=ndjson" \
    dotnet "$sample_dll" "$message" --level warning >"$run_log" 2>&1; then
    echo "e2e-local-relays: HelloClient output:" >&2
    cat "$run_log" >&2 2>/dev/null || true
    fail "HelloClient exited non-zero for the terminal+file relay configuration"
fi

# The compact renderer prints "<time> WARNING hello local  (id ...)".
if ! awk -v message="$message" \
    'index($0, "WARN") > 0 && index($0, message) > 0 { found = 1 } END { exit found ? 0 : 1 }' \
    "$run_log"; then
    echo "e2e-local-relays: HelloClient output:" >&2
    cat "$run_log" >&2 || true
    fail "the terminal relay did not render a line with WARN and '$message'"
fi

if ! awk '/relay terminal/ && /failed=0/ && /dropped=0/ { found = 1 } END { exit found ? 0 : 1 }' "$run_log"; then
    echo "e2e-local-relays: HelloClient output:" >&2
    cat "$run_log" >&2 || true
    fail "the terminal relay did not report failed=0 dropped=0"
fi

if ! awk '/relay file/ && /failed=0/ && /dropped=0/ { found = 1 } END { exit found ? 0 : 1 }' "$run_log"; then
    echo "e2e-local-relays: HelloClient output:" >&2
    cat "$run_log" >&2 || true
    fail "the file relay did not report failed=0 dropped=0"
fi

shopt -s nullglob
ndjson_files=("$temp_dir"/glitchport-*.ndjson)
shopt -u nullglob

if [ "${#ndjson_files[@]}" -eq 0 ]; then
    ls -la "$temp_dir" >&2 2>/dev/null || true
    fail "the file relay did not create $temp_dir/glitchport-*.ndjson"
fi

if ! awk -v message="$message" \
    'index($0, "\"message\":\"" message "\"") > 0 && index($0, "\"level\":\"warning\"") > 0 { found = 1 } END { exit found ? 0 : 1 }' \
    "${ndjson_files[@]}"; then
    echo "e2e-local-relays: NDJSON file content:" >&2
    cat "${ndjson_files[@]}" >&2 || true
    fail "the NDJSON file does not contain the captured warning event"
fi

echo "e2e-local-relays: terminal output and NDJSON file verified"

# Parent-death check: the renderer must not outlive the process it renders for.
echo "e2e-local-relays: checking that the terminal child dies with its parent"
hold_log="$temp_dir/parent-death.log"

GLITCHPORT_DSN="terminal:?window=inherit&format=compact" \
    dotnet "$sample_dll" "$message" --level warning --hold 60 >"$hold_log" 2>&1 &
parent_pid=$!

deadline=$((SECONDS + 20))
while :; do
    terminal_pid="$(grep -o 'terminal_pid: [0-9][0-9]*' "$hold_log" 2>/dev/null \
        | head -n 1 \
        | tr -cd '0-9')" || true

    if [ -n "$terminal_pid" ]; then
        break
    fi

    if ! kill -0 "$parent_pid" >/dev/null 2>&1; then
        echo "e2e-local-relays: HelloClient output:" >&2
        cat "$hold_log" >&2 2>/dev/null || true
        fail "the sample exited before printing terminal_pid"
    fi

    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "e2e-local-relays: HelloClient output:" >&2
        cat "$hold_log" >&2 2>/dev/null || true
        fail "terminal_pid did not appear within 20 s"
    fi

    sleep 0.2
done

if ! process_alive "$terminal_pid"; then
    fail "the terminal child $terminal_pid is not running before the parent is killed"
fi

if ! kill -0 "$parent_pid" >/dev/null 2>&1; then
    echo "e2e-local-relays: HelloClient output:" >&2
    cat "$hold_log" >&2 2>/dev/null || true
    fail "the sample exited before the parent-death check"
fi

echo "e2e-local-relays: killing only the parent process; terminal child pid $terminal_pid"
if [ "$is_windows" -eq 1 ]; then
    parent_win_pid="$(native_pid "$parent_pid")"
    if ! taskkill //F //PID "$parent_win_pid" >/dev/null 2>&1; then
        fail "taskkill could not terminate the parent process $parent_win_pid"
    fi
else
    if ! kill -KILL "$parent_pid" 2>/dev/null; then
        fail "kill -9 could not terminate the parent process $parent_pid"
    fi
fi

deadline=$((SECONDS + 5))
while process_alive "$terminal_pid"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        fail "the terminal child $terminal_pid was still running 5 s after the parent was killed"
    fi

    sleep 0.1
done

wait "$parent_pid" 2>/dev/null || true
parent_pid=""

echo "e2e-local-relays: terminal child exited after the parent was killed"
echo "e2e-local-relays: ok"
