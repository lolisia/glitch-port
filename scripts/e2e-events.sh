#!/usr/bin/env bash
#
# End-to-end check for the structured events pipeline:
#
#   1. starts PostgreSQL (Docker Compose, host port 55432) and waits for it
#   2. builds the Processor and the Relay in Release
#   3. starts the Processor (5090) and the Relay (5080) in the background with
#      ASPNETCORE_ENVIRONMENT=Development, writing their output to
#      .artifact/e2e/e2e-events-*.log
#   4. waits for both /healthz endpoints
#   5. posts sdk/protocol/examples/exception-unity.json twice with different
#      event ids and polls the Management API until exactly one issue matches
#      the example's exception and has event_count 2
#   6. marks that issue resolved and posts a third copy, then polls until the
#      same issue is unresolved again with event_count 3
#   7. posts a legacy message to POST /api/1/messages and asserts that
#      GET /api/manage/messages lists it with the endpoint POST /api/1/messages,
#      proving the events table and the legacy messages path share one list
#
# The request bodies are prepared with sed and the management responses are
# read with POSIX text tools, so the script does not require jq.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-events.sh
#
# Requires: bash, curl, Docker with Compose v2 and the .NET 10 SDK.
# The script always tears the stack down again (processes and containers) and
# exits non-zero as soon as a step fails. It uses the fixed development ports
# 5080, 5090 and 55432, so it must not run concurrently with e2e-hello.sh.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e"
processor_log="$log_dir/e2e-events-processor.log"
relay_log="$log_dir/e2e-events-relay.log"
work_dir="$log_dir/e2e-events"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"

project_id="1"
public_key="dev-public-key"
admin_token="dev-admin-token"

events_url="$relay_endpoint/api/$project_id/events"
legacy_messages_url="$relay_endpoint/api/$project_id/messages"
issues_url="$processor_endpoint/api/manage/projects/$project_id/issues?status=all&limit=100"
messages_url="$processor_endpoint/api/manage/messages?limit=100"

example_path="sdk/protocol/examples/exception-unity.json"
issue_title_marker="NullReferenceException"
legacy_message="e2e-events legacy message from the old messages endpoint"

processor_pid=""
relay_pid=""
issue_id=""

mkdir -p "$log_dir" "$work_dir"

[ -f "$example_path" ] || {
    echo "e2e-events: $example_path not found" >&2
    exit 1
}

fail() {
    echo "e2e-events: $*" >&2
    exit 1
}

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
        echo "e2e-events: FAILED (exit $status)" >&2
        echo "e2e-events: processor log: $processor_log" >&2
        echo "e2e-events: relay log: $relay_log" >&2

        local log_file
        for log_file in "$processor_log" "$relay_log"; do
            if [ -f "$log_file" ]; then
                echo "e2e-events: last 40 lines of $log_file:" >&2
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

echo "e2e-events: starting postgres"
if ! docker compose up -d --wait postgres; then
    echo "e2e-events: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-events: postgres did not become ready within 60s" >&2
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
    echo "e2e-events: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

echo "e2e-events: starting processor (log: $processor_log)"
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-events: starting relay (log: $relay_log)"
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
            echo "e2e-events: $name exited before answering $url" >&2
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-events: $name did not answer $url within 60s" >&2
            return 1
        fi

        sleep 1
    done

    echo "e2e-events: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"

# Return a fresh 32-character hexadecimal event id. Four $RANDOM values keep
# repeated runs from being deduplicated when a previous teardown left data
# behind; no external tool is needed.
new_event_id() {
    printf '%08x%08x%08x%08x' "$RANDOM" "$RANDOM" "$RANDOM" "$RANDOM"
}

# Copy the Unity protocol example to <destination>, replacing its event_id.
# sed is used instead of jq so the script only needs the usual Git Bash tools.
write_event_body() {
    local event_id="$1"
    local destination="$2"

    sed -E \
        's/("event_id"[[:space:]]*:[[:space:]]*")[^"]*(")/\1'"$event_id"'\2/' \
        "$example_path" >"$destination"

    if ! grep -Fq "\"event_id\": \"$event_id\"" "$destination"; then
        fail "could not rewrite event_id to $event_id in $destination"
    fi
}

# POST one ingest body and require the Relay's 202 Accepted response.
post_ingest() {
    local description="$1"
    local url="$2"
    local body_file="$3"
    local response_file="$work_dir/post-response.json"
    local http_code

    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X POST \
        -H "X-GlitchPort-Key: $public_key" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$url" 2>/dev/null || true)"

    if [ "$http_code" != "202" ]; then
        echo "e2e-events: POST $url failed for $description (HTTP ${http_code:-<no response>})" >&2
        cat "$response_file" >&2 2>/dev/null || true
        echo >&2
        fail "$description was not accepted by the Relay"
    fi

    echo "e2e-events: posted $description"
}

# Extract a string field from one compact JSON object. The Processor writes
# snake_case JSON without whitespace, but the patterns also accept pretty output.
json_string_field() {
    local json="$1"
    local field="$2"

    sed -n -E "s/.*\"$field\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" <<<"$json"
}

# Extract a numeric field from one compact JSON object.
json_number_field() {
    local json="$1"
    local field="$2"

    sed -n -E "s/.*\"$field\"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p" <<<"$json"
}

# Split the issue list into one line per object and keep the lines whose title
# contains the example's exception. Labels are nested objects, but the title,
# id and event_count of an issue all appear before the labels array, so a split
# never separates the fields this script reads.
matching_issue_lines() {
    sed 's/},{/}\n{/g' <<<"$1" | grep -F -- "$issue_title_marker" || true
}

# Poll the Management API until exactly one issue matches the example and has
# the expected status and event_count. When expected_issue_id is set, the issue
# id must also stay the same, proving the regression reopened the existing issue
# instead of creating a new one.
wait_for_issue() {
    local expected_status="$1"
    local expected_count="$2"
    local expected_issue_id="$3"
    local description="$4"
    local deadline=$((SECONDS + 30))
    local response=""
    local matching=""
    local matches=0
    local id=""
    local count=""
    local status=""

    echo "e2e-events: waiting for $description"

    while :; do
        response="$(curl -sf -H "Authorization: Bearer $admin_token" "$issues_url" 2>/dev/null || true)"
        matching="$(matching_issue_lines "$response")"

        matches=0
        if [ -n "$matching" ]; then
            matches="$(printf '%s\n' "$matching" | wc -l | tr -d '[:space:]')"
        fi

        if [ "$matches" -eq 1 ]; then
            id="$(json_number_field "$matching" "id")"
            count="$(json_number_field "$matching" "event_count")"
            status="$(json_string_field "$matching" "status")"

            if [ -n "$id" ] \
                && [ "$count" = "$expected_count" ] \
                && [ "$status" = "$expected_status" ] \
                && { [ -z "$expected_issue_id" ] || [ "$id" = "$expected_issue_id" ]; }; then
                issue_id="$id"
                echo "e2e-events: issue $id is $status with event_count $count"
                return 0
            fi
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-events: GET $issues_url returned:" >&2
            printf '%s\n' "${response:-<no response>}" >&2
            echo "e2e-events: matching issue lines:" >&2
            printf '%s\n' "${matching:-<none>}" >&2
            fail "expected $description within 30s"
        fi

        sleep 1
    done
}

# Change the issue status through the Management API and assert the response.
patch_issue_status() {
    local id="$1"
    local status="$2"
    local body_file="$work_dir/patch-status.json"
    local response_file="$work_dir/patch-response.json"
    local http_code=""
    local response=""

    printf '{"status":"%s"}\n' "$status" >"$body_file"

    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X PATCH \
        -H "Authorization: Bearer $admin_token" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$processor_endpoint/api/manage/issues/$id" 2>/dev/null || true)"

    response="$(cat "$response_file" 2>/dev/null || true)"

    if [ "$http_code" != "200" ]; then
        echo "e2e-events: PATCH /api/manage/issues/$id failed (HTTP ${http_code:-<no response>})" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        fail "could not mark issue $id as $status"
    fi

    if [ "$(json_string_field "$response" "status")" != "$status" ]; then
        echo "e2e-events: PATCH /api/manage/issues/$id returned:" >&2
        printf '%s\n' "$response" >&2
        fail "issue $id was not marked as $status"
    fi

    echo "e2e-events: issue $id is $status"
}

# Poll the unified message list until the legacy post is visible together with
# its endpoint. Both fields must be on the same object, not just somewhere in
# the response.
wait_for_legacy_message() {
    local deadline=$((SECONDS + 30))
    local response=""

    echo "e2e-events: waiting for the legacy message in GET /api/manage/messages"

    while :; do
        response="$(curl -sf -H "Authorization: Bearer $admin_token" "$messages_url" 2>/dev/null || true)"

        if sed 's/},{/}\n{/g' <<<"$response" \
            | awk -v message="$legacy_message" \
                -v endpoint="\"endpoint\":\"POST /api/$project_id/messages\"" \
                'index($0, message) > 0 && index($0, endpoint) > 0 { found = 1 }
                 END { exit found ? 0 : 1 }'; then
            echo "e2e-events: management API lists the legacy message with endpoint POST /api/$project_id/messages"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-events: GET $messages_url returned:" >&2
            printf '%s\n' "${response:-<no response>}" >&2
            fail "the legacy message did not reach GET /api/manage/messages within 30s"
        fi

        sleep 1
    done
}

event_id_1="$(new_event_id)"
event_id_2="$(new_event_id)"
event_id_3="$(new_event_id)"

for event_id in "$event_id_1" "$event_id_2" "$event_id_3"; do
    if [[ ! "$event_id" =~ ^[0-9a-f]{32}$ ]]; then
        fail "generated an invalid event id: $event_id"
    fi
done

if [ "$event_id_1" = "$event_id_2" ] \
    || [ "$event_id_1" = "$event_id_3" ] \
    || [ "$event_id_2" = "$event_id_3" ]; then
    fail "generated duplicate event ids"
fi

write_event_body "$event_id_1" "$work_dir/event-1.json"
write_event_body "$event_id_2" "$work_dir/event-2.json"
write_event_body "$event_id_3" "$work_dir/event-3.json"

# Two distinct events of the same exception must group into one issue.
post_ingest "Unity example event 1" "$events_url" "$work_dir/event-1.json"
post_ingest "Unity example event 2" "$events_url" "$work_dir/event-2.json"

wait_for_issue "unresolved" "2" "" "one issue for the Unity example with event_count 2"

# Resolving the issue and posting another event must reopen the same issue.
patch_issue_status "$issue_id" "resolved"
post_ingest "Unity example event 3" "$events_url" "$work_dir/event-3.json"

wait_for_issue "unresolved" "3" "$issue_id" "issue $issue_id to reopen with event_count 3"

# The legacy message endpoint feeds the same message list as structured events.
printf '{"message":"%s","level":"info","platform":"unity"}\n' "$legacy_message" \
    >"$work_dir/legacy-message.json"
post_ingest "legacy message" "$legacy_messages_url" "$work_dir/legacy-message.json"

wait_for_legacy_message

echo "e2e-events: ok"
