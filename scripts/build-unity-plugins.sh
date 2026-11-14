#!/usr/bin/env bash
#
# Build the precompiled plugins that ship inside the Unity package
# (sdk/unity/com.glitchport.unity).
#
#   1. dotnet build GlitchPort.Core in Release with an XML documentation file
#   2. copy GlitchPort.Core.dll and GlitchPort.Core.xml to Runtime/Plugins/
#   3. write a SHA-256 over the sorted, LF-normalized Core sources to
#      Runtime/Plugins/GlitchPort.Core.sources.sha256
#   4. on Windows, publish glitchport-terminal as a native win-x64 executable
#      into Tools~/win-x64/ (native AOT, falling back to a self-contained
#      single-file build when AOT is unavailable)
#
# Usage (from the repository root, or from anywhere else):
#   bash scripts/build-unity-plugins.sh          # build and refresh the plugins
#   bash scripts/build-unity-plugins.sh --check  # verify the source hash only
#
# --check never builds anything. It recomputes the hash and exits non-zero when
# the checked-in hash is missing or stale, telling the caller to run the build.
#
# Windows Git Bash and macOS/Linux compatible (the terminal binary is only
# published on Windows; other hosts build the managed plugin only).

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

package_dir="sdk/unity/com.glitchport.unity"
plugins_dir="$package_dir/Runtime/Plugins"
core_project="sdk/dotnet/src/GlitchPort.Core/GlitchPort.Core.csproj"
core_source_dir="sdk/dotnet/src/GlitchPort.Core"
core_dll_name="GlitchPort.Core.dll"
core_xml_name="GlitchPort.Core.xml"
hash_name="GlitchPort.Core.sources.sha256"
terminal_project="sdk/dotnet/tools/GlitchPort.Terminal"

usage() {
    cat <<'EOF'
usage: build-unity-plugins.sh [--check]

  (no arguments)  build GlitchPort.Core, copy it into the Unity package and
                  publish the Windows terminal binary
  --check         verify Runtime/Plugins/GlitchPort.Core.sources.sha256
                  against the current Core sources without building
EOF
}

check_only=0
for argument in "$@"; do
    case "$argument" in
        --check)
            check_only=1
            ;;
        -h | --help)
            usage
            exit 0
            ;;
        *)
            echo "build-unity-plugins: unknown argument: $argument" >&2
            usage >&2
            exit 2
            ;;
    esac
done

# Hash stdin with sha256sum, falling back to the macOS shasum. Only the hex
# digest is printed.
sha256_hex() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum | awk '{ print $1 }'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 | awk '{ print $1 }'
    else
        echo "build-unity-plugins: sha256sum or shasum is required" >&2
        return 1
    fi
}

# Hash the sorted relative paths and LF-normalized contents of every Core
# source file (all *.cs recursively plus the project file). Paths are relative
# to the repository root so the digest is independent of the checkout location.
compute_sources_hash() {
    (
        cd "$repo_root"

        find "$core_source_dir" -type f \( -name '*.cs' -o -name '*.csproj' \) \
            -not -path '*/bin/*' -not -path '*/obj/*' -print \
            | LC_ALL=C sort \
            | while IFS= read -r source; do
                printf '%s\n' "$source"
                sed 's/\r$//' "$source"
            done \
            | sha256_hex
    )
}

hash_file="$plugins_dir/$hash_name"
expected_hash="$(compute_sources_hash)"

if [ "$check_only" -eq 1 ]; then
    if [ ! -f "$hash_file" ]; then
        echo "build-unity-plugins: $hash_file is missing; run scripts/build-unity-plugins.sh" >&2
        exit 1
    fi

    recorded_hash="$(tr -d '\r\n' < "$hash_file")"
    if [ "$recorded_hash" != "$expected_hash" ]; then
        echo "build-unity-plugins: GlitchPort.Core sources changed; run scripts/build-unity-plugins.sh" >&2
        exit 1
    fi

    echo "build-unity-plugins: GlitchPort.Core plugins are up to date"
    exit 0
fi

echo "build-unity-plugins: dotnet build -c Release $core_project"
dotnet build -c Release \
    -p:GenerateDocumentationFile=true \
    -p:NoWarn=1591 \
    "$core_project"

build_output_dir="$core_source_dir/bin/Release"
core_dll="$build_output_dir/netstandard2.1/$core_dll_name"
core_xml="$build_output_dir/netstandard2.1/$core_xml_name"

if [ ! -f "$core_dll" ]; then
    core_dll="$(find "$build_output_dir" -type f -name "$core_dll_name" -print -quit 2>/dev/null || true)"
fi
if [ ! -f "$core_xml" ]; then
    core_xml="$(find "$build_output_dir" -type f -name "$core_xml_name" -print -quit 2>/dev/null || true)"
fi

if [ -z "$core_dll" ] || [ ! -f "$core_dll" ]; then
    echo "build-unity-plugins: could not find $core_dll_name under $build_output_dir" >&2
    exit 1
fi
if [ -z "$core_xml" ] || [ ! -f "$core_xml" ]; then
    echo "build-unity-plugins: could not find $core_xml_name; is GenerateDocumentationFile enabled?" >&2
    exit 1
fi

mkdir -p "$plugins_dir"
cp "$core_dll" "$plugins_dir/$core_dll_name"
cp "$core_xml" "$plugins_dir/$core_xml_name"
echo "build-unity-plugins: wrote $plugins_dir/$core_dll_name and $core_xml_name"

printf '%s' "$expected_hash" > "$hash_file"
echo "build-unity-plugins: wrote $hash_file"

# The terminal relay binary is Windows-only for now; it is distributed as a
# Unity plugin next to the Core assembly and must stay out of Unity's asset
# importer, hence the trailing "~" in Tools~.
publish_windows_terminal() {
    local rid="win-x64"
    local exe_name="glitchport-terminal.exe"
    local tools_dir="$package_dir/Tools~/$rid"
    local staging

    staging="$(mktemp -d "${TMPDIR:-/tmp}/glitchport-unity-terminal.XXXXXX")"

    echo "build-unity-plugins: dotnet publish -c Release -r $rid $terminal_project"
    if ! dotnet publish -c Release -r "$rid" "$terminal_project" -o "$staging"; then
        echo "build-unity-plugins: warning: native AOT publish failed; retrying with PublishAot=false, self-contained single-file" >&2
        rm -rf "$staging"
        mkdir -p "$staging"

        if ! dotnet publish -c Release -r "$rid" \
            -p:PublishAot=false \
            --self-contained true \
            -p:PublishSingleFile=true \
            "$terminal_project" -o "$staging"; then
            rm -rf "$staging"
            echo "build-unity-plugins: could not publish $terminal_project" >&2
            return 1
        fi
    fi

    if [ ! -f "$staging/$exe_name" ]; then
        rm -rf "$staging"
        echo "build-unity-plugins: dotnet publish did not produce $exe_name" >&2
        return 1
    fi

    mkdir -p "$tools_dir"
    cp "$staging/$exe_name" "$tools_dir/$exe_name"
    rm -rf "$staging"
    echo "build-unity-plugins: wrote $tools_dir/$exe_name"
}

case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
        publish_windows_terminal
        ;;
    *)
        echo "build-unity-plugins: terminal binary is only published on Windows (skipped)"
        ;;
esac

echo "build-unity-plugins: ok"
