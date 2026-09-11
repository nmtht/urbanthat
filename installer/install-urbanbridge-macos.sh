#!/usr/bin/env bash
# Installs a previously built UrbanBridge bundle into the current user's Rhino 8 plug-ins folder.
set -euo pipefail
SOURCE_DIR="${1:-$(cd "$(dirname "$0")/../rhino-plugin/bin/Release/net7.0/package/UrbanBridgePlugin" && pwd)}"
TARGET_DIR="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/UrbanBridgePlugin"

if [[ ! -f "$SOURCE_DIR/UrbanBridgePlugin.rhp" ]]; then
  echo "UrbanBridgePlugin.rhp was not found in: $SOURCE_DIR" >&2
  echo "Build the Release bundle first: dotnet build rhino-plugin/UrbanBridgePlugin.csproj -c Release" >&2
  exit 1
fi
mkdir -p "$(dirname "$TARGET_DIR")"
rm -rf "$TARGET_DIR"
cp -R "$SOURCE_DIR" "$TARGET_DIR"
echo "UrbanBridge was installed to: $TARGET_DIR"
echo "Restart Rhino, then run UrbanBridgeStatus in the Rhino command line."
