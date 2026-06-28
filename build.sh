#!/usr/bin/env bash
# Build and package the More Like This Enhanced plugin for Jellyfin.
#
# Usage:
#   ./build.sh               # builds Release, creates dist/ zip
#   ./build.sh --install     # also copies plugin to local Jellyfin data dir
#
# Requirements: .NET 9 SDK  (https://dot.net/download) — Jellyfin 10.11 targets .NET 9

set -euo pipefail

PLUGIN_NAME="BetterRecs"
VERSION="2.0.0.0"
OUT_DIR="dist/${PLUGIN_NAME}"
ZIP_NAME="${PLUGIN_NAME}_${VERSION}.zip"

echo "==> Building ${PLUGIN_NAME} v${VERSION}..."
dotnet build Jellyfin.Plugin.BetterRecs.csproj -c Release --nologo -o "${OUT_DIR}"

# Copy plugin manifest.
cp meta.json "${OUT_DIR}/meta.json"

# Remove xml doc / pdb files that aren't needed at runtime.
find "${OUT_DIR}" -name "*.pdb" -delete
find "${OUT_DIR}" -name "*.xml" -not -name "meta.json" -delete 2>/dev/null || true

echo "==> Packaging into ${ZIP_NAME}..."
(cd dist && zip -r "../${ZIP_NAME}" "${PLUGIN_NAME}/")

echo "==> Done: ${ZIP_NAME}"
echo ""
echo "To install manually:"
echo "  1. Copy the dist/${PLUGIN_NAME}/ folder into your Jellyfin plugins directory."
echo "     - Native install: ~/.local/share/jellyfin/plugins/ or /var/lib/jellyfin/plugins/"
echo "     - Docker / Unraid:  <jellyfin appdata>/plugins/${PLUGIN_NAME}/"
echo "       (e.g. /mnt/user/appdata/jellyfin/plugins/ , i.e. /config/plugins/ inside the container)"
echo "  2. Restart the Jellyfin server."
echo "  3. Open Dashboard → Plugins to confirm it loaded, then configure it."
echo "  4. Watch the server log for: 'BetterRecs: similar-items interceptor inserted'"

if [[ "${1:-}" == "--install" ]]; then
  # Attempt to find a local Jellyfin data directory.
  JELLYFIN_DATA="${JELLYFIN_DATA_DIR:-${HOME}/.local/share/jellyfin}"
  PLUGIN_DIR="${JELLYFIN_DATA}/plugins/${PLUGIN_NAME}"
  echo ""
  echo "==> Installing to ${PLUGIN_DIR}..."
  mkdir -p "${PLUGIN_DIR}"
  cp -r "${OUT_DIR}/." "${PLUGIN_DIR}/"
  echo "    Restart Jellyfin to load the updated plugin."
fi
