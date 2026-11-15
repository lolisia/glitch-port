#!/usr/bin/env bash
#
# End-to-end check for the Supabase SDK against the real collector.
#
#   1. starts PostgreSQL (Docker Compose, host port 55432) and waits for it
#   2. builds the Processor and the Relay in Release
#   3. starts the Processor (5090) and the Relay (5080) in the background with
#      ASPNETCORE_ENVIRONMENT=Development, writing their output to
#      .artifact/e2e-supabase/*.log
#   4. waits for both /healthz endpoints
#   5. runs sdk/supabase/examples/node-e2e.ts with Node.js 22 and
#      --experimental-strip-types; the example throws through a withGlitchPort
#      handler, checks the automatic 500 response and verifies the stored event
#      and the generated issue through the Management API
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-supabase.sh
#
# Requires: bash, curl, Docker with Compose v2, the .NET 10 SDK and Node.js 22.
# The SDK source is imported directly, so no npm install or build step is
# needed for the example.
#
# This is an integration-only check: it is not part of scripts/verify.sh. It
# uses the fixed host ports 5080 (Relay), 5090 (Processor) and 55432
# (PostgreSQL), so it must not run concurrently with e2e-hello.sh,
# e2e-events.sh or any other fixed-port end-to-end script.
#
# The script always tears the stack down again (processes and containers) and
# exits non-zero as soon as a step fails. It prints the Processor and Relay logs
# when a step fails.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e-supabase"
processor_log="$log_dir/processor.log"
relay_log="$log_dir/relay.log"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"
manage_url="$processor_endpoint/api/manage"

supabase_dsn="http://dev-public-key@localhost:5080/1"
project_id="1"
admin_token="dev-admin-token"

processor_pid=""
relay_pid=""

mkdir -p "$log_dir"

# Stop a background process together with every process it spawned. `dotnet run`
# keeps the application in a child process, so killing only the recorded pid
# would leave the server holding its port.
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
        echo "e2e-supabase: FAILED (exit $status)" >&2
        echo "e2e-supabase: processor log: $processor_log" >&2
        echo "e2e-supabase: relay log: $relay_log" >&2

        local log_file
        for log_file in "$processor_log" "$relay_log"; do
            if [ -f "$log_file" ]; then
                echo "e2e-supabase: last 40 lines of $log_file:" >&2
                tail -n 40 "$log_file" >&2 || true
            fi
        done
    fi
}

trap cleanup EXIT

# Also unwind on Ctrl+C and on CI cancellation: the EXIT trap performs the
# actual teardown.
trap 'exit 130' INT
trap 'exit 143' TERM

echo "e2e-supabase: starting postgres"
if ! docker compose up -d --wait postgres; then
    echo "e2e-supabase: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-supabase: postgres did not become ready within 60s" >&2
            exit 1
        fi
        sleep 1
    done
fi

projects=(
    collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj
    collector/src/GlitchPort.Relay/GlitchPort.Relay.csproj
)

for project in "${projects[@]}"; do
    echo "e2e-supabase: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

echo "e2e-supabase: starting processor (log: $processor_log)"
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-supabase: starting relay (log: $relay_log)"
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
    local deadline=$((SECONDS + 60))

    until curl -sf "$url" >/dev/null 2>&1; do
        if ! kill -0 "$pid" >/dev/null 2>&1; then
            echo "e2e-supabase: $name exited before answering $url" >&2
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-supabase: $name did not answer $url within 60s" >&2
            return 1
        fi

        sleep 1
    done

    echo "e2e-supabase: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"

command -v node >/dev/null 2>&1 \
    || {
        echo "e2e-supabase: node was not found on PATH; install Node.js 22" >&2
        exit 1
    }

echo "e2e-supabase: running the Node 22 Supabase SDK example"
GLITCHPORT_DSN="$supabase_dsn" \
    GLITCHPORT_PROJECT_ID="$project_id" \
    GLITCHPORT_MANAGE_URL="$manage_url" \
    GLITCHPORT_ADMIN_TOKEN="$admin_token" \
    node --experimental-strip-types sdk/supabase/examples/node-e2e.ts

echo "e2e-supabase: ok"
