#!/usr/bin/env bash
#
# Decompile the game's sts2.dll into decompiled/<version>/ (gitignored), so we can
# keep per-version C# source side by side and diff how internals change across builds
# (e.g. the map-generation RNG/algorithm). Uses ilspycmd to decompile the dll.
#
# Old builds: download them with Steam's `download_depot 2868840 <depotID> <manifestID>`
# console command (manifest IDs are on SteamDB), then point --dll at the depot's sts2.dll.
#
# This script is inspired by: https://github.com/elliotttate/sts2-modding-mcp
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DEFAULT_DLL="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64/sts2.dll"
GODOT_LOG="$HOME/Library/Application Support/SlayTheSpire2/logs/godot.log"

DLL="$DEFAULT_DLL"
DLL_EXPLICIT=0
VERSION=""
FORCE=0

usage() {
  cat <<EOF
Usage: scripts/decompile-game.sh [--version vX.Y.Z] [--dll <path-to-sts2.dll>] [--force]

Decompiles sts2.dll into <repo>/decompiled/<version>/ (gitignored) via ilspycmd.

  --version vX.Y.Z  Override the output-folder label. Normally unnecessary: the
                    version is read from the build's own release_info.json (and
                    falls back to godot.log for the installed game).
  --dll <path>      Path to sts2.dll. Default: the installed macOS arm64 game.
  --force           Re-decompile even if decompiled/<version>/ already exists.

Examples:
  scripts/decompile-game.sh                       # current install
  scripts/decompile-game.sh \\                     # an old build downloaded via download_depot
    --dll "\$HOME/Library/Application Support/Steam/.../depot_<id>/.../data_sts2_macos_arm64/sts2.dll"
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="${2:-}"; shift 2 ;;
    --dll)     DLL="${2:-}"; DLL_EXPLICIT=1; shift 2 ;;
    --force)   FORCE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown argument '$1'" >&2; usage; exit 1 ;;
  esac
done

# Resolve ilspycmd (global dotnet tools are often not on PATH).
ILSPY="$(command -v ilspycmd || true)"
if [ -z "$ILSPY" ] && [ -x "$HOME/.dotnet/tools/ilspycmd" ]; then
  ILSPY="$HOME/.dotnet/tools/ilspycmd"
fi
if [ -z "$ILSPY" ]; then
  echo "error: ilspycmd not found. Install it with: dotnet tool install -g ilspycmd" >&2
  echo "       (it targets .NET 8; install the .NET 8 runtime if decompilation fails)" >&2
  exit 1
fi

if [ ! -f "$DLL" ]; then
  echo "error: sts2.dll not found at: $DLL" >&2
  echo "       Pass --dll <path> (e.g. a downloaded old depot)." >&2
  exit 1
fi

# The build ships its authoritative version in release_info.json, next to the app's
# Resources (one level up from the data_sts2_* dll dir). Always prefer it — it's the
# only reliable label for an arbitrary dll (a downloaded old depot has no other marker).
RELEASE_INFO="$(dirname "$DLL")/../release_info.json"
DETECTED=""
if [ -f "$RELEASE_INFO" ]; then
  DETECTED="$(grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]*"' "$RELEASE_INFO" | head -1 | grep -oE 'v[0-9]+\.[0-9]+\.[0-9]+' || true)"
fi
if [ -n "$DETECTED" ]; then
  if [ -n "$VERSION" ] && [ "$VERSION" != "$DETECTED" ]; then
    echo "warning: --version '$VERSION' disagrees with release_info.json ('$DETECTED'); using $DETECTED" >&2
  fi
  VERSION="$DETECTED"
  echo "Version $VERSION (from release_info.json)"
fi

# Fallback only when release_info.json is missing: the installed game's godot.log.
if [ -z "$VERSION" ] && [ "$DLL_EXPLICIT" -eq 0 ] && [ -f "$GODOT_LOG" ]; then
  VERSION="$(grep -oE 'release=v[0-9]+\.[0-9]+\.[0-9]+' "$GODOT_LOG" | tail -1 | cut -d= -f2 || true)"
  [ -n "$VERSION" ] && echo "Version $VERSION (from godot.log; no release_info.json found)"
fi
if [ -z "$VERSION" ]; then
  echo "error: could not determine version (no release_info.json next to the dll); pass --version vX.Y.Z" >&2
  exit 1
fi

OUT="$REPO_ROOT/decompiled/$VERSION"
if [ -d "$OUT" ] && [ "$FORCE" -ne 1 ]; then
  echo "decompiled/$VERSION already exists — use --force to overwrite."
  exit 0
fi

echo "Decompiling: $DLL"
echo "        ->   decompiled/$VERSION"
rm -rf "$OUT"
mkdir -p "$OUT"
"$ILSPY" -p -o "$OUT" "$DLL"

# The game ships its XML doc-comments next to the dll; keep them alongside the source
# (CLAUDE.md uses the XML for intent/contracts). Best-effort.
XML="$(dirname "$DLL")/sts2.xml"
[ -f "$XML" ] && cp "$XML" "$OUT/sts2.xml" && echo "Copied sts2.xml alongside the source."

echo "Done: decompiled/$VERSION ($(find "$OUT" -name '*.cs' | wc -l | tr -d ' ') .cs files)"
