#!/usr/bin/env bash
# Installs UrbanBridge into the current user's Rhino 8 plug-ins folder.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
DEFAULT_SOURCE="$REPOSITORY_ROOT/rhino-plugin/bin/Release/net7.0/package/UrbanBridgePlugin"
SOURCE_DIR="${1:-$DEFAULT_SOURCE}"
TARGET_DIR="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/UrbanBridgePlugin"

# A source checkout does not contain compiled binaries. Build them automatically when possible.
if [[ ! -f "$SOURCE_DIR/UrbanBridgePlugin.rhp" && "$SOURCE_DIR" == "$DEFAULT_SOURCE" ]]; then
  if command -v dotnet >/dev/null 2>&1; then
    echo "UrbanBridge bundle not found; building the Release bundle..."
    dotnet build "$REPOSITORY_ROOT/rhino-plugin/UrbanBridgePlugin.csproj" -c Release
  else
    echo "UrbanBridgePlugin.rhp was not found in: $SOURCE_DIR" >&2
    echo "Install .NET SDK 7.x and run this installer again, or pass the folder containing UrbanBridgePlugin.rhp as its first argument." >&2
    exit 1
  fi
fi

if [[ ! -f "$SOURCE_DIR/UrbanBridgePlugin.rhp" ]]; then
  echo "UrbanBridgePlugin.rhp was not found in: $SOURCE_DIR" >&2
  echo "Pass the folder that contains UrbanBridgePlugin.rhp, not the .rhp file itself." >&2
  exit 1
fi

mkdir -p "$(dirname "$TARGET_DIR")"
rm -rf "$TARGET_DIR"
cp -R "$SOURCE_DIR" "$TARGET_DIR"
echo "UrbanBridge was installed to: $TARGET_DIR"
echo "Restart Rhino, then run UrbanBridgeStatus in the Rhino command line."
