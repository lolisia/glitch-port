#!/usr/bin/env bash
#
# Run the Unity package tests (EditMode and PlayMode) in batch mode.
#
# The Unity editor is resolved from UNITY_PATH when set, otherwise from the
# editor version recorded in sdk/unity/TestProject/ProjectSettings/ProjectVersion.txt:
#
#   Windows  C:/Program Files/Unity/Hub/Editor/<ver>/Editor/Unity.exe
#   macOS    /Applications/Unity/Hub/Editor/<ver>/Unity.app/Contents/MacOS/Unity
#   Linux    $HOME/Unity/Hub/Editor/<ver>/Editor/Unity
#
# When that editor is not installed the tests are skipped with exit code 0, so
# the repository verification works on machines without Unity. Otherwise both
# test platforms must exit 0 and their NUnit result document must report
# result="Passed"; on failure the log path is printed and the script exits
# non-zero.
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/unity-test.sh
#
# Windows Git Bash, macOS and Linux compatible.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

project_path="sdk/unity/TestProject"
project_version_file="$project_path/ProjectSettings/ProjectVersion.txt"
logs_dir="$project_path/Logs"

if [ ! -f "$project_version_file" ]; then
    echo "unity-test: $project_version_file not found" >&2
    exit 1
fi

editor_version="$(sed -n 's/^m_EditorVersion:[[:space:]]*//p' "$project_version_file" | tr -d '\r' | head -n 1)"
if [ -z "$editor_version" ]; then
    echo "unity-test: no m_EditorVersion in $project_version_file" >&2
    exit 1
fi

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
    echo "unity-test: Unity $editor_version not found (skipped)"
    exit 0
fi

# Unity refuses to open a project that has no Assets folder. The committed test
# project contains only Packages/ and ProjectSettings/ (git does not track empty
# directories), so recreate the empty folder on fresh checkouts. Assets/ is not
# ignored, so e2e assets committed there by later work survive a re-run.
mkdir -p "$project_path/Assets"
mkdir -p "$logs_dir"

# The NUnit XML written by Unity has a single <test-run> root element; only a
# root result="Passed" counts as success (a zero exit code alone is not enough).
results_passed() {
    local results_file="$1"

    [ -f "$results_file" ] || return 1
    grep -m 1 -o '<test-run[^>]*result="Passed"' "$results_file" >/dev/null 2>&1
}

for platform in EditMode PlayMode; do
    # Unity resolves a relative -testResults path against the project folder,
    # not the current directory, so use absolute paths for both files.
    results_file="$repo_root/$logs_dir/$platform-results.xml"
    log_file="$repo_root/$logs_dir/$platform.log"

    # Unity may leave an old result document behind when it fails before the
    # test run starts; never evaluate a stale file.
    rm -f "$results_file"

    echo "unity-test: running $platform tests (Unity $editor_version)"

    if "$unity" \
        -batchmode \
        -nographics \
        -projectPath "$project_path" \
        -runTests \
        -testPlatform "$platform" \
        -testResults "$results_file" \
        -logFile "$log_file"; then
        unity_status=0
    else
        unity_status=$?
    fi

    if [ "$unity_status" -ne 0 ]; then
        echo "unity-test: $platform tests failed (Unity exited with $unity_status); see $log_file" >&2
        exit 1
    fi

    if ! results_passed "$results_file"; then
        echo "unity-test: $platform tests did not report result=\"Passed\"; see $log_file" >&2
        exit 1
    fi

    echo "unity-test: $platform tests passed"
done

echo "unity-test: ok"
