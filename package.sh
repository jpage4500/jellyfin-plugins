#!/usr/bin/env bash
#
# Packages a plugin the way Jellyfin's "New Repository" flow expects: a zip of the built
# assembly, plus an entry in manifest.json describing where that zip lives and what it
# hashes to. Jellyfin refuses an install whose checksum does not match, so the two are
# generated together here rather than maintained by hand.
#
#   ./package.sh 1.0.1.0
#   CHANGELOG="Fixed the thing" ./package.sh 1.0.1.0
#
# Then: upload dist/<id>_<version>.zip to a GitHub release tagged v<version>, commit
# manifest.json, and point the server at the raw manifest URL printed at the end.

set -euo pipefail

PROJECT_DIR="jellyfin-playlist"
PROJECT="${PROJECT_DIR}/jellyfin-playlist.csproj"
ASSEMBLY="jellyfin-playlist.dll"
FRAMEWORK="net10.0"

ID="favorites-exporter"
NAME="Favorites Exporter"
GUID="f9b7b8d4-8d9e-4b3a-9a2f-3d5c6e8a1b2c"
CATEGORY="General"
DESCRIPTION="Exports favorited music to portable .m3u playlists, and replays those favorites onto another server."
OVERVIEW="Back up and move your music favorites"

# The MINIMUM server version this build runs on. Jellyfin hides any version whose targetAbi
# is newer than the server asking, so raising this drops older servers -- but setting it below
# a version you have actually run on lets the plugin install and then fail at load. It is not
# the plugin's own version. 13.0.0.0 is what the dev container runs; the plugin builds against
# the 12.0.0 unstable Controller package, so lowering it is plausible but untested.
TARGET_ABI="13.0.0.0"

VERSION="${1:-}"
if [[ -z "${VERSION}" ]]; then
    echo "usage: ./package.sh <version>   e.g. ./package.sh 1.0.0.0" >&2
    exit 1
fi
if [[ ! "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "version must be four numeric parts, e.g. 1.0.0.0 (Jellyfin parses it as System.Version)" >&2
    exit 1
fi

cd "$(dirname "$0")"

SLUG="$(git remote get-url origin | sed -E 's#(git@github\.com:|https://github\.com/)##; s#\.git$##')"
OWNER="${SLUG%%/*}"
TAG="v${VERSION}"
ZIP_NAME="${ID}_${VERSION}.zip"
SOURCE_URL="https://github.com/${SLUG}/releases/download/${TAG}/${ZIP_NAME}"
MANIFEST_URL="https://raw.githubusercontent.com/${SLUG}/$(git symbolic-ref --short HEAD)/manifest.json"
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo "==> building ${NAME} ${VERSION}"
dotnet build "${PROJECT}" --configuration Release --nologo \
    -p:Version="${VERSION}" -p:AssemblyVersion="${VERSION}" -p:FileVersion="${VERSION}" \
    | tail -3

STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT

# Jellyfin unpacks the zip straight into the plugin folder, so the assembly must sit at the
# root of the archive, not inside a directory.
cp "${PROJECT_DIR}/bin/Release/${FRAMEWORK}/${ASSEMBLY}" "${STAGE}/"

python3 - "${STAGE}/meta.json" <<PY
import json, sys
json.dump({
    "category": "${CATEGORY}",
    "changelog": """${CHANGELOG:-}""",
    "description": "${DESCRIPTION}",
    "guid": "${GUID}",
    "name": "${NAME}",
    "overview": "${OVERVIEW}",
    "owner": "${OWNER}",
    "targetAbi": "${TARGET_ABI}",
    "timestamp": "${TIMESTAMP}",
    "version": "${VERSION}",
    "status": "Active",
    "autoUpdate": True,
    "assemblies": ["${ASSEMBLY}"],
}, open(sys.argv[1], "w"), indent=2)
PY

mkdir -p dist
ZIP_PATH="${PWD}/dist/${ZIP_NAME}"
rm -f "${ZIP_PATH}"
(cd "${STAGE}" && zip -q -X -r "${ZIP_PATH}" .)

if command -v md5sum >/dev/null; then
    CHECKSUM="$(md5sum "${ZIP_PATH}" | cut -d' ' -f1)"
else
    CHECKSUM="$(md5 -q "${ZIP_PATH}")"
fi

echo "==> updating manifest.json"
VERSION="${VERSION}" CHANGELOG="${CHANGELOG:-}" TARGET_ABI="${TARGET_ABI}" \
SOURCE_URL="${SOURCE_URL}" CHECKSUM="${CHECKSUM}" TIMESTAMP="${TIMESTAMP}" \
GUID="${GUID}" NAME="${NAME}" DESCRIPTION="${DESCRIPTION}" OVERVIEW="${OVERVIEW}" \
OWNER="${OWNER}" CATEGORY="${CATEGORY}" python3 - <<'PY'
import json, os, pathlib

manifest_path = pathlib.Path("manifest.json")
manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else []

plugin = next((p for p in manifest if p["guid"] == os.environ["GUID"]), None)
if plugin is None:
    plugin = {"guid": os.environ["GUID"], "versions": []}
    manifest.append(plugin)

plugin.update({
    "name": os.environ["NAME"],
    "description": os.environ["DESCRIPTION"],
    "overview": os.environ["OVERVIEW"],
    "owner": os.environ["OWNER"],
    "category": os.environ["CATEGORY"],
})

entry = {
    "version": os.environ["VERSION"],
    "changelog": os.environ["CHANGELOG"],
    "targetAbi": os.environ["TARGET_ABI"],
    "sourceUrl": os.environ["SOURCE_URL"],
    "checksum": os.environ["CHECKSUM"],
    "timestamp": os.environ["TIMESTAMP"],
}

# Newest first, and re-running for the same version replaces it rather than duplicating.
plugin["versions"] = [entry] + [v for v in plugin["versions"] if v["version"] != entry["version"]]

# Key order matches the official repo manifest so diffs against it stay readable.
ordered = [{k: p[k] for k in ("guid", "name", "description", "overview", "owner", "category", "versions")}
           for p in manifest]
manifest_path.write_text(json.dumps(ordered, indent=4) + "\n")
PY

cat <<EOF

  zip       dist/${ZIP_NAME}
  checksum  ${CHECKSUM}
  targetAbi ${TARGET_ABI}  (minimum server version)

Next:

  gh release create ${TAG} "dist/${ZIP_NAME}" --title "${NAME} ${VERSION}" --notes "${CHANGELOG:-Release ${VERSION}}"
  git add manifest.json && git commit -m "Release ${VERSION}" && git push

Then in Jellyfin: Dashboard -> Plugins -> Repositories -> + and paste

  ${MANIFEST_URL}

EOF
