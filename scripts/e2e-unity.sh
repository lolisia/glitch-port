#!/usr/bin/env bash
#
# End-to-end check for the Unity SDK package (sdk/unity/com.glitchport.unity).
#
#   1. Build sdk/unity/TestProject as a Windows 64-bit development player
#      (Mono backend) that contains Assets/E2E/E2EScene.unity.
#   2. Run the player headlessly with GLITCHPORT_DSN=file:?dir=<tmp>/logs and
#      -glitchport-e2e. E2EDriver proves automatic initialization (it never
#      calls Init), captures "e2e unity error" through the Unity log bridge and
#      "e2e unity message" through the manual API, flushes every relay and exits
#      with 0 only when both captures and the flush succeeded.
#   3. Assert the NDJSON file under <tmp>/logs contains the error event with
#      level=error, the warning event with level=warning and the
#      name=glitchport.unity SDK name.
#
# The Unity editor is resolved from UNITY_PATH when set, otherwise from the
# editor version recorded in sdk/unity/TestProject/ProjectSettings/ProjectVersion.txt
# (same rules as scripts/unity-test.sh).
#
# The check only runs on Windows Git Bash; other hosts, and Windows machines
# without the editor, skip with exit code 0. The temporary build output and logs
# are always removed, also on failure.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/e2e-unity.sh

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

player_dsn="file:?dir=$(cygpath -m "$logs_dir")"
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

# Every event line is a single physical line; match both the message and the
# level on the same line so an event with the wrong level cannot pass.
has_event() {
    local message="$1"
    local level="$2"
    local file

    for file in "${ndjson_files[@]}"; do
        if grep -F "\"message\":\"$message" "$file" 2>/dev/null \
            | grep -F "\"level\":\"$level\"" 2>/dev/null \
            | grep -F -q "\"name\":\"glitchport.unity\"" 2>/dev/null; then
            return 0
        fi
    done

    return 1
}

if ! has_event "e2e unity error" "error"; then
    fail "no event with message 'e2e unity error' and level=error was captured"
fi

if ! has_event "e2e unity message" "warning"; then
    fail "no event with message 'e2e unity message' and level=warning was captured"
fi

echo "e2e-unity: ok"
