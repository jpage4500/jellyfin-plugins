#!/usr/bin/env bash
#
# Packages one plugin the way Jellyfin's "New Repository" flow expects: a zip of the built
# assembly, plus an entry in manifest.json describing where that zip lives and what it hashes
# to. Jellyfin refuses an install whose checksum does not match, so the two are generated
# together here rather than maintained by hand.
#
#   ./package.sh jellyfin-playlist 1.0.1.0
#   CHANGELOG="Fixed the thing" ./package.sh jellyfin-playlist 1.0.1.0
#
# Everything plugin-specific comes from <plugin-dir>/plugin.json; this script holds no
# knowledge of any particular plugin. manifest.json stays at the repo root because a Jellyfin
# repository is a single JSON array listing every plugin behind one URL.
#
# Normally driven by ./release.sh, which also tags, publishes and pushes.

set -euo pipefail

cd "$(dirname "$0")"

PLUGIN_DIR="${1:-}"
VERSION="${2:-}"

if [[ -z "${PLUGIN_DIR}" || -z "${VERSION}" ]]; then
    echo "usage: ./package.sh <plugin-dir> <version>   e.g. ./package.sh jellyfin-playlist 1.0.1.0" >&2
    exit 1
fi

PLUGIN_DIR="${PLUGIN_DIR%/}"
PLUGIN_JSON="${PLUGIN_DIR}/plugin.json"
[[ -f "${PLUGIN_JSON}" ]] || { echo "no ${PLUGIN_JSON} -- is ${PLUGIN_DIR} a plugin?" >&2; exit 1; }

if [[ ! "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "version must be four numeric parts, e.g. 1.0.1.0 (Jellyfin parses it as System.Version)" >&2
    exit 1
fi

field() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "${PLUGIN_JSON}" "$1"; }

ID="$(field id)"
NAME="$(field name)"
GUID="$(field guid)"
CATEGORY="$(field category)"
DESCRIPTION="$(field description)"
OVERVIEW="$(field overview)"
PROJECT="$(field project)"
ASSEMBLY="$(field assembly)"
FRAMEWORK="$(field framework)"
# The MINIMUM server version this build runs on. Jellyfin hides any version whose targetAbi is
# newer than the server asking, so raising it drops older servers -- but setting it below a
# version you have actually run on lets the plugin install and then fail at load.
TARGET_ABI="$(field targetAbi)"

SLUG="$(git remote get-url origin | sed -E 's#(git@github\.com:|https://github\.com/)##; s#\.git$##')"
OWNER="${SLUG%%/*}"
# Tags carry the plugin id so two plugins in this repo can release independently.
TAG="${ID}-v${VERSION}"
ZIP_NAME="${ID}_${VERSION}.zip"
SOURCE_URL="https://github.com/${SLUG}/releases/download/${TAG}/${ZIP_NAME}"
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo "==> building ${NAME} ${VERSION}"
dotnet build "${PLUGIN_DIR}/${PROJECT}" --configuration Release --nologo \
    -p:Version="${VERSION}" -p:AssemblyVersion="${VERSION}" -p:FileVersion="${VERSION}" \
    | tail -3

STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT

# Jellyfin unpacks the zip straight into the plugin folder, so the assembly must sit at the
# root of the archive, not inside a directory.
cp "${PLUGIN_DIR}/bin/Release/${FRAMEWORK}/${ASSEMBLY}" "${STAGE}/"

CHANGELOG="${CHANGELOG:-}" ASSEMBLY="${ASSEMBLY}" CATEGORY="${CATEGORY}" \
DESCRIPTION="${DESCRIPTION}" GUID="${GUID}" NAME="${NAME}" OVERVIEW="${OVERVIEW}" \
OWNER="${OWNER}" TARGET_ABI="${TARGET_ABI}" TIMESTAMP="${TIMESTAMP}" VERSION="${VERSION}" \
python3 - "${STAGE}/meta.json" <<'PY'
import json, os, sys
json.dump({
    "category": os.environ["CATEGORY"],
    "changelog": os.environ["CHANGELOG"],
    "description": os.environ["DESCRIPTION"],
    "guid": os.environ["GUID"],
    "name": os.environ["NAME"],
    "overview": os.environ["OVERVIEW"],
    "owner": os.environ["OWNER"],
    "targetAbi": os.environ["TARGET_ABI"],
    "timestamp": os.environ["TIMESTAMP"],
    "version": os.environ["VERSION"],
    "status": "Active",
    "autoUpdate": True,
    "assemblies": [os.environ["ASSEMBLY"]],
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
CATEGORY="${CATEGORY}" CHANGELOG="${CHANGELOG:-}" CHECKSUM="${CHECKSUM}" \
DESCRIPTION="${DESCRIPTION}" GUID="${GUID}" NAME="${NAME}" OVERVIEW="${OVERVIEW}" \
OWNER="${OWNER}" SOURCE_URL="${SOURCE_URL}" TARGET_ABI="${TARGET_ABI}" \
TIMESTAMP="${TIMESTAMP}" VERSION="${VERSION}" python3 - <<'PY'
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

echo "    zip       dist/${ZIP_NAME}"
echo "    checksum  ${CHECKSUM}"
echo "    tag       ${TAG}"
