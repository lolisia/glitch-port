#!/usr/bin/env bash
#
# Collector-backed end-to-end check for the Unity SDK package
# (sdk/unity/com.glitchport.unity).
#
#   1. starts PostgreSQL (Docker Compose, host port 55432) and waits for it
#   2. builds the SDK core and both collector services (Release)
#   3. starts the Processor (5090) and the Relay (5080) in the background and
#      waits for their /healthz endpoints, failing early when a process exits
#   4. runs scripts/e2e-unity.sh with
#        GLITCHPORT_E2E_HTTP_DSN=http://dev-public-key@localhost:5080/1
#        GLITCHPORT_E2E_ADMIN_URL=http://localhost:5090
#        GLITCHPORT_E2E_ADMIN_TOKEN=dev-admin-token
#      so the real Unity Windows player built by that script delivers its
#      events through the Relay and the Processor stores the issue, while the
#      script's collector stage polls the Management API until the issue shows
#      up. The DSN deliberately has no offline_dir: the events cannot be parked
#      in a queue, so a successful run proves that the player delivered them
#      immediately while the Relay was reachable.
#   5. asserts that the captured run really executed that collector stage (its
#      log contains "collector issue") instead of silently skipping it.
#
# Fixed host ports: 5080 (Relay), 5090 (Processor) and 55432 (PostgreSQL).
# They are the same ports the other collector e2e scripts use, so this script
# must not run concurrently with them (or with any other fixed-port e2e script);
# scripts/e2e-all.sh runs every e2e script sequentially for that reason.
#
# Requires: Windows Git Bash, Docker with Compose v2, the .NET 10 SDK and the
# Unity editor recorded in sdk/unity/TestProject/ProjectSettings/ProjectVersion.txt.
# The editor is resolved from UNITY_PATH when set, otherwise from the Unity Hub
# installation folders (same rules as scripts/e2e-unity.sh and
# scripts/unity-test.sh).
#
# The check only runs on Windows Git Bash; other hosts, and Windows machines
# without the editor, skip with exit code 0 before starting Docker. Every run
# always tears the stack down again (both process trees and the Compose
# PostgreSQL container and volume) through an EXIT trap, also on failure and on
# Ctrl+C/termination.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-unity-http.sh
#
# The logs of a run are written below .artifact/e2e-unity-http/:
#   processor.log  Processor console output
#   relay.log      Relay console output
#   unity.log      combined output of scripts/e2e-unity.sh
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) ;;
    *)
        echo "e2e-unity-http: the collector-backed Unity e2e check requires Windows Git Bash (skipped)"
        exit 0
        ;;
esac

project_path="sdk/unity/TestProject"
project_version_file="$project_path/ProjectSettings/ProjectVersion.txt"

if [ ! -f "$project_version_file" ]; then
    echo "e2e-unity-http: $project_version_file not found" >&2
    exit 1
fi

editor_version="$(sed -n 's/^m_EditorVersion:[[:space:]]*//p' "$project_version_file" | tr -d '\r' | head -n 1)"
if [ -z "$editor_version" ]; then
    echo "e2e-unity-http: no m_EditorVersion in $project_version_file" >&2
    exit 1
fi

# Resolve the editor exactly like scripts/e2e-unity.sh and scripts/unity-test.sh:
# UNITY_PATH first, then the Unity Hub installation folders for the project's
# editor version.
find_unity() {
    local candidates=()
    local candidate

    if [ -n "${UNITY_PATH:-}" ]; then
        candidates+=("$UNITY_PATH")
    fi

    candidates+=(
        "C:/Program Files/Unity/Hub/Editor/$editor_version/Editor/Unity.exe"
        "/Applications/Unity/Hub/Editor/$editor_version/Unity.app/Contents/MacOS/Unity"
        "$HOME/Unity/Hub/Editor/$editor_version/Editor/Unity"
    )

    for candidate in "${candidates[@]}"; do
        if [ -f "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done

    return 1
}

unity="$(find_unity || true)"
if [ -z "$unity" ]; then
    echo "e2e-unity-http: Unity $editor_version not found (skipped)"
    exit 0
fi

log_dir=".artifact/e2e-unity-http"
processor_log="$log_dir/processor.log"
relay_log="$log_dir/relay.log"
unity_log="$log_dir/unity.log"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"

relay_pid=""
processor_pid=""

mkdir -p "$log_dir"

# Stop a background process together with every process it spawned. `dotnet run`
# keeps the application in a child process, so killing only the recorded pid
# would leave the server holding its port. This is the same process-tree
# cleanup scripts/e2e-hello.sh uses.
kill_tree() {
    local pid="${1:-}"
    local win_pid

    [ -n "$pid" ] || return 0

    if command -v taskkill >/dev/null 2>&1; then
        # Git Bash reports MSYS pids in `$!`, but taskkill expects the native
        # Windows pid. /proc/<msys pid>/winpid maps the two; `//F` is passed
        # through as `/F` (avoids MSYS path conversion).
        win_pid="$pid"
        if [ -r "/proc/$pid/winpid" ]; then
            win_pid="$(cat "/proc/$pid/winpid" 2>/dev/null || echo "$pid")"
        fi

        taskkill //F //T //PID "$win_pid" >/dev/null 2>&1 || true
        return 0
    fi

    if command -v pgrep >/dev/null 2>&1; then
        local child
        for child in $(pgrep -P "$pid" 2>/dev/null || true); do
            kill_tree "$child"
        done
    fi

    kill -TERM "$pid" >/dev/null 2>&1 || true

    local attempt
    for attempt in 1 2 3 4 5; do
        kill -0 "$pid" >/dev/null 2>&1 || return 0
        sleep 0.2
    done

    kill -KILL "$pid" >/dev/null 2>&1 || true
}

# Print every log location and the tail of both collector logs. Called from
# cleanup for any non-zero exit, so a `set -e` failure diagnoses as well as an
# explicit fail().
print_failure_diagnostics() {
    echo "e2e-unity-http: processor log: $processor_log" >&2
    echo "e2e-unity-http: relay log: $relay_log" >&2
    echo "e2e-unity-http: unity log: $unity_log" >&2

    if [ -f "$processor_log" ]; then
        echo "e2e-unity-http: last 80 lines of $processor_log:" >&2
        tail -n 80 "$processor_log" >&2 || true
    fi

    if [ -f "$relay_log" ]; then
        echo "e2e-unity-http: last 80 lines of $relay_log:" >&2
        tail -n 80 "$relay_log" >&2 || true
    fi
}

cleanup() {
    local status=$?
    trap - EXIT

    kill_tree "$relay_pid"
    kill_tree "$processor_pid"

    # `-v` also removes the anonymous PostgreSQL data volume, so every run
    # starts from an empty database and re-applies the migrations. Tear the
    # containers down before reaping the jobs so a stuck wait cannot leak them.
    docker compose down -v >/dev/null 2>&1 || true

    if [ -n "$relay_pid" ]; then
        wait "$relay_pid" 2>/dev/null || true
    fi
    if [ -n "$processor_pid" ]; then
        wait "$processor_pid" 2>/dev/null || true
    fi

    if [ "$status" -ne 0 ]; then
        echo "e2e-unity-http: FAILED (exit $status)" >&2
        print_failure_diagnostics
    fi
}

trap cleanup EXIT

# Also unwind on Ctrl+C and on CI cancellation: the EXIT trap performs the
# actual teardown.
trap 'exit 130' INT
trap 'exit 143' TERM

# Report a step failure. The EXIT trap prints the logs and tears the stack down.
fail() {
    echo "e2e-unity-http: $*" >&2
    exit 1
}

echo "e2e-unity-http: using Unity $editor_version ($unity)"
echo "e2e-unity-http: starting postgres"

if ! docker compose up -d --wait postgres; then
    echo "e2e-unity-http: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            fail "postgres did not become ready within 60s"
        fi
        sleep 1
    done
fi

projects=(
    sdk/dotnet/src/GlitchPort.Core/GlitchPort.Core.csproj
    collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj
    collector/src/GlitchPort.Relay/GlitchPort.Relay.csproj
)

for project in "${projects[@]}"; do
    echo "e2e-unity-http: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

echo "e2e-unity-http: starting processor (log: $processor_log)"
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-unity-http: starting relay (log: $relay_log)"
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Relay/GlitchPort.Relay.csproj \
    -c Release --no-build >"$relay_log" 2>&1 &
relay_pid=$!

# Poll a /healthz endpoint for up to 60 seconds, failing early when the
# background process died (for example because its port was already in use).
wait_for_health() {
    local name="$1"
    local url="$2"
    local pid="$3"
    local health_deadline=$((SECONDS + 60))

    until curl -sf "$url" >/dev/null 2>&1; do
        if ! kill -0 "$pid" >/dev/null 2>&1; then
            fail "$name exited before answering $url"
        fi

        if [ "$SECONDS" -ge "$health_deadline" ]; then
            fail "$name did not answer $url within 60s"
        fi

        sleep 1
    done

    echo "e2e-unity-http: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"

# The DSN carries no offline_dir on purpose: an event that the Relay cannot
# accept fails the Unity flush instead of being parked in a queue, so a passing
# run proves immediate delivery. The Management API settings let
# scripts/e2e-unity.sh poll for the collected issue itself.
export GLITCHPORT_E2E_HTTP_DSN="http://dev-public-key@localhost:5080/1"
export GLITCHPORT_E2E_ADMIN_URL="http://localhost:5090"
export GLITCHPORT_E2E_ADMIN_TOKEN="dev-admin-token"

echo "e2e-unity-http: running scripts/e2e-unity.sh against the collector (log: $unity_log)"

# -e is disabled only around the pipeline: a failing e2e run must still be
# reported through this script's own diagnostics and teardown. PIPESTATUS keeps
# the status of e2e-unity.sh instead of tee.
set +e
bash scripts/e2e-unity.sh 2>&1 | tee "$unity_log"
unity_status=${PIPESTATUS[0]}
set -e

if [ "$unity_status" -ne 0 ]; then
    fail "scripts/e2e-unity.sh failed (exit $unity_status)"
fi

if ! grep -Fq "collector issue" "$unity_log"; then
    fail "scripts/e2e-unity.sh exited 0 but its collector stage did not run (no 'collector issue' in $unity_log)"
fi

echo "e2e-unity-http: ok"
