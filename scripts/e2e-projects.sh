#!/usr/bin/env bash
#
# Integration-only end-to-end check for projects and public keys:
#
#   1. starts PostgreSQL (Docker Compose, host port 55432) and waits for it
#   2. builds the Processor and the Relay in Release
#   3. starts the Processor (port 5090) and the Relay (port 5080) in the
#      background with ASPNETCORE_ENVIRONMENT=Development and
#      Relay__ConfigRefreshSeconds=2, writing their output to
#      .artifact/e2e-projects/*.log
#   4. waits for both /healthz endpoints
#   5. creates the project "e2e projects" (slug e2e-projects) through
#      POST /api/manage/projects and reads the project id and DSN from the
#      201 response with sed; jq is not required
#   6. posts a minimal protocol v1 event to POST /api/<id>/events with the
#      public key taken from the DSN and retries for up to 10 seconds until
#      the Relay answers 202, proving its background config refresh picked up
#      the new key from PostgreSQL
#   7. lists the project keys, revokes the created key with
#      DELETE /api/manage/keys/<keyId> (204) and retries until the Relay
#      answers 401 after its refresh
#   8. creates a key with rate_limit_per_minute=2, waits for its first 202 and
#      asserts that the next accepted request is 202 and the third request is
#      429 with a Retry-After header
#   9. deletes the project (204) and asserts that its issue list now answers
#      404
#  10. prints "e2e-projects: ok"
#
# The fixed development ports 5080 (Relay), 5090 (Processor) and 55432
# (PostgreSQL) make this script integration-only: it must not run concurrently
# with e2e-hello.sh, e2e-events.sh or another e2e-projects run. The script
# always tears the stack down again (processes and containers) and exits
# non-zero as soon as a step fails.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-projects.sh
#
# Requires: bash, curl, sed, Docker with Compose v2 and the .NET 10 SDK.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e-projects"
processor_log="$log_dir/processor.log"
relay_log="$log_dir/relay.log"
work_dir="$log_dir/work"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"

admin_token="dev-admin-token"
project_name="e2e projects"
project_slug="e2e-projects"

processor_pid=""
relay_pid=""
last_event_status=""
last_event_retry_after=""

mkdir -p "$log_dir" "$work_dir"

fail() {
    echo "e2e-projects: $*" >&2
    exit 1
}

# Print a saved response or log file to stderr for failure diagnostics.
print_file() {
    local file="$1"

    if [ -f "$file" ]; then
        cat "$file" >&2 || true
    fi
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
        echo "e2e-projects: FAILED (exit $status)" >&2
        echo "e2e-projects: processor log: $processor_log" >&2
        echo "e2e-projects: relay log: $relay_log" >&2

        local log_file
        for log_file in "$processor_log" "$relay_log"; do
            if [ -f "$log_file" ]; then
                echo "e2e-projects: last 40 lines of $log_file:" >&2
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

echo "e2e-projects: starting postgres"
if ! docker compose up -d --wait postgres; then
    echo "e2e-projects: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-projects: postgres did not become ready within 60s" >&2
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
    echo "e2e-projects: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

echo "e2e-projects: starting processor (log: $processor_log)"
# The Development configuration bootstraps project 1 with dev-public-key and
# sets Processor:PublicRelayUrl, so the DSNs created below point at the local
# Relay. The bootstrap project is never modified by this script.
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-projects: starting relay (log: $relay_log)"
ASPNETCORE_ENVIRONMENT=Development Relay__ConfigRefreshSeconds=2 \
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
            echo "e2e-projects: $name exited before answering $url" >&2
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-projects: $name did not answer $url within 60s" >&2
            return 1
        fi

        sleep 1
    done

    echo "e2e-projects: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"

# Perform one HTTP request and print its status code. The body is written to
# <output_file>; the remaining arguments are passed to curl. A failed curl
# call prints an empty status so the caller can report a clear failure.
http_status() {
    local output_file="$1"
    shift

    curl -sS -o "$output_file" -w '%{http_code}' "$@" 2>/dev/null || true
}

# Extract a string field from one compact JSON object. The Processor writes
# snake_case JSON without whitespace, but the patterns also accept pretty
# output.
json_string_field() {
    local json="$1"
    local field="$2"

    sed -n -E "s/.*\"$field\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" <<<"$json" \
        | sed -n '1p'
}

# Extract a numeric field from one compact JSON object.
json_number_field() {
    local json="$1"
    local field="$2"

    sed -n -E "s/.*\"$field\"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p" <<<"$json" \
        | sed -n '1p'
}

# Print the object of a compact JSON array that contains <public_key>.
key_object_for_public_key() {
    local json="$1"
    local public_key="$2"

    sed 's/},{/}\n{/g' <<<"$json" \
        | grep -F "\"public_key\":\"$public_key\"" \
        | sed -n '1p' || true
}

# Return a fresh 32-character hexadecimal event id. Four $RANDOM values keep
# repeated attempts from being deduplicated by the Processor; no external tool
# is needed.
new_event_id() {
    printf '%08x%08x%08x%08x' "$RANDOM" "$RANDOM" "$RANDOM" "$RANDOM"
}

# POST one minimal protocol v1 event with <key> and store the HTTP status and
# Retry-After header in last_event_status / last_event_retry_after. Every call
# uses a brand-new event id, so a retry is never treated as a duplicate.
post_event() {
    local key="$1"
    local description="$2"
    local event_id
    local body_file="$work_dir/attempt-event.json"
    local response_file="$work_dir/attempt-event-response.json"
    local headers_file="$work_dir/attempt-event-headers.txt"

    event_id="$(new_event_id)"
    printf '{"event_id":"%s","message":"%s","level":"info","platform":"e2e-projects"}\n' \
        "$event_id" "$description" >"$body_file"

    rm -f "$headers_file"
    last_event_status="$(http_status "$response_file" \
        -D "$headers_file" \
        -X POST \
        -H "X-GlitchPort-Key: $key" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$events_url")"

    last_event_retry_after=""
    if [ -f "$headers_file" ]; then
        last_event_retry_after="$(tr -d '\r' <"$headers_file" \
            | sed -n -E 's/^[Rr]etry-[Aa]fter:[[:space:]]*([0-9]+).*/\1/p' \
            | sed -n '1p' || true)"
    fi
}

# Retry POST /api/<id>/events with fresh event ids until the Relay answers the
# expected status (202 once a config refresh picked up a new key, 401 once a
# revocation was refreshed) or the timeout expires.
wait_for_event_status() {
    local key="$1"
    local expected="$2"
    local timeout_seconds="$3"
    local description="$4"
    local deadline=$((SECONDS + timeout_seconds))

    while :; do
        post_event "$key" "$description"

        if [ "$last_event_status" = "$expected" ]; then
            echo "e2e-projects: $description (HTTP $expected)"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-projects: POST $events_url returned HTTP ${last_event_status:-<no response>} while waiting for $expected" >&2
            if [ -f "$work_dir/attempt-event-response.json" ]; then
                print_file "$work_dir/attempt-event-response.json"
                echo >&2
            fi
            fail "$description did not produce HTTP $expected within ${timeout_seconds}s"
        fi

        sleep 1
    done
}

echo "e2e-projects: creating project '$project_name' ($project_slug)"
create_body="$work_dir/create-project.json"
create_response_file="$work_dir/create-project-response.json"
printf '{"name":"%s","slug":"%s"}\n' "$project_name" "$project_slug" >"$create_body"

create_status="$(http_status "$create_response_file" \
    -X POST \
    -H "Authorization: Bearer $admin_token" \
    -H "Content-Type: application/json" \
    --data-binary @"$create_body" \
    "$processor_endpoint/api/manage/projects")"

if [ "$create_status" != "201" ]; then
    echo "e2e-projects: POST /api/manage/projects returned HTTP ${create_status:-<no response>}" >&2
    print_file "$create_response_file"
    echo >&2
    fail "could not create project '$project_slug'"
fi

create_response="$(cat "$create_response_file")"
project_id="$(json_number_field "$create_response" "id")"
dsn="$(json_string_field "$create_response" "dsn")"

if [ -z "$project_id" ]; then
    echo "e2e-projects: POST /api/manage/projects returned:" >&2
    printf '%s\n' "$create_response" >&2
    fail "the created project response did not contain an id"
fi

if [ -z "$dsn" ]; then
    echo "e2e-projects: POST /api/manage/projects returned:" >&2
    printf '%s\n' "$create_response" >&2
    fail "the created project response did not contain a dsn"
fi

# DSNs look like http(s)://<public-key>@<host>[:port]/<project-id>. Deriving
# the key from the DSN keeps the script independent of the key response shape.
public_key="$(sed -n -E 's#^[A-Za-z][A-Za-z0-9+.-]*://([^@/]+)@.*$#\1#p' <<<"$dsn" | sed -n '1p')"
if [ -z "$public_key" ]; then
    fail "could not derive the public key from the project DSN: $dsn"
fi

echo "e2e-projects: project $project_id created with public key $public_key"
echo "e2e-projects: dsn: $dsn"

events_url="$relay_endpoint/api/$project_id/events"
keys_url="$processor_endpoint/api/manage/projects/$project_id/keys"

# A newly created project is not in the Relay's static configuration, so the
# first acceptance proves the Relay fetched the key from the Processor.
wait_for_event_status "$public_key" "202" 10 \
    "relay accepted the new project key after a config refresh"

echo "e2e-projects: listing project keys"
keys_response_file="$work_dir/project-keys.json"
keys_status="$(http_status "$keys_response_file" \
    -H "Authorization: Bearer $admin_token" \
    "$keys_url")"

if [ "$keys_status" != "200" ]; then
    echo "e2e-projects: GET /api/manage/projects/$project_id/keys returned HTTP ${keys_status:-<no response>}" >&2
    print_file "$keys_response_file"
    echo >&2
    fail "could not list the keys of project $project_id"
fi

keys_response="$(cat "$keys_response_file")"
key_object="$(key_object_for_public_key "$keys_response" "$public_key")"
key_id="$(json_number_field "$key_object" "id")"

if [ -z "$key_id" ]; then
    echo "e2e-projects: GET /api/manage/projects/$project_id/keys returned:" >&2
    printf '%s\n' "$keys_response" >&2
    fail "the project key list did not contain the DSN's public key"
fi

echo "e2e-projects: revoking key $key_id"
revoke_response_file="$work_dir/revoke-key-response.json"
revoke_status="$(http_status "$revoke_response_file" \
    -X DELETE \
    -H "Authorization: Bearer $admin_token" \
    "$processor_endpoint/api/manage/keys/$key_id")"

if [ "$revoke_status" != "204" ]; then
    echo "e2e-projects: DELETE /api/manage/keys/$key_id returned HTTP ${revoke_status:-<no response>}" >&2
    print_file "$revoke_response_file"
    echo >&2
    fail "could not revoke key $key_id"
fi

wait_for_event_status "$public_key" "401" 10 \
    "relay rejected the revoked key after a config refresh"

echo "e2e-projects: creating a rate-limited key"
limited_body="$work_dir/create-limited-key.json"
limited_response_file="$work_dir/limited-key-response.json"
printf '{"label":"limited","rate_limit_per_minute":2}\n' >"$limited_body"

limited_status="$(http_status "$limited_response_file" \
    -X POST \
    -H "Authorization: Bearer $admin_token" \
    -H "Content-Type: application/json" \
    --data-binary @"$limited_body" \
    "$keys_url")"

if [ "$limited_status" != "201" ]; then
    echo "e2e-projects: POST /api/manage/projects/$project_id/keys returned HTTP ${limited_status:-<no response>}" >&2
    print_file "$limited_response_file"
    echo >&2
    fail "could not create the rate-limited key"
fi

limited_response="$(cat "$limited_response_file")"
limited_key="$(json_string_field "$limited_response" "public_key")"

if [ -z "$limited_key" ]; then
    echo "e2e-projects: POST /api/manage/projects/$project_id/keys returned:" >&2
    printf '%s\n' "$limited_response" >&2
    fail "the rate-limited key response did not contain a public_key"
fi

# The first accepted request consumes the first of the two tokens. It may take
# one refresh cycle before the Relay knows the key.
wait_for_event_status "$limited_key" "202" 10 \
    "relay accepted the rate-limited key after a config refresh"

echo "e2e-projects: sending the second event with the rate-limited key"
post_event "$limited_key" "second accepted event for the rate-limited key"
if [ "$last_event_status" != "202" ]; then
    echo "e2e-projects: POST $events_url returned HTTP ${last_event_status:-<no response>}" >&2
    print_file "$work_dir/attempt-event-response.json"
    echo >&2
    fail "the second request of the rate-limited key was not accepted"
fi
echo "e2e-projects: second request accepted (HTTP 202)"

echo "e2e-projects: sending the third event with the rate-limited key"
post_event "$limited_key" "third event for the rate-limited key"
if [ "$last_event_status" != "429" ]; then
    echo "e2e-projects: POST $events_url returned HTTP ${last_event_status:-<no response>}" >&2
    print_file "$work_dir/attempt-event-response.json"
    echo >&2
    fail "the third request of the rate-limited key was not rejected with 429"
fi

if [ -z "$last_event_retry_after" ]; then
    echo "e2e-projects: the 429 response headers were:" >&2
    tr -d '\r' <"$work_dir/attempt-event-headers.txt" >&2 || true
    fail "the 429 response did not carry a Retry-After header"
fi

echo "e2e-projects: third request rejected (HTTP 429, Retry-After: $last_event_retry_after)"

echo "e2e-projects: deleting project $project_id"
delete_response_file="$work_dir/delete-project-response.json"
delete_status="$(http_status "$delete_response_file" \
    -X DELETE \
    -H "Authorization: Bearer $admin_token" \
    "$processor_endpoint/api/manage/projects/$project_id")"

if [ "$delete_status" != "204" ]; then
    echo "e2e-projects: DELETE /api/manage/projects/$project_id returned HTTP ${delete_status:-<no response>}" >&2
    print_file "$delete_response_file"
    echo >&2
    fail "could not delete project $project_id"
fi

issues_url="$processor_endpoint/api/manage/projects/$project_id/issues?status=all"
issues_response_file="$work_dir/issues-after-delete.json"
issues_status="$(http_status "$issues_response_file" \
    -H "Authorization: Bearer $admin_token" \
    "$issues_url")"

if [ "$issues_status" != "404" ]; then
    echo "e2e-projects: GET /api/manage/projects/$project_id/issues?status=all returned HTTP ${issues_status:-<no response>}" >&2
    print_file "$issues_response_file"
    echo >&2
    fail "project $project_id still answered its issue list after deletion"
fi

echo "e2e-projects: ok"
