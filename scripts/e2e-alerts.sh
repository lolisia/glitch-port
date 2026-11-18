#!/usr/bin/env bash
#
# Integration-only end-to-end check for the alert pipeline and the inbound
# message filter:
#
#   1. resets PostgreSQL (Docker Compose, host port 55432) so every run starts
#      from an empty database, then waits for it
#   2. builds the Processor and the Relay in Release
#   3. starts the Processor (5090) with the alert dispatch worker on a one
#      second cadence, the evaluator on a two second cadence and a dashboard
#      base URL, the Relay (5080) with a one second project config refresh, and
#      a Node webhook receiver on an arbitrary free port; their output goes to
#      .artifact/e2e/e2e-alerts-*.log and the received webhook bodies to
#      .artifact/e2e/e2e-alerts-webhooks.jsonl
#   4. waits for both /healthz endpoints and for the Relay project config
#   5. creates a new_issue webhook rule, posts one Unity example event and waits
#      for exactly one webhook body; reposting the same event id must not
#      deliver another one
#   6. resolves the issue through PATCH /api/manage/issues/<id>, posts a new
#      event id with the same fingerprint and waits for the regression webhook
#   7. disables the two issue rules (so the frequency counts stay unambiguous),
#      creates an event_frequency rule (threshold 3, window 5 minutes, cooldown
#      60 minutes), posts three events with a fresh fingerprint and waits for
#      one webhook; two more events inside the cooldown must not deliver again
#   8. PATCHes project 1 with inbound_filters.message_patterns ["*ignore-me*"],
#      waits for the Relay configuration refresh, posts a message event that
#      matches the filter and asserts the Relay answers 202 with
#      X-GlitchPort-Filtered: message without creating an issue
#   9. always tears the stack down again (processes and containers)
#
# Request bodies are derived with sed and responses are read with grep and sed,
# so the script does not require jq.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-alerts.sh
#
# Requires: bash, curl, Docker with Compose v2, the .NET 10 SDK and Node.js 22.
# The script uses the fixed development ports 5080 (Relay), 5090 (Processor)
# and 55432 (PostgreSQL), so it must not run concurrently with the other e2e
# scripts; scripts/e2e-all.sh runs them one after another.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e"
processor_log="$log_dir/e2e-alerts-processor.log"
relay_log="$log_dir/e2e-alerts-relay.log"
receiver_log="$log_dir/e2e-alerts-receiver.log"
webhook_file="$log_dir/e2e-alerts-webhooks.jsonl"
work_dir="$log_dir/e2e-alerts"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"

project_id="1"
public_key="dev-public-key"
admin_token="dev-admin-token"

events_url="$relay_endpoint/api/$project_id/events"
issues_url="$processor_endpoint/api/manage/projects/$project_id/issues?status=all&limit=100"
alert_rules_url="$processor_endpoint/api/manage/projects/$project_id/alert-rules"
alert_rule_url="$processor_endpoint/api/manage/alert-rules"
project_url="$processor_endpoint/api/manage/projects/$project_id"

example_path="sdk/protocol/examples/exception-unity.json"
issue_title_marker="NullReferenceException"
filtered_message="ignore-me e2e filter event"
filter_pattern="*ignore-me*"

processor_pid=""
relay_pid=""
receiver_pid=""
receiver_port=""
issue_id=""
created_rule_id=""

mkdir -p "$log_dir" "$work_dir"

# Every run starts with an empty JSONL file; the receiver only ever appends.
: >"$webhook_file"

[ -f "$example_path" ] || {
    echo "e2e-alerts: $example_path not found" >&2
    exit 1
}

[ -f scripts/e2e/webhook-receiver.mjs ] || {
    echo "e2e-alerts: scripts/e2e/webhook-receiver.mjs not found" >&2
    exit 1
}

command -v node >/dev/null 2>&1 || {
    echo "e2e-alerts: node (Node.js 22) is required by scripts/e2e/webhook-receiver.mjs" >&2
    exit 1
}

fail() {
    echo "e2e-alerts: $*" >&2
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

# Print the recorded webhook bodies for a failure diagnostic.
dump_webhook_file() {
    if [ -f "$webhook_file" ]; then
        echo "e2e-alerts: webhook bodies in $webhook_file:" >&2
        tail -n 20 "$webhook_file" >&2 || true
    else
        echo "e2e-alerts: no webhook bodies were recorded in $webhook_file" >&2
    fi
}

cleanup() {
    local status=$?
    trap - EXIT

    kill_tree "$receiver_pid"
    kill_tree "$relay_pid"
    kill_tree "$processor_pid"

    # `-v` also removes the anonymous PostgreSQL data volume, so every run
    # starts from an empty database and re-applies the migrations. Tear the
    # containers down before reaping the jobs so a stuck wait cannot leak them.
    docker compose down -v >/dev/null 2>&1 || true

    if [ -n "$receiver_pid" ]; then
        wait "$receiver_pid" 2>/dev/null || true
    fi
    if [ -n "$relay_pid" ]; then
        wait "$relay_pid" 2>/dev/null || true
    fi
    if [ -n "$processor_pid" ]; then
        wait "$processor_pid" 2>/dev/null || true
    fi

    if [ "$status" -ne 0 ]; then
        echo "e2e-alerts: FAILED (exit $status)" >&2
        echo "e2e-alerts: processor log: $processor_log" >&2
        echo "e2e-alerts: relay log: $relay_log" >&2
        echo "e2e-alerts: receiver log: $receiver_log" >&2

        local log_file
        for log_file in "$processor_log" "$relay_log" "$receiver_log"; do
            if [ -f "$log_file" ]; then
                echo "e2e-alerts: last 40 lines of $log_file:" >&2
                tail -n 40 "$log_file" >&2 || true
            fi
        done

        dump_webhook_file
    fi
}

trap cleanup EXIT

# Also unwind on Ctrl+C and on CI cancellation: the EXIT trap performs the
# actual teardown.
trap 'exit 130' INT
trap 'exit 143' TERM

# A previous interrupted run may have left the development database behind.
# This script asserts that a named rule fires exactly once, so it must start
# from an empty schema and no stale alert rules.
echo "e2e-alerts: resetting the development database"
docker compose down -v >/dev/null 2>&1 || true

echo "e2e-alerts: starting postgres"
if ! docker compose up -d --wait postgres; then
    echo "e2e-alerts: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-alerts: postgres did not become ready within 60s" >&2
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
    echo "e2e-alerts: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

# The webhook receiver picks a free port itself and prints "listening on <port>";
# the shell only reads that line, so no port has to be reserved up front.
echo "e2e-alerts: starting the webhook receiver (log: $receiver_log)"
node scripts/e2e/webhook-receiver.mjs --output "$webhook_file" >"$receiver_log" 2>&1 &
receiver_pid=$!

wait_for_receiver_port() {
    local deadline=$((SECONDS + 30))
    local line=""

    echo "e2e-alerts: waiting for the webhook receiver"

    while :; do
        # grep -m 1 exits after the first match without a pipeline, so a
        # partially written log never breaks the poll.
        line="$(grep -m 1 -oE 'listening on [0-9]+' "$receiver_log" 2>/dev/null || true)"

        if [ -n "$line" ]; then
            receiver_port="${line##* }"
            echo "e2e-alerts: webhook receiver listens on port $receiver_port"
            return 0
        fi

        if ! kill -0 "$receiver_pid" >/dev/null 2>&1; then
            echo "e2e-alerts: the webhook receiver exited before reporting its port" >&2
            cat "$receiver_log" >&2 2>/dev/null || true
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-alerts: the webhook receiver did not report its port within 30s" >&2
            cat "$receiver_log" >&2 2>/dev/null || true
            return 1
        fi

        sleep 0.2
    done
}

wait_for_receiver_port

# The receiver is a webhook sink: every request except POST must be rejected
# with 405 before anything is recorded.
receiver_get_code="$(curl -sS -o "$work_dir/receiver-get-response.json" -w '%{http_code}' \
    "http://127.0.0.1:$receiver_port/new-issue" 2>/dev/null || true)"
if [ "$receiver_get_code" != "405" ]; then
    fail "the webhook receiver answered GET with HTTP ${receiver_get_code:-<no response>}, expected 405"
fi
echo "e2e-alerts: the webhook receiver rejects GET with 405"

echo "e2e-alerts: starting processor (log: $processor_log)"
ASPNETCORE_ENVIRONMENT=Development \
    Processor__Alerts__DispatchIntervalSeconds=1 \
    Processor__Alerts__EvaluateIntervalSeconds=2 \
    Processor__Alerts__DashboardBaseUrl=http://localhost:5173 \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-alerts: starting relay (log: $relay_log)"
ASPNETCORE_ENVIRONMENT=Development \
    Relay__ConfigRefreshSeconds=1 \
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
            echo "e2e-alerts: $name exited before answering $url" >&2
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-alerts: $name did not answer $url within 60s" >&2
            return 1
        fi

        sleep 1
    done

    echo "e2e-alerts: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"

# Poll the Relay /healthz until its project configuration was loaded from the
# Processor. Until that first successful refresh the Relay answers ingest
# requests with 503, so no event may be sent before config.source is
# "processor".
wait_for_relay_config() {
    local deadline=$((SECONDS + 60))
    local body=""
    local source=""

    echo "e2e-alerts: waiting for the relay project configuration"

    while :; do
        body="$(curl -sf "$relay_endpoint/healthz" 2>/dev/null || true)"
        source="$(sed -n -E 's/.*"source"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p' <<<"$body")"

        if [ "$source" = "processor" ]; then
            echo "e2e-alerts: relay loaded the project configuration from the processor"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-alerts: relay did not report config.source=processor within 60s" >&2
            printf '%s\n' "${body:-<no response>}" >&2
            return 1
        fi

        sleep 1
    done
}

wait_for_relay_config

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

# Copy the Unity protocol example like write_event_body and add an explicit
# fingerprint right after the opening brace, so every event of a group maps to
# a fresh issue that no earlier step touched.
write_frequency_event_body() {
    local event_id="$1"
    local fingerprint="$2"
    local destination="$3"

    sed -E \
        -e 's/("event_id"[[:space:]]*:[[:space:]]*")[^"]*(")/\1'"$event_id"'\2/' \
        -e '1s/^\{$/{\n  "fingerprint": ["'"$fingerprint"'"],/' \
        "$example_path" >"$destination"

    if ! grep -Fq "\"event_id\": \"$event_id\"" "$destination"; then
        fail "could not rewrite event_id to $event_id in $destination"
    fi

    if ! grep -Fq "\"fingerprint\": [\"$fingerprint\"]" "$destination"; then
        fail "could not add the fingerprint $fingerprint to $destination"
    fi
}

# POST one ingest body and require the Relay's 202 Accepted response.
post_ingest() {
    local description="$1"
    local body_file="$2"
    local response_file="$work_dir/post-response.json"
    local http_code

    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X POST \
        -H "X-GlitchPort-Key: $public_key" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$events_url" 2>/dev/null || true)"

    if [ "$http_code" != "202" ]; then
        echo "e2e-alerts: POST $events_url failed for $description (HTTP ${http_code:-<no response>})" >&2
        cat "$response_file" >&2 2>/dev/null || true
        echo >&2
        fail "$description was not accepted by the Relay"
    fi

    echo "e2e-alerts: posted $description"
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
# contains the example's exception. Labels are nested objects, but the id and
# title of an issue appear before the labels array, so a split never separates
# the fields this script reads.
matching_issue_lines() {
    sed 's/},{/}\n{/g' <<<"$1" | grep -F -- "$issue_title_marker" || true
}

# Print the number of webhook bodies recorded so far.
count_webhook_lines() {
    local count=""

    if [ ! -f "$webhook_file" ]; then
        printf '0'
        return 0
    fi

    count="$( { wc -l <"$webhook_file" || true; } | tr -d '[:space:]')"
    printf '%s' "${count:-0}"
}

# Print the number of webhook bodies whose "trigger" field is <trigger>.
count_webhook_trigger() {
    local trigger="$1"
    local count=""

    if [ ! -f "$webhook_file" ]; then
        printf '0'
        return 0
    fi

    count="$(
        {
            grep -Fc "\"trigger\":\"$trigger\"" "$webhook_file" 2>/dev/null || true
        } | tr -d '[:space:]'
    )"
    printf '%s' "${count:-0}"
}

# Poll until at least <expected> webhook bodies carry the trigger, then log it.
wait_for_webhook_trigger() {
    local trigger="$1"
    local expected="$2"
    local description="$3"
    local deadline=$((SECONDS + 60))
    local count=0

    echo "e2e-alerts: waiting for $description"

    while :; do
        count="$(count_webhook_trigger "$trigger")"

        if [ "$count" -ge "$expected" ]; then
            echo "e2e-alerts: received $count $trigger webhook body/bodies"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            dump_webhook_file
            fail "no $trigger webhook for $description within 60s"
        fi

        sleep 0.5
    done
}

# Wait <settle_seconds>, then require exactly <expected> recorded bodies. The
# settle window is deliberately longer than the one second dispatch interval so
# a delivery that should have been suppressed has time to show up.
assert_webhook_count() {
    local expected="$1"
    local settle_seconds="$2"
    local description="$3"
    local count=0

    echo "e2e-alerts: waiting ${settle_seconds}s to check that $description"
    sleep "$settle_seconds"

    count="$(count_webhook_lines)"
    if [ "$count" -ne "$expected" ]; then
        dump_webhook_file
        fail "expected $expected webhook body/bodies for $description, received $count"
    fi

    echo "e2e-alerts: received $count webhook body/bodies, as expected"
}

# The receiver logs every accepted POST together with its path. Assert that a
# webhook actually reached the path of the rule that was supposed to fire; the
# bodies themselves are recorded without their path by request.
assert_receiver_path() {
    local path="$1"
    local description="$2"
    local deadline=$((SECONDS + 5))

    echo "e2e-alerts: waiting for a webhook request on $path"

    until grep -Fq "received POST $path" "$receiver_log" 2>/dev/null; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-alerts: the webhook receiver log does not mention a POST to $path" >&2
            cat "$receiver_log" >&2 2>/dev/null || true
            fail "no $description reached $path"
        fi

        sleep 0.2
    done

    echo "e2e-alerts: a webhook reached $path"
}

# Wait <settle_seconds>, then require exactly <expected> bodies of a trigger.
assert_webhook_trigger_count() {
    local trigger="$1"
    local expected="$2"
    local settle_seconds="$3"
    local description="$4"
    local count=0

    echo "e2e-alerts: waiting ${settle_seconds}s to check that $description"
    sleep "$settle_seconds"

    count="$(count_webhook_trigger "$trigger")"
    if [ "$count" -ne "$expected" ]; then
        dump_webhook_file
        fail "expected $expected $trigger webhook body/bodies for $description, received $count"
    fi

    echo "e2e-alerts: received $count $trigger webhook body/bodies, as expected"
}

# Create one alert rule for project 1 and store its id in created_rule_id.
# <threshold> and <window_minutes> are numbers for event_frequency rules and
# the literal null for every other trigger.
create_alert_rule() {
    local name="$1"
    local trigger="$2"
    local threshold="$3"
    local window_minutes="$4"
    local target_url="$5"
    local body_file="$work_dir/alert-rule-$trigger.json"
    local response_file="$work_dir/alert-rule-$trigger-response.json"
    local http_code=""
    local response=""
    local id=""

    printf '{"name":"%s","trigger":"%s","min_level":null,"environment":null,"threshold":%s,"window_minutes":%s,"cooldown_minutes":60,"channel":"webhook","target_url":"%s","is_enabled":true}\n' \
        "$name" "$trigger" "$threshold" "$window_minutes" "$target_url" >"$body_file"

    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X POST \
        -H "Authorization: Bearer $admin_token" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$alert_rules_url" 2>/dev/null || true)"

    response="$(cat "$response_file" 2>/dev/null || true)"

    if [ "$http_code" != "201" ]; then
        echo "e2e-alerts: POST $alert_rules_url failed for '$name' (HTTP ${http_code:-<no response>})" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        fail "could not create the $trigger alert rule"
    fi

    if [ "$(json_string_field "$response" "trigger")" != "$trigger" ]; then
        printf '%s\n' "$response" >&2
        fail "the created $trigger alert rule reports a different trigger"
    fi

    id="$(json_number_field "$response" "id")"
    if [ -z "$id" ]; then
        printf '%s\n' "$response" >&2
        fail "the created $trigger alert rule has no id"
    fi

    created_rule_id="$id"
    echo "e2e-alerts: created the $trigger alert rule $id"
}

# PATCH one alert rule to is_enabled=false.
disable_alert_rule() {
    local rule_id="$1"
    local description="$2"
    local body_file="$work_dir/disable-alert-rule-$rule_id.json"
    local response_file="$work_dir/disable-alert-rule-$rule_id-response.json"
    local http_code=""
    local response=""

    printf '{"is_enabled":false}\n' >"$body_file"

    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X PATCH \
        -H "Authorization: Bearer $admin_token" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$alert_rule_url/$rule_id" 2>/dev/null || true)"

    response="$(cat "$response_file" 2>/dev/null || true)"

    if [ "$http_code" != "200" ]; then
        echo "e2e-alerts: PATCH $alert_rule_url/$rule_id failed (HTTP ${http_code:-<no response>})" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        fail "could not disable the $description alert rule"
    fi

    if ! grep -Fq '"is_enabled":false' <<<"$response"; then
        printf '%s\n' "$response" >&2
        fail "the $description alert rule is still enabled"
    fi

    echo "e2e-alerts: disabled the $description alert rule ($rule_id)"
}

# Poll the Management API until exactly one issue matches the Unity example,
# then remember its id in issue_id.
wait_for_issue_id() {
    local deadline=$((SECONDS + 30))
    local response=""
    local matching=""
    local matches=0
    local id=""

    echo "e2e-alerts: waiting for the Unity example issue"

    while :; do
        response="$(curl -sf -H "Authorization: Bearer $admin_token" "$issues_url" 2>/dev/null || true)"
        matching="$(matching_issue_lines "$response")"

        matches=0
        if [ -n "$matching" ]; then
            matches="$(printf '%s\n' "$matching" | wc -l | tr -d '[:space:]')"
        fi

        if [ "$matches" -eq 1 ]; then
            id="$(json_number_field "$matching" "id")"
            if [ -n "$id" ]; then
                issue_id="$id"
                echo "e2e-alerts: issue $id matches the Unity example"
                return 0
            fi
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-alerts: GET $issues_url returned:" >&2
            printf '%s\n' "${response:-<no response>}" >&2
            fail "the Unity example issue did not appear within 30s"
        fi

        sleep 0.5
    done
}

# Change the issue status through the Management API and assert the response.
patch_issue_status() {
    local id="$1"
    local status="$2"
    local body_file="$work_dir/patch-status-$id.json"
    local response_file="$work_dir/patch-status-$id-response.json"
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
        echo "e2e-alerts: PATCH /api/manage/issues/$id failed (HTTP ${http_code:-<no response>})" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        fail "could not mark issue $id as $status"
    fi

    if [ "$(json_string_field "$response" "status")" != "$status" ]; then
        printf '%s\n' "$response" >&2
        fail "issue $id was not marked as $status"
    fi

    echo "e2e-alerts: issue $id is $status"
}

# Poll the Management API until the issue has the expected status.
wait_for_issue_status() {
    local expected="$1"
    local deadline=$((SECONDS + 30))
    local response=""
    local status=""

    echo "e2e-alerts: waiting for issue $issue_id to be $expected"

    while :; do
        response="$(curl -sf -H "Authorization: Bearer $admin_token" \
            "$processor_endpoint/api/manage/issues/$issue_id" 2>/dev/null || true)"
        status="$(json_string_field "$response" "status")"

        if [ "$status" = "$expected" ]; then
            echo "e2e-alerts: issue $issue_id is $expected"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            printf '%s\n' "${response:-<no response>}" >&2
            fail "issue $issue_id did not become $expected within 30s"
        fi

        sleep 0.5
    done
}

# Read the Relay healthz last_refresh timestamp (an empty string when the Relay
# has not refreshed yet or the field is missing).
relay_last_refresh() {
    local body=""

    body="$(curl -sf "$relay_endpoint/healthz" 2>/dev/null || true)"
    sed -n -E 's/.*"last_refresh"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p' <<<"$body"
}

# Wait until the Relay completed <needed> configuration refreshes after
# <baseline>. Two refreshes guarantee that at least one fetch started after the
# project update: the second one begins only after the first one finished.
wait_for_relay_refreshes() {
    local baseline="$1"
    local needed="$2"
    local deadline=$((SECONDS + 60))
    local current=""
    local last="$baseline"
    local changes=0

    echo "e2e-alerts: waiting for $needed Relay configuration refresh(es)"

    while [ "$changes" -lt "$needed" ]; do
        current="$(relay_last_refresh)"

        if [ -n "$current" ] && [ "$current" != "$last" ]; then
            changes=$((changes + 1))
            last="$current"
            echo "e2e-alerts: relay configuration refresh $changes/$needed ($current)"
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            fail "the Relay did not refresh its project configuration $needed time(s) within 60s"
        fi

        sleep 0.5
    done
}

# PATCH project 1 so the Relay drops matching messages before publishing them.
patch_project_filters() {
    local body_file="$work_dir/project-inbound-filters.json"
    local response_file="$work_dir/project-inbound-filters-response.json"
    local http_code=""
    local response=""

    printf '{"inbound_filters":{"message_patterns":["%s"]}}\n' "$filter_pattern" >"$body_file"

    http_code="$(curl -sS -o "$response_file" -w '%{http_code}' \
        -X PATCH \
        -H "Authorization: Bearer $admin_token" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$project_url" 2>/dev/null || true)"

    response="$(cat "$response_file" 2>/dev/null || true)"

    if [ "$http_code" != "200" ]; then
        echo "e2e-alerts: PATCH $project_url failed (HTTP ${http_code:-<no response>})" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        fail "could not store the project inbound filters"
    fi

    if ! grep -Fq "\"message_patterns\":[\"$filter_pattern\"]" <<<"$response"; then
        printf '%s\n' "$response" >&2
        fail "project $project_id did not store the inbound message filter"
    fi

    echo "e2e-alerts: project $project_id now filters messages matching $filter_pattern"
}

# POST one message-only event and require a 202 that names the message filter.
post_filtered_message() {
    local body_file="$1"
    local headers_file="$work_dir/filtered-message-headers.txt"
    local response_file="$work_dir/filtered-message-response.json"
    local http_code=""
    local response=""

    http_code="$(curl -sS -D "$headers_file" -o "$response_file" -w '%{http_code}' \
        -X POST \
        -H "X-GlitchPort-Key: $public_key" \
        -H "Content-Type: application/json" \
        --data-binary @"$body_file" \
        "$events_url" 2>/dev/null || true)"

    response="$(cat "$response_file" 2>/dev/null || true)"

    if [ "$http_code" != "202" ]; then
        echo "e2e-alerts: POST $events_url for the filtered message returned HTTP ${http_code:-<no response>}" >&2
        printf '%s\n' "${response:-<no response>}" >&2
        fail "the filtered message event was not accepted with 202"
    fi

    if ! grep -iqE '^x-glitchport-filtered:[[:space:]]*message' "$headers_file"; then
        echo "e2e-alerts: the Relay answered 202 without X-GlitchPort-Filtered: message" >&2
        cat "$headers_file" >&2 2>/dev/null || true
        printf '%s\n' "$response" >&2
        fail "the message filter did not report X-GlitchPort-Filtered: message"
    fi

    echo "e2e-alerts: the Relay filtered the message event (202, X-GlitchPort-Filtered: message)"
}

# The filtered event never reached the Processor, so no issue may carry its
# message. Poll for a few seconds because an unwanted publish would be stored
# asynchronously.
assert_no_filtered_issue() {
    local deadline=$((SECONDS + 5))
    local response=""

    echo "e2e-alerts: checking that the filtered message created no issue"

    while :; do
        response="$(curl -sf -H "Authorization: Bearer $admin_token" "$issues_url" 2>/dev/null || true)"

        if grep -Fq -- "$filtered_message" <<<"$response"; then
            echo "e2e-alerts: GET $issues_url returned:" >&2
            printf '%s\n' "$response" >&2
            fail "a filtered message event created an issue"
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            break
        fi

        sleep 1
    done

    echo "e2e-alerts: no issue was created for the filtered message"
}

new_issue_target="http://127.0.0.1:$receiver_port/new-issue"
regression_target="http://127.0.0.1:$receiver_port/regression"
frequency_target="http://127.0.0.1:$receiver_port/frequency"

# --- new_issue ------------------------------------------------------------
create_alert_rule "e2e new issue" "new_issue" "null" "null" "$new_issue_target"
new_issue_rule_id="$created_rule_id"

new_issue_event_id="$(new_event_id)"
write_event_body "$new_issue_event_id" "$work_dir/event-new-issue.json"
post_ingest "the first Unity example event" "$work_dir/event-new-issue.json"

wait_for_webhook_trigger "new_issue" 1 "the new_issue webhook"
assert_receiver_path "/new-issue" "the new_issue webhook"
assert_webhook_trigger_count "new_issue" 1 3 "the new_issue rule delivered exactly one webhook"

# --- resend suppression ----------------------------------------------------
post_ingest "the same Unity example event id again" "$work_dir/event-new-issue.json"
assert_webhook_count 1 3 "the duplicate event id produced no additional webhook body"

# --- regression ------------------------------------------------------------
wait_for_issue_id
patch_issue_status "$issue_id" "resolved"

create_alert_rule "e2e regression" "regression" "null" "null" "$regression_target"
regression_rule_id="$created_rule_id"

regression_event_id="$(new_event_id)"
write_event_body "$regression_event_id" "$work_dir/event-regression.json"
post_ingest "a new event id with the resolved issue's fingerprint" "$work_dir/event-regression.json"

wait_for_webhook_trigger "regression" 1 "the regression webhook"
assert_receiver_path "/regression" "the regression webhook"
assert_webhook_trigger_count "regression" 1 3 "the regression rule delivered exactly one webhook"
wait_for_issue_status "unresolved"

# --- event frequency -------------------------------------------------------
# The frequency counts must only see the events of the fresh fingerprint, so
# the two issue rules are disabled first; the new issue they would report does
# not produce a webhook at all.
disable_alert_rule "$new_issue_rule_id" "new_issue"
disable_alert_rule "$regression_rule_id" "regression"

create_alert_rule "e2e frequency" "event_frequency" "3" "5" "$frequency_target"

frequency_fingerprint="e2e-alerts-frequency-$(new_event_id)"

frequency_event_id="$(new_event_id)"
write_frequency_event_body "$frequency_event_id" "$frequency_fingerprint" "$work_dir/event-frequency-1.json"
post_ingest "frequency event 1 with the fresh fingerprint" "$work_dir/event-frequency-1.json"

frequency_event_id="$(new_event_id)"
write_frequency_event_body "$frequency_event_id" "$frequency_fingerprint" "$work_dir/event-frequency-2.json"
post_ingest "frequency event 2 with the fresh fingerprint" "$work_dir/event-frequency-2.json"

frequency_event_id="$(new_event_id)"
write_frequency_event_body "$frequency_event_id" "$frequency_fingerprint" "$work_dir/event-frequency-3.json"
post_ingest "frequency event 3 with the fresh fingerprint" "$work_dir/event-frequency-3.json"

wait_for_webhook_trigger "event_frequency" 1 "the event_frequency webhook"
assert_receiver_path "/frequency" "the event_frequency webhook"

# Two more events reach the threshold again, but the cooldown bucket of the
# rule suppresses a second notification.
frequency_event_id="$(new_event_id)"
write_frequency_event_body "$frequency_event_id" "$frequency_fingerprint" "$work_dir/event-frequency-4.json"
post_ingest "frequency event 4 inside the cooldown" "$work_dir/event-frequency-4.json"

frequency_event_id="$(new_event_id)"
write_frequency_event_body "$frequency_event_id" "$frequency_fingerprint" "$work_dir/event-frequency-5.json"
post_ingest "frequency event 5 inside the cooldown" "$work_dir/event-frequency-5.json"

assert_webhook_trigger_count "event_frequency" 1 5 "the cooldown suppresses a second frequency webhook"
assert_webhook_count 3 0 "the frequency rule produced exactly one of the three webhook bodies"

# --- inbound message filter -------------------------------------------------
refresh_baseline="$(relay_last_refresh)"
patch_project_filters
wait_for_relay_refreshes "$refresh_baseline" 2

filtered_event_id="$(new_event_id)"
filtered_timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf '{"event_id":"%s","timestamp":"%s","level":"error","platform":"unity","message":"%s"}\n' \
    "$filtered_event_id" "$filtered_timestamp" "$filtered_message" \
    >"$work_dir/event-filtered-message.json"

post_filtered_message "$work_dir/event-filtered-message.json"
assert_no_filtered_issue

echo "e2e-alerts: ok"
