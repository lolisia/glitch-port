#!/usr/bin/env bash
#
# End-to-end check for the Unity SDK package (sdk/unity/com.glitchport.unity).
#
#   1. Build sdk/unity/TestProject as a Windows 64-bit development player
#      (Mono backend) that contains Assets/E2E/E2EScene.unity.
#   2. Run the player headlessly with GLITCHPORT_DSN=file:?dir=<tmp>/logs and
#      -glitchport-e2e. E2EDriver proves automatic initialization (it never
#      calls Init), captures "e2e unity error" through the Unity log bridge,
#      "e2e unity message" through the manual API and a nested
#      InvalidOperationException with an inner ArgumentException through
#      Debug.LogException, flushes every relay and exits with 0 only when the
#      captures and the flush succeeded.
#   3. Assert the NDJSON file under <tmp>/logs contains the error event (as a
#      message or as the value of the synthesized LogError exception, because
#      Unity reports a stacked Error log as an exception event), the warning
#      event with level=warning, and the nested exception with its inner cause
#      first, stack frames including at least one in-app frame, platform unity,
#      the product's release and the os/device/gpu/runtime/app/game contexts.
#   4. When GLITCHPORT_E2E_HTTP_DSN is non-empty, the same player also delivers
#      to a real collector. When GLITCHPORT_E2E_ADMIN_URL and
#      GLITCHPORT_E2E_ADMIN_TOKEN are set as well, the script polls the
#      Management API until the collected issue appears; otherwise that stage
#      is skipped without failing.
#
# The Unity editor is resolved from UNITY_PATH when set, otherwise from the
# editor version recorded in sdk/unity/TestProject/ProjectSettings/ProjectVersion.txt
# (same rules as scripts/unity-test.sh).
#
# The check only runs on Windows Git Bash; other hosts, and Windows machines
# without the editor, skip with exit code 0. The temporary build output and logs
# are always removed, also on failure. The script itself adds no fixed ports:
# every collector address comes from the GLITCHPORT_E2E_* environment.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-unity.sh
#
# Optional environment variables:
#   GLITCHPORT_E2E_HTTP_DSN     HTTP relay DSN appended to the file relay DSN.
#                               When empty, the collector stage is skipped.
#   GLITCHPORT_E2E_ADMIN_URL    Management API base URL of that collector.
#   GLITCHPORT_E2E_ADMIN_TOKEN  Bearer token for the Management API.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) ;;
    *)
        echo "e2e-unity: the Unity e2e check requires Windows Git Bash (skipped)"
        exit 0
        ;;
esac

project_path="sdk/unity/TestProject"
project_version_file="$project_path/ProjectSettings/ProjectVersion.txt"
project_settings_file="$project_path/ProjectSettings/ProjectSettings.asset"
scene_path="Assets/E2E/E2EScene.unity"

if [ ! -f "$project_version_file" ]; then
    echo "e2e-unity: $project_version_file not found" >&2
    exit 1
fi

if [ ! -f "$project_path/$scene_path" ]; then
    echo "e2e-unity: $project_path/$scene_path not found" >&2
    exit 1
fi

editor_version="$(sed -n 's/^m_EditorVersion:[[:space:]]*//p' "$project_version_file" | tr -d '\r' | head -n 1)"
if [ -z "$editor_version" ]; then
    echo "e2e-unity: no m_EditorVersion in $project_version_file" >&2
    exit 1
fi

# The automatic release is "<productName>@<bundleVersion>"; read both from the
# project so the check keeps working when the test project is renamed or its
# version is bumped.
product_name="$(sed -n 's/^[[:space:]]*productName:[[:space:]]*//p' "$project_settings_file" 2>/dev/null | tr -d '\r' | sed 's/[[:space:]]*$//' | head -n 1)"
product_version="$(sed -n 's/^[[:space:]]*bundleVersion:[[:space:]]*//p' "$project_settings_file" 2>/dev/null | tr -d '\r' | sed 's/[[:space:]]*$//' | head -n 1)"
if [ -z "$product_name" ] || [ -z "$product_version" ]; then
    echo "e2e-unity: no productName/bundleVersion in $project_settings_file" >&2
    exit 1
fi
expected_release="$product_name@$product_version"

# Resolve the editor exactly like scripts/unity-test.sh: UNITY_PATH first, then
# the Unity Hub installation folders for the project's editor version.
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
    echo "e2e-unity: Unity $editor_version not found (skipped)"
    exit 0
fi

# Unity refuses to build a project that has no Assets folder. The committed
# test project contains the e2e assets, but a fresh checkout of an older tree
# may not; recreate the folder so the build fails on the real problem instead.
mkdir -p "$project_path/Assets"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/glitchport-unity-e2e.XXXXXX")"
player_pid=""

cleanup() {
    if [ -n "$player_pid" ] && kill -0 "$player_pid" 2>/dev/null; then
        kill "$player_pid" 2>/dev/null || true
    fi

    rm -rf "$tmp_dir" || true
}

trap cleanup EXIT

# Trim the optional collector settings the same way the relay parser does, so
# a value that only contains whitespace counts as unset.
trim() {
    printf '%s' "$1" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'
}

# Unity, the player and the file relay need native Windows paths; cygpath -m
# keeps them absolute with forward slashes, which the relay configuration
# parser and Unity both accept.
tmp_win="$(cygpath -m "$tmp_dir")"
build_output="$tmp_win/build/GlitchPortE2E.exe"
build_log="$tmp_dir/build.log"
player_log="$tmp_dir/player.log"
logs_dir="$tmp_dir/logs"

echo "e2e-unity: building the Windows development player (Unity $editor_version)"

if "$unity" \
    -batchmode \
    -nographics \
    -projectPath "$project_path" \
    -executeMethod GlitchPort.TestProject.Editor.BuildScript.BuildWindowsDevelopment \
    -buildOutput "$build_output" \
    -logFile "$(cygpath -m "$build_log")"; then
    build_status=0
else
    build_status=$?
fi

if [ "$build_status" -ne 0 ] || [ ! -f "$build_output" ]; then
    echo "e2e-unity: player build failed (Unity exited with $build_status)" >&2
    echo "e2e-unity: build log: $build_log" >&2
    if [ -f "$build_log" ]; then
        tail -n 120 "$build_log" >&2 || true
    fi
    exit 1
fi

mkdir -p "$logs_dir"

http_dsn="$(trim "${GLITCHPORT_E2E_HTTP_DSN:-}")"
player_dsn="file:?dir=$(cygpath -m "$logs_dir")"
if [ -n "$http_dsn" ]; then
    # Relay definitions are semicolon separated; the file relay stays first so
    # the local NDJSON assertions below never depend on the collector.
    player_dsn="$player_dsn;$http_dsn"
fi
timeout_seconds=120

echo "e2e-unity: running the player with GLITCHPORT_DSN=$player_dsn"

GLITCHPORT_DSN="$player_dsn" \
    "$build_output" \
    -batchmode \
    -nographics \
    -glitchport-e2e \
    -logFile "$(cygpath -m "$player_log")" &
player_pid=$!

deadline=$((SECONDS + timeout_seconds))
timed_out=0

while kill -0 "$player_pid" 2>/dev/null; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        timed_out=1
        kill "$player_pid" 2>/dev/null || true
        sleep 1
        kill -9 "$player_pid" 2>/dev/null || true
        break
    fi

    sleep 1
done

if wait "$player_pid" 2>/dev/null; then
    player_status=0
else
    player_status=$?
fi

shopt -s nullglob
ndjson_files=("$logs_dir"/glitchport-*.ndjson)
shopt -u nullglob

# Print the player log (and every captured event) before a failed run exits.
fail() {
    echo "e2e-unity: $*" >&2
    echo "e2e-unity: player log: $player_log" >&2

    if [ -f "$player_log" ]; then
        tail -n 120 "$player_log" >&2 || true
    fi

    if [ "${#ndjson_files[@]}" -gt 0 ]; then
        local file
        for file in "${ndjson_files[@]}"; do
            echo "e2e-unity: captured events in $file:" >&2
            cat "$file" >&2 || true
        done
    fi

    exit 1
}

if [ "$timed_out" -ne 0 ]; then
    fail "the player did not exit within ${timeout_seconds} seconds"
fi

if [ "$player_status" -ne 0 ]; then
    fail "the player exited with $player_status"
fi

if [ "${#ndjson_files[@]}" -eq 0 ]; then
    fail "no glitchport-*.ndjson file was written under $logs_dir"
fi

# Return 0 when the text contains every given literal substring.
contains_all() {
    local text="$1"
    shift
    local marker

    for marker in "$@"; do
        case "$text" in
            *"$marker"*) ;;
            *) return 1 ;;
        esac
    done

    return 0
}

matched_event_line=""

# Sets matched_event_line to the first event line that contains every given
# literal marker. Every NDJSON event is a single physical line, so checking one
# line at a time keeps fields of different events from satisfying a check
# together.
select_event_line() {
    local file
    local line

    matched_event_line=""

    for file in "${ndjson_files[@]}"; do
        while IFS= read -r line || [ -n "$line" ]; do
            if contains_all "$line" "$@"; then
                matched_event_line="$line"
                return 0
            fi
        done <"$file"
    done

    return 1
}

# Every event line is a single physical line; match both the message and the
# level on the same line so an event with the wrong level cannot pass. A
# stacked Error log is reported by Unity as an exception event whose synthetic
# LogError exception carries the text as its value, so accept that field too.
has_event() {
    local message="$1"
    local level="$2"
    local file

    for file in "${ndjson_files[@]}"; do
        if grep -F -e "\"message\":\"$message" -e "\"value\":\"$message" "$file" 2>/dev/null \
            | grep -F "\"level\":\"$level\"" 2>/dev/null \
            | grep -F -q "\"name\":\"glitchport.unity\"" 2>/dev/null; then
            return 0
        fi
    done

    return 1
}

if ! has_event "e2e unity error" "error"; then
    fail "no event with message/value 'e2e unity error' and level=error was captured"
fi

if ! has_event "e2e unity message" "warning"; then
    fail "no event with message/value 'e2e unity message' and level=warning was captured"
fi

# Select the event that Debug.LogException produced for the nested exception.
# Both exception types and both values make the selection unambiguous: the
# LogError event for "e2e unity error" does not contain either type.
if ! select_event_line \
    '"type":"ArgumentException"' \
    '"type":"InvalidOperationException"' \
    '"value":"e2e unity inner exception"' \
    '"value":"e2e unity exception"'; then
    fail "no event for the nested e2e unity exception was captured"
fi

nested_event_line="$matched_event_line"

missing_markers=()
for marker in \
    '"exception":[' \
    '"frames":[{' \
    '"in_app":true' \
    '"platform":"unity"' \
    "\"release\":\"$expected_release\"" \
    '"contexts":{' \
    '"os":{' \
    '"device":{' \
    '"gpu":{' \
    '"runtime":{"name":"Unity"' \
    '"app":{' \
    '"game":{'; do
    if ! contains_all "$nested_event_line" "$marker"; then
        missing_markers+=("$marker")
    fi
done

if [ "${#missing_markers[@]}" -gt 0 ]; then
    fail "the nested exception event is missing: ${missing_markers[*]}"
fi

# The serializer writes the exception array before the contexts, so this prefix
# holds the event metadata and the exception array only. Frames never serialize
# a "type" field and the contexts (the only other "type" key) are excluded, so
# every occurrence of "type":" is one exception object.
exception_prefix="${nested_event_line%%\"contexts\":*}"
exception_count="$(printf '%s' "$exception_prefix" | awk -F '"type":"' '{ print NF - 1 }')"
if [ "$exception_count" -ne 2 ]; then
    fail "the nested exception event has $exception_count exception objects instead of 2"
fi

# Exceptions are ordered from the innermost cause to the outermost exception;
# compare the byte positions on the same physical line so the inner
# ArgumentException must appear before the outer InvalidOperationException.
if ! printf '%s\n' "$nested_event_line" | awk \
    -v inner='"type":"ArgumentException"' \
    -v outer='"type":"InvalidOperationException"' '
        {
            inner_at = index($0, inner)
            outer_at = index($0, outer)
            exit (inner_at > 0 && outer_at > 0 && inner_at < outer_at) ? 0 : 1
        }'; then
    fail "the inner ArgumentException must appear before the outer InvalidOperationException"
fi

if [ -z "$http_dsn" ]; then
    echo "e2e-unity: GLITCHPORT_E2E_HTTP_DSN is not set; skipping the collector stage"
else
    admin_url="$(trim "${GLITCHPORT_E2E_ADMIN_URL:-}")"
    admin_token="$(trim "${GLITCHPORT_E2E_ADMIN_TOKEN:-}")"

    if [ -z "$admin_url" ] || [ -z "$admin_token" ]; then
        echo "e2e-unity: GLITCHPORT_E2E_ADMIN_URL and GLITCHPORT_E2E_ADMIN_TOKEN must both be set to check the collected issue (skipped)"
    else
        # The project id is the last path segment of the HTTP DSN, without any
        # query string and without trailing slashes.
        dsn_without_query="${http_dsn%%\?*}"
        while [ "$dsn_without_query" != "${dsn_without_query%/}" ]; do
            dsn_without_query="${dsn_without_query%/}"
        done
        project_id="${dsn_without_query##*/}"

        case "$project_id" in
            '' | *[!0-9]*)
                fail "could not derive a numeric project id from GLITCHPORT_E2E_HTTP_DSN"
                ;;
        esac

        while [ "$admin_url" != "${admin_url%/}" ]; do
            admin_url="${admin_url%/}"
        done

        issues_url="$admin_url/api/manage/projects/$project_id/issues?status=all&q=e2e%20unity%20exception&limit=25"
        echo "e2e-unity: waiting for the collected issue at $issues_url"

        issue_deadline=$((SECONDS + 30))
        last_response=""

        while :; do
            last_response="$(curl -sS -H "Authorization: Bearer $admin_token" "$issues_url" 2>/dev/null || true)"

            if printf '%s' "$last_response" | grep -F -q "e2e unity exception"; then
                # The issue id is the first field of the first item; the nested
                # label objects also have an id, so the pattern stops at the
                # first object boundary.
                issue_id="$(printf '%s' "$last_response" \
                    | sed -n -E 's/.*"items"[[:space:]]*:[[:space:]]*\[[[:space:]]*\{[^{}]*"id"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p' \
                    | head -n 1 || true)"

                if [ -z "$issue_id" ]; then
                    echo "e2e-unity: GET $issues_url returned:" >&2
                    printf '%s\n' "$last_response" >&2
                    fail "the collector response contained the exception but no issue id"
                fi

                echo "e2e-unity: collector issue $issue_id matches 'e2e unity exception'"
                break
            fi

            if [ "$SECONDS" -ge "$issue_deadline" ]; then
                echo "e2e-unity: GET $issues_url did not return the issue within 30s; last response:" >&2
                printf '%s\n' "${last_response:-<no response>}" >&2
                fail "the collected issue did not appear within 30s"
            fi

            sleep 1
        done
    fi
fi

echo "e2e-unity: ok"
