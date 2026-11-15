#!/usr/bin/env bash
#
# Integration-only end-to-end check for the complete local Docker Compose
# stack (profile `full`):
#
#   1. starts PostgreSQL, two Processors, the Relay and the dashboard with
#      `docker compose --profile full up -d --build --wait --wait-timeout 300`
#   2. waits for http://localhost:5080/healthz, :5091/healthz, :5092/healthz
#      and http://localhost:5173/ for up to 60 seconds
#   3. sends 20 protocol v1 events copied from
#      sdk/protocol/examples/exception-unity.json through POST
#      http://localhost:5080/api/1/events with a fresh 32-hex event_id per
#      request, replacing "event_id" with sed (no jq required)
#   4. polls GET http://localhost:5091/api/manage/projects/1/issues?status=all
#      &limit=100 until the NullReferenceException issue reports event_count 20
#      and reads the same page from http://localhost:5092, proving both
#      processors share one database
#   5. asserts that the processor-1 and processor-2 logs each contain a
#      POST /internal/envelopes request, proving the Relay distributed the
#      events across both instances
#   6. asserts that http://localhost:5173/ serves the dashboard HTML
#   7. tears the stack down again (EXIT/INT/TERM trap, `down -v
#      --remove-orphans`) and prints "e2e-compose: ok"
#
# On failure the last lines of `docker compose --profile full logs --no-color`
# are printed to stderr. All scratch output is written to
# .artifact/e2e-compose/.
#
# The fixed development ports 5080 (Relay), 5091/5092 (Processors), 5173
# (dashboard) and 55432 (PostgreSQL) make this script integration-only: it must
# not run concurrently with e2e-hello.sh, e2e-events.sh, e2e-projects.sh or
# another e2e-compose run.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-compose.sh
#
# Requires: bash, curl, sed, Docker with Compose v2.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e-compose"
up_log="$log_dir/up.log"
compose_log="$log_dir/compose.log"
work_dir="$log_dir/work"

relay_endpoint="http://localhost:5080"
processor_1_endpoint="http://localhost:5091"
processor_2_endpoint="http://localhost:5092"
dashboard_endpoint="http://localhost:5173"

project_id="1"
public_key="dev-public-key"
admin_token="dev-admin-token"
event_total=20
issue_title_marker="NullReferenceException"
example_path="sdk/protocol/examples/exception-unity.json"

mkdir -p "$log_dir" "$work_dir"

[ -f "$example_path" ] || {
    echo "e2e-compose: $example_path not found" >&2
    exit 1
}

fail() {
    echo "e2e-compose: $*" >&2
    exit 1
}

# Print the tail of the stack logs for failure diagnostics. The full output is
# kept under .artifact/e2e-compose for later inspection.
print_compose_logs() {
    echo "e2e-compose: last 80 lines of 'docker compose --profile full logs --no-color':" >&2

    docker compose --profile full logs --no-color >"$compose_log" 2>&1 || true

    if [ -s "$compose_log" ]; then
        tail -n 80 "$compose_log" >&2 || true
    else
        echo "e2e-compose: <no compose logs>" >&2
    fi
}

# The EXIT trap performs the teardown for every outcome, including Ctrl+C and
# CI cancellation (INT/TERM exit and re-enter through EXIT). `-v` also removes
# the anonymous PostgreSQL data volume, and --remove-orphans cleans up
# containers of services that no longer exist.
cleanup() {
    local status=$?
    trap - EXIT INT TERM

    if [ "$status" -ne 0 ]; then
        echo "e2e-compose: FAILED (exit $status)" >&2
        print_compose_logs
        echo "e2e-compose: logs: $log_dir" >&2
    fi

    echo "e2e-compose: tearing the full stack down"
    docker compose --profile full down -v --remove-orphans >/dev/null 2>&1 || true

    if [ "$status" -eq 0 ]; then
        echo "e2e-compose: ok"
    fi

    exit "$status"
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Start from a clean database so the expected event_count is not affected by
# data left behind by an interrupted earlier run.
echo "e2e-compose: removing a stack left by a previous run"
docker compose --profile full down -v --remove-orphans >/dev/null 2>&1 || true

echo "e2e-compose: starting the full stack (log: $up_log)"
if ! docker compose --profile full up -d --build --wait --wait-timeout 300 2>&1 | tee "$up_log"; then
    fail "docker compose --profile full up did not bring the stack up"
fi

# Poll an HTTP endpoint for up to 60 seconds. `up --wait` already waits for
# every healthcheck, but this repeats the check from the host side, which is
# what the SDKs and the dashboard actually use.
wait_for_http() {
    local name="$1"
    local url="$2"
    local deadline=$((SECONDS + 60))

    until curl -sf "$url" >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            fail "$name did not answer $url within 60s"
        fi

        sleep 1
    done

    echo "e2e-compose: $name is ready ($url)"
}

wait_for_http "relay" "$relay_endpoint/healthz"
wait_for_http "processor-1" "$processor_1_endpoint/healthz"
wait_for_http "processor-2" "$processor_2_endpoint/healthz"
wait_for_http "dashboard" "$dashboard_endpoint/"

# Return a fresh 32-character hexadecimal event id. The sequence number keeps
# the ids of one run distinct; the random words keep repeated runs from being
# deduplicated by the Processor. No external tool is needed.
new_event_id() {
    local sequence="$1"

    printf '%08x%08x%08x%08x' "$RANDOM" "$RANDOM" "$RANDOM" "$sequence"
}

# Copy the protocol v1 example, replacing its event_id with <event_id>. The
# rest of the document (message, exception, tags, ...) stays untouched so all
# 20 events group into the same issue.
write_event_body() {
    local event_id="$1"
    local body_file="$2"

    sed -E "s/(\"event_id\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1$event_id\2/" \
        "$example_path" >"$body_file"

    if ! grep -Eq "\"event_id\"[[:space:]]*:[[:space:]]*\"$event_id\"" "$body_file"; then
        fail "could not replace the event_id in $example_path"
    fi
}

# POST one example event through the Relay and require 202 Accepted.
post_event() {
    local sequence="$1"
    local event_id
    local body_file="$work_dir/event-$sequence.json"
    local response_file="$work_dir/event-$sequence-response.json"
    local status

    event_id="$(new_event_id "$sequence")"
    write_event_body "$event_id" "$body_file"

    status="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X POST \
        -H "X-GlitchPort-Key: $public_key" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$relay_endpoint/api/$project_id/events" 2>/dev/null || true)"

    if [ "$status" != "202" ]; then
        echo "e2e-compose: POST /api/$project_id/events returned HTTP ${status:-<no response>} for event $event_id" >&2
        cat "$response_file" >&2 2>/dev/null || true
        echo >&2
        fail "the relay did not accept event $sequence"
    fi
}

echo "e2e-compose: sending $event_total protocol v1 events through the relay"
for ((sequence = 1; sequence <= event_total; sequence++)); do
    post_event "$sequence"
done
echo "e2e-compose: all $event_total events were accepted with HTTP 202"

issues_path="api/manage/projects/$project_id/issues?status=all&limit=100"

# Extract a numeric field from one compact JSON object.
json_number_field() {
    local json="$1"
    local field="$2"

    sed -n -E "s/.*\"$field\"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p" <<<"$json" \
        | sed -n '1p'
}

# Split the issue list into one line per object and keep the lines for the
# example's exception. Title, id and event_count all appear before the nested
# labels array, so a split never separates the fields this script reads.
matching_issue_lines() {
    sed 's/},{/}\n{/g' <<<"$1" | grep -F -- "$issue_title_marker" || true
}

# Poll one management API until the example exception is exactly one issue
# with event_count == <expected>. Both processors must report it because they
# read the same database.
wait_for_issue_count() {
    local name="$1"
    local endpoint="$2"
    local expected="$3"
    local deadline=$((SECONDS + 60))
    local response=""
    local matching=""
    local matches=0
    local count=""

    echo "e2e-compose: waiting for $name to report event_count $expected"

    while :; do
        response="$(curl -sf -H "Authorization: Bearer $admin_token" \
            "$endpoint/$issues_path" 2>/dev/null || true)"
        matching="$(matching_issue_lines "$response")"

        matches=0
        if [ -n "$matching" ]; then
            matches="$(printf '%s\n' "$matching" | wc -l | tr -d '[:space:]')"
        fi

        if [ "$matches" -eq 1 ]; then
            count="$(json_number_field "$matching" "event_count")"

            if [ "$count" = "$expected" ]; then
                echo "e2e-compose: $name reports one $issue_title_marker issue with event_count $count"
                return 0
            fi
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-compose: GET $endpoint/$issues_path returned:" >&2
            printf '%s\n' "${response:-<no response>}" >&2
            echo "e2e-compose: matching issue lines:" >&2
            printf '%s\n' "${matching:-<none>}" >&2
            fail "$name did not report event_count $expected within 60s"
        fi

        sleep 1
    done
}

wait_for_issue_count "processor-1" "$processor_1_endpoint" "$event_total"
wait_for_issue_count "processor-2" "$processor_2_endpoint" "$event_total"

# The Relay rotates between Relay:ProcessorEndpoints, so half of the 20 events
# go through each Processor. Their request logs are the only place where the
# internal hop is visible; `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics`
# is set to Information for exactly this assertion.
assert_handled_envelopes() {
    local service="$1"
    local service_log="$log_dir/$service.log"
    local deadline=$((SECONDS + 30))

    echo "e2e-compose: checking that $service handled POST /internal/envelopes"

    while :; do
        docker compose --profile full logs --no-color "$service" >"$service_log" 2>&1 || true

        if grep -Eq 'POST[[:space:]].*/internal/envelopes' "$service_log"; then
            echo "e2e-compose: $service logged POST /internal/envelopes"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-compose: $service log ($service_log):" >&2
            tail -n 40 "$service_log" >&2 || true
            fail "$service did not log a POST /internal/envelopes request"
        fi

        sleep 1
    done
}

assert_handled_envelopes processor-1
assert_handled_envelopes processor-2

# The dashboard image serves the Vite build through nginx with the history
# fallback, so "/" must return the React document.
echo "e2e-compose: checking the dashboard"
dashboard_status="$(curl -sS -o "$work_dir/dashboard.html" -w '%{http_code}' \
    "$dashboard_endpoint/" 2>/dev/null || true)"

if [ "$dashboard_status" != "200" ]; then
    fail "GET $dashboard_endpoint/ returned HTTP ${dashboard_status:-<no response>}"
fi

if ! grep -qi '<!doctype html' "$work_dir/dashboard.html"; then
    echo "e2e-compose: GET $dashboard_endpoint/ returned:" >&2
    head -n 20 "$work_dir/dashboard.html" >&2 || true
    fail "the dashboard did not return an HTML document"
fi

echo "e2e-compose: dashboard returned HTML"

# The EXIT trap performs the teardown and prints "e2e-compose: ok" once the
# stack is gone.
