#!/usr/bin/env bash
# One-shot: all C# + csproj → namespace UrbanBridge.Plugin (eliminates CS0246).
set -euo pipefail
cd "$(dirname "$0")"

echo "=== Before ==="
grep -Rhn "namespace UrbanBridge" --include='*.cs' . 2>/dev/null | sed 's/.*namespace /  /' | sort | uniq -c || true

if sed --version >/dev/null 2>&1; then
  # GNU sed
  find . -name '*.cs' -print0 | xargs -0 sed -i 's/namespace UrbanBridge\.Rhino/namespace UrbanBridge.Plugin/g'
  sed -i 's/RootNamespace>UrbanBridge\.Rhino</RootNamespace>UrbanBridge.Plugin</g' UrbanBridgePlugin.csproj 2>/dev/null || true
else
  # BSD sed (macOS)
  find . -name '*.cs' -print0 | xargs -0 sed -i '' 's/namespace UrbanBridge\.Rhino/namespace UrbanBridge.Plugin/g'
  sed -i '' 's/RootNamespace>UrbanBridge\.Rhino</RootNamespace>UrbanBridge.Plugin</g' UrbanBridgePlugin.csproj 2>/dev/null || true
fi

echo "=== After ==="
grep -Rhn "namespace UrbanBridge" --include='*.cs' . 2>/dev/null | sed 's/.*namespace /  /' | sort | uniq -c || true

if grep -R "UrbanBridge\.Rhino" --include='*.cs' --include='*.csproj' . 2>/dev/null; then
  echo "ERROR: UrbanBridge.Rhino still present"
  exit 1
fi
echo "OK: every file uses UrbanBridge.Plugin"
