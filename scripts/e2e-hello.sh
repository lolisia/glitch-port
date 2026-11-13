#!/usr/bin/env bash
#
# End-to-end hello check for the glitch-port collector.
#
#   1. starts PostgreSQL (Docker Compose, host port 55432) and waits for it
#   2. builds the SDK core, the HelloClient sample and both collector services
#   3. starts the Processor (5090) and the Relay (5080) in the background,
#      writing their output to .artifact/e2e/*.log
#   4. waits for both /healthz endpoints
#   5. sends the default "hello" message through the .NET SDK sample
#   6. asserts that the message reached the management API with the endpoint
#      recorded by the Relay (POST /api/1/messages)
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-hello.sh
#
# Requires: bash, curl, Docker with Compose v2 and the .NET 10 SDK.
# The script always tears the stack down again (processes and containers) and
# exits non-zero as soon as a step fails.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e"
processor_log="$log_dir/processor.log"
relay_log="$log_dir/relay.log"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"
hello_dsn="http://dev-public-key@localhost:5080/1"
admin_token="dev-admin-token"
project_id="1"

relay_pid=""
processor_pid=""

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
        echo "e2e-hello: FAILED (exit $status)" >&2
        echo "e2e-hello: processor log: $processor_log" >&2
        echo "e2e-hello: relay log: $relay_log" >&2
    fi
}

trap cleanup EXIT

# Also unwind on Ctrl+C and on CI cancellation: the EXIT trap performs the
# actual teardown.
trap 'exit 130' INT
trap 'exit 143' TERM

echo "e2e-hello: starting postgres"
if ! docker compose up -d --wait postgres; then
    echo "e2e-hello: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-hello: postgres did not become ready within 60s" >&2
            exit 1
        fi
        sleep 1
    done
fi

projects=(
    sdk/dotnet/src/GlitchPort.Core/GlitchPort.Core.csproj
    sdk/dotnet/samples/HelloClient/HelloClient.csproj
    collector/src/GlitchPort.Relay/GlitchPort.Relay.csproj
    collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj
)

for project in "${projects[@]}"; do
    echo "e2e-hello: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

echo "e2e-hello: starting processor (log: $processor_log)"
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-hello: starting relay (log: $relay_log)"
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
            echo "e2e-hello: $name exited before answering $url" >&2
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-hello: $name did not answer $url within 60s" >&2
            return 1
        fi

        sleep 1
    done

    echo "e2e-hello: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"

echo "e2e-hello: sending hello through HelloClient"
GLITCHPORT_DSN="$hello_dsn" \
    dotnet run --project sdk/dotnet/samples/HelloClient/HelloClient.csproj -c Release --no-build

# The Relay records `"<method> <path>"` as the endpoint, so a stored hello
# message proves the Relay forwarded the event to the Processor.
messages_url="$processor_endpoint/api/manage/messages?limit=100"
expected_endpoint="POST /api/$project_id/messages"
deadline=$((SECONDS + 10))
response=""

while :; do
    response="$(curl -sf -H "Authorization: Bearer $admin_token" "$messages_url" 2>/dev/null || true)"

    if grep -q 'hello' <<<"$response" && grep -q "$expected_endpoint" <<<"$response"; then
        echo "e2e-hello: management API lists the hello message"
        break
    fi

    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "e2e-hello: the hello message did not reach the management API within 10s" >&2
        echo "e2e-hello: GET $messages_url returned:" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        exit 1
    fi

    sleep 1
done

echo "e2e-hello: ok"
