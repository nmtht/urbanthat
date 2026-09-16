#!/usr/bin/env bash
# Unify all C# files to namespace UrbanBridge.Plugin (fixes CS0246 after partial renames).
set -euo pipefail
cd "$(dirname "$0")"

echo "Before:"
grep -R "namespace UrbanBridge" --include='*.cs' . | sed 's/.*namespace /  /' | sort | uniq -c || true

# macOS sed requires '' after -i; GNU sed does not — handle both
if sed --version >/dev/null 2>&1; then
  find . -name '*.cs' -print0 | xargs -0 sed -i 's/namespace UrbanBridge\.Rhino/namespace UrbanBridge.Plugin/g'
  sed -i 's/RootNamespace>UrbanBridge\.Rhino</RootNamespace>UrbanBridge.Plugin</g' UrbanBridgePlugin.csproj 2>/dev/null || true
else
  find . -name '*.cs' -print0 | xargs -0 sed -i '' 's/namespace UrbanBridge\.Rhino/namespace UrbanBridge.Plugin/g'
  sed -i '' 's/RootNamespace>UrbanBridge\.Rhino</RootNamespace>UrbanBridge.Plugin</g' UrbanBridgePlugin.csproj 2>/dev/null || true
fi

echo "After:"
grep -R "namespace UrbanBridge" --include='*.cs' . | sed 's/.*namespace /  /' | sort | uniq -c

if grep -R "UrbanBridge\.Rhino" --include='*.cs' --include='*.csproj' . ; then
  echo "ERROR: still have UrbanBridge.Rhino"
  exit 1
fi
echo "OK: all files use UrbanBridge.Plugin"
