#!/usr/bin/env bash
#
# End-to-end check for the .NET SDK ASP.NET Core integration:
#
#   1. starts PostgreSQL (Docker Compose, host port 55432) and waits for it
#   2. builds the Processor, the Relay and the GameServerSample in Release
#   3. starts the Processor (5090) and the Relay (5080) in the background with
#      ASPNETCORE_ENVIRONMENT=Development, writing their output to
#      .artifact/e2e/e2e-dotnet-server-*.log
#   4. starts GameServerSample (5070) with ASPNETCORE_ENVIRONMENT=Development
#      and ASPNETCORE_URLS=http://localhost:5070, using --no-build
#      --no-launch-profile; its GlitchPort:Dsn points at the Relay
#   5. waits for both /healthz endpoints and for GET /ok
#   6. calls GET /boom twice (UseGlitchPort captures the unhandled request
#      exception) and GET /log-error once (AddGlitchPort forwards the ILogger
#      error as an event)
#   7. polls the Management API until the System.InvalidOperationException issue
#      has event_count 2, then asserts the stored event: the exception array is
#      inner to outer (ArgumentException first), contexts.request.method is GET
#      and at least one stack frame is in_app
#   8. asserts that the log-error issue exists
#
# The JSON responses are read with Node.js, so the script does not need jq. It
# always tears the whole stack down (sample, Relay, Processor and containers)
# through an EXIT trap.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-dotnet-server.sh
#
# Requires: bash, curl, Docker with Compose v2, the .NET 10 SDK and Node.js.
# This is an integration check with fixed ports (5070, 5080, 5090 and 55432), so
# it is deliberately not part of scripts/verify.sh and must not run
# concurrently with e2e-hello.sh or e2e-events.sh.
#
# Windows Git Bash compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

log_dir=".artifact/e2e"
processor_log="$log_dir/e2e-dotnet-server-processor.log"
relay_log="$log_dir/e2e-dotnet-server-relay.log"
sample_log="$log_dir/e2e-dotnet-server-sample.log"
work_dir="$log_dir/e2e-dotnet-server"

relay_endpoint="http://localhost:5080"
processor_endpoint="http://localhost:5090"
sample_endpoint="http://localhost:5070"

project_id="1"
admin_token="dev-admin-token"
issues_url="$processor_endpoint/api/manage/projects/$project_id/issues?status=all&limit=100"

# Titles and values the sample throws and logs (see GameServerSample/Program.cs).
boom_title_prefix="System.InvalidOperationException"
boom_inner_type="System.ArgumentException"
boom_outer_type="System.InvalidOperationException"
boom_inner_message="inner argument"
boom_outer_message="boom from GameServerSample"
log_error_title_prefix="log error from GameServerSample"

sample_pid=""
relay_pid=""
processor_pid=""

mkdir -p "$log_dir" "$work_dir"

if ! command -v node >/dev/null 2>&1; then
    echo "e2e-dotnet-server: node is required to read the management API JSON" >&2
    exit 1
fi

fail() {
    echo "e2e-dotnet-server: $*" >&2
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

    kill_tree "$sample_pid"
    kill_tree "$relay_pid"
    kill_tree "$processor_pid"

    # `-v` also removes the anonymous PostgreSQL data volume, so every run
    # starts from an empty database and re-applies the migrations. Tear the
    # containers down before reaping the jobs so a stuck wait cannot leak them.
    docker compose down -v >/dev/null 2>&1 || true

    if [ -n "$sample_pid" ]; then
        wait "$sample_pid" 2>/dev/null || true
    fi
    if [ -n "$relay_pid" ]; then
        wait "$relay_pid" 2>/dev/null || true
    fi
    if [ -n "$processor_pid" ]; then
        wait "$processor_pid" 2>/dev/null || true
    fi

    if [ "$status" -ne 0 ]; then
        echo "e2e-dotnet-server: FAILED (exit $status)" >&2
        echo "e2e-dotnet-server: processor log: $processor_log" >&2
        echo "e2e-dotnet-server: relay log: $relay_log" >&2
        echo "e2e-dotnet-server: sample log: $sample_log" >&2

        local log_file
        for log_file in "$processor_log" "$relay_log" "$sample_log"; do
            if [ -f "$log_file" ]; then
                echo "e2e-dotnet-server: last 40 lines of $log_file:" >&2
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

echo "e2e-dotnet-server: starting postgres"
if ! docker compose up -d --wait postgres; then
    echo "e2e-dotnet-server: 'docker compose up --wait' failed; polling pg_isready instead" >&2
    docker compose up -d postgres

    deadline=$((SECONDS + 60))
    until docker compose exec -T postgres pg_isready -U glitchport -d glitchport >/dev/null 2>&1; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-dotnet-server: postgres did not become ready within 60s" >&2
            exit 1
        fi
        sleep 1
    done
fi

projects=(
    collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj
    collector/src/GlitchPort.Relay/GlitchPort.Relay.csproj
    sdk/dotnet/samples/GameServerSample/GameServerSample.csproj
)

for project in "${projects[@]}"; do
    echo "e2e-dotnet-server: dotnet build -c Release $project"
    dotnet build -c Release "$project"
done

echo "e2e-dotnet-server: starting processor (log: $processor_log)"
ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project collector/src/GlitchPort.Processor/GlitchPort.Processor.csproj \
    -c Release --no-build >"$processor_log" 2>&1 &
processor_pid=$!

echo "e2e-dotnet-server: starting relay (log: $relay_log)"
ASPNETCORE_ENVIRONMENT=Development Relay__ConfigRefreshSeconds=2 \
    dotnet run --project collector/src/GlitchPort.Relay/GlitchPort.Relay.csproj \
    -c Release --no-build >"$relay_log" 2>&1 &
relay_pid=$!

echo "e2e-dotnet-server: starting GameServerSample (log: $sample_log)"
ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$sample_endpoint" \
    dotnet run --project sdk/dotnet/samples/GameServerSample/GameServerSample.csproj \
    -c Release --no-build --no-launch-profile >"$sample_log" 2>&1 &
sample_pid=$!

# Poll a readiness endpoint for up to 60 seconds, failing early when the
# background process died (for example because its port was already in use).
wait_for_health() {
    local name="$1"
    local url="$2"
    local pid="$3"
    local deadline=$((SECONDS + 60))

    until curl -sf "$url" >/dev/null 2>&1; do
        if ! kill -0 "$pid" >/dev/null 2>&1; then
            echo "e2e-dotnet-server: $name exited before answering $url" >&2
            return 1
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-dotnet-server: $name did not answer $url within 60s" >&2
            return 1
        fi

        sleep 1
    done

    echo "e2e-dotnet-server: $name is healthy"
}

wait_for_health "processor" "$processor_endpoint/healthz" "$processor_pid"
wait_for_health "relay" "$relay_endpoint/healthz" "$relay_pid"
wait_for_health "GameServerSample" "$sample_endpoint/ok" "$sample_pid"

# Poll the Relay /healthz until its project configuration was loaded from the
# Processor. Until that first successful refresh the Relay answers ingest
# requests with 503, so no event may be sent before config.source is
# "processor".
wait_for_relay_config() {
    local deadline=$((SECONDS + 60))
    local body=""
    local source=""

    echo "e2e-dotnet-server: waiting for the relay project configuration"

    while :; do
        body="$(curl -sf "$relay_endpoint/healthz" 2>/dev/null || true)"
        source="$(sed -n -E 's/.*"source"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p' <<<"$body")"

        if [ "$source" = "processor" ]; then
            echo "e2e-dotnet-server: relay loaded the project configuration from the processor"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-dotnet-server: relay did not report config.source=processor within 60s" >&2
            printf '%s\n' "${body:-<no response>}" >&2
            return 1
        fi

        sleep 1
    done
}

wait_for_relay_config

# Call one endpoint and print its HTTP status code ("000" when the request
# never reached the host). The body is kept in the work directory so a failure
# can be reported.
http_status() {
    local url="$1"

    curl -sS -o "$work_dir/last-response.txt" -w '%{http_code}' "$url" 2>/dev/null || true
}

echo "e2e-dotnet-server: calling GET /boom twice"
for attempt in 1 2; do
    status="$(http_status "$sample_endpoint/boom")"

    if [ "$status" != "500" ]; then
        echo "e2e-dotnet-server: GET /boom returned HTTP ${status:-<no response>}" >&2
        cat "$work_dir/last-response.txt" >&2 2>/dev/null || true
        fail "GET /boom did not answer 500"
    fi
done

echo "e2e-dotnet-server: calling GET /log-error once"
status="$(http_status "$sample_endpoint/log-error")"

if [ "$status" != "200" ]; then
    echo "e2e-dotnet-server: GET /log-error returned HTTP ${status:-<no response>}" >&2
    cat "$work_dir/last-response.txt" >&2 2>/dev/null || true
    fail "GET /log-error did not answer 200"
fi

# Print "<id> <event_count>" for every issue whose title starts with the given
# prefix, one line per issue. Node.js parses the response, so no jq is needed.
# An unreadable document (the Processor is still starting) prints nothing.
list_matching_issues() {
    local issues_file="$1"
    local title_prefix="$2"

    node - "$issues_file" "$title_prefix" <<'NODE'
const fs = require("node:fs");

const [issuesFile, titlePrefix] = process.argv.slice(2);
let page;

try {
  page = JSON.parse(fs.readFileSync(issuesFile, "utf8"));
} catch {
  process.exit(0);
}

const items = page && Array.isArray(page.items) ? page.items : [];

for (const issue of items) {
  const title = typeof issue.title === "string" ? issue.title : "";
  if (title.startsWith(titlePrefix) && Number.isInteger(issue.event_count)) {
    console.log(`${issue.id} ${issue.event_count}`);
  }
}
NODE
}

# Poll the Management API until exactly one issue matches the title prefix and
# has the expected event_count. An empty expected_count accepts any count.
# Stores the matching issue id in issue_id.
issue_id=""
wait_for_issue() {
    local title_prefix="$1"
    local expected_count="$2"
    local description="$3"
    local deadline=$((SECONDS + 30))
    local matching=""
    local matches=0
    local id=""
    local count=""

    echo "e2e-dotnet-server: waiting for $description"

    while :; do
        matching=""
        if curl -sf -H "Authorization: Bearer $admin_token" \
            -o "$work_dir/issues.json" "$issues_url" 2>/dev/null; then
            matching="$(list_matching_issues "$work_dir/issues.json" "$title_prefix")"
        fi

        matches=0
        if [ -n "$matching" ]; then
            matches="$(printf '%s\n' "$matching" | wc -l | tr -d '[:space:]')"
        fi

        if [ "$matches" -eq 1 ]; then
            id="${matching%% *}"
            count="${matching#* }"

            if [ -z "$expected_count" ] || [ "$count" = "$expected_count" ]; then
                issue_id="$id"
                echo "e2e-dotnet-server: issue $id (title prefix '$title_prefix') has event_count $count"
                return 0
            fi
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "e2e-dotnet-server: GET $issues_url returned:" >&2
            cat "$work_dir/issues.json" >&2 2>/dev/null || true
            echo "e2e-dotnet-server: matching issue lines:" >&2
            printf '%s\n' "${matching:-<none>}" >&2
            fail "expected $description within 30s"
        fi

        sleep 1
    done
}

# Assert the stored /boom event: exactly two exceptions ordered inner to outer,
# contexts.request.method == GET and at least one in_app stack frame.
assert_boom_event() {
    local detail_file="$1"

    node - "$detail_file" "$boom_inner_type" "$boom_outer_type" "$boom_inner_message" "$boom_outer_message" <<'NODE'
const fs = require("node:fs");

const [detailFile, innerType, outerType, innerMessage, outerMessage] = process.argv.slice(2);
const problems = [];

let detail;
try {
  detail = JSON.parse(fs.readFileSync(detailFile, "utf8"));
} catch (error) {
  console.error(`e2e-dotnet-server: ${detailFile} is not valid JSON: ${error.message}`);
  process.exit(1);
}

const fail = (message) => problems.push(message);
const event = detail && detail.latest_event;

if (!event || typeof event !== "object") {
  fail("latest_event is missing");
} else {
  const payload = event.payload;

  if (!payload || typeof payload !== "object") {
    fail("latest_event.payload is missing");
  } else {
    const exceptions = Array.isArray(payload.exception) ? payload.exception : [];

    if (exceptions.length !== 2) {
      fail(`payload.exception must have 2 items (inner to outer), found ${exceptions.length}`);
    } else {
      const inner = exceptions[0] || {};
      const outer = exceptions[1] || {};

      if (inner.type !== innerType) {
        fail(`payload.exception[0].type is ${JSON.stringify(inner.type)} instead of ${JSON.stringify(innerType)}`);
      }
      if (inner.value !== innerMessage) {
        fail(`payload.exception[0].value is ${JSON.stringify(inner.value)} instead of ${JSON.stringify(innerMessage)}`);
      }
      if (outer.type !== outerType) {
        fail(`payload.exception[1].type is ${JSON.stringify(outer.type)} instead of ${JSON.stringify(outerType)}`);
      }
      if (outer.value !== outerMessage) {
        fail(`payload.exception[1].value is ${JSON.stringify(outer.value)} instead of ${JSON.stringify(outerMessage)}`);
      }
    }

    const request = payload.contexts && payload.contexts.request;
    if (!request || typeof request !== "object") {
      fail("payload.contexts.request is missing");
    } else if (request.method !== "GET") {
      fail(`payload.contexts.request.method is ${JSON.stringify(request.method)} instead of "GET"`);
    }

    const frames = exceptions.flatMap((exception) => {
      const stacktrace = exception && exception.stacktrace;
      return stacktrace && Array.isArray(stacktrace.frames) ? stacktrace.frames : [];
    });

    if (!frames.some((frame) => frame && frame.in_app === true)) {
      fail("no stack frame has in_app true");
    }
  }
}

if (problems.length > 0) {
  console.error("e2e-dotnet-server: the stored /boom event does not match:");
  for (const problem of problems) {
    console.error(`e2e-dotnet-server: - ${problem}`);
  }
  console.error(`e2e-dotnet-server: GET /api/manage/issues/${detail.id} returned:`);
  console.error(fs.readFileSync(detailFile, "utf8"));
  process.exit(1);
}

console.log("e2e-dotnet-server: the stored /boom event has the inner exception first, contexts.request.method GET and an in_app frame");
NODE
}

wait_for_issue "$boom_title_prefix" "2" "one issue for the /boom requests with event_count 2"
boom_issue_id="$issue_id"

detail_url="$processor_endpoint/api/manage/issues/$boom_issue_id"
if ! curl -sf -H "Authorization: Bearer $admin_token" \
    -o "$work_dir/boom-issue.json" "$detail_url"; then
    fail "GET $detail_url failed"
fi

assert_boom_event "$work_dir/boom-issue.json"

wait_for_issue "$log_error_title_prefix" "" "the /log-error issue"

echo "e2e-dotnet-server: ok"
