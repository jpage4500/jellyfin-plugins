#!/usr/bin/env bash
#
# Cuts the next release of one plugin: bumps its patch version, asks for release notes, then
# builds the zip, publishes it as a GitHub release asset and pushes the manifest.json entry
# pointing at it. The manifest URL a Jellyfin server subscribes to is served from this repo,
# so the commit and the release have to land together -- that coupling is why this is a script.
#
#   ./release.sh              pick the plugin (prompts only if there is more than one)
#   ./release.sh --dry-run    build and show what would happen, change nothing
#
# Plugins are any directory holding a plugin.json. Each releases independently: versions are
# tracked per plugin in manifest.json and tags are prefixed with the plugin id, so releasing
# one never disturbs another.
#
# Requires a clean working tree: the zip must correspond to a commit someone can check out.
# The notes prompt is the last point of no return -- Ctrl-C there aborts with nothing done.

set -euo pipefail

cd "$(dirname "$0")"

DRY_RUN=0
case "${1:-}" in
    "") ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "usage: ./release.sh [--dry-run]" >&2; exit 1 ;;
esac

die() { echo "release: $*" >&2; exit 1; }

# --- preflight ---------------------------------------------------------------------------
for tool in dotnet gh git python3 zip; do
    command -v "${tool}" >/dev/null || die "${tool} is not installed"
done
gh auth status >/dev/null 2>&1 || die "gh is not authenticated -- run: gh auth login"

BRANCH="$(git symbolic-ref --short HEAD 2>/dev/null)" || die "HEAD is detached; check out a branch"

if [[ -n "$(git status --porcelain)" ]]; then
    die "working tree is dirty -- commit or stash first, so the release matches a commit
$(git status --short | sed 's/^/       /')"
fi

# --- which plugin ------------------------------------------------------------------------
PLUGINS=()
while IFS= read -r found; do PLUGINS+=("$(dirname "${found}")"); done \
    < <(find . -mindepth 2 -maxdepth 2 -name plugin.json | sed 's#^\./##' | sort)

case ${#PLUGINS[@]} in
    0) die "no plugin.json found in any subdirectory" ;;
    1) PLUGIN_DIR="${PLUGINS[0]}" ;;
    *)
        echo
        echo "  Which plugin?"
        for i in "${!PLUGINS[@]}"; do
            echo "    $((i + 1))) ${PLUGINS[$i]}"
        done
        echo
        while :; do
            read -r -p "  Number: " choice || die "no plugin chosen"
            if [[ "${choice}" =~ ^[0-9]+$ ]] && (( choice >= 1 && choice <= ${#PLUGINS[@]} )); then
                PLUGIN_DIR="${PLUGINS[$((choice - 1))]}"
                break
            fi
            echo "  pick 1-${#PLUGINS[@]} (Ctrl-C to abort)"
        done ;;
esac

PLUGIN_JSON="${PLUGIN_DIR}/plugin.json"
ID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "${PLUGIN_JSON}")"
NAME="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["name"])' "${PLUGIN_JSON}")"
GUID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["guid"])' "${PLUGIN_JSON}")"

# --- next version ------------------------------------------------------------------------
# Patch bump off this plugin's newest version in the manifest; 1.0.0.0 if it has none yet.
VERSION="$(GUID="${GUID}" python3 - <<'PY'
import json, os, pathlib

path = pathlib.Path("manifest.json")
versions = []
if path.exists():
    versions = [v["version"]
                for p in json.loads(path.read_text()) if p["guid"] == os.environ["GUID"]
                for v in p["versions"]]

if not versions:
    print("1.0.0.0")
else:
    major, minor, patch, build = max(tuple(int(n) for n in v.split(".")) for v in versions)
    print(f"{major}.{minor}.{patch + 1}.{build}")
PY
)" || die "could not work out the next version from manifest.json"

TAG="${ID}-v${VERSION}"
git rev-parse -q --verify "refs/tags/${TAG}" >/dev/null && die "tag ${TAG} already exists locally"
gh release view "${TAG}" >/dev/null 2>&1 && die "release ${TAG} already exists on GitHub"

SLUG="$(git remote get-url origin | sed -E 's#(git@github\.com:|https://github\.com/)##; s#\.git$##')"

# --- release notes -----------------------------------------------------------------------
echo
echo "  Releasing ${NAME} ${VERSION} from ${BRANCH} to ${SLUG}$([[ ${DRY_RUN} -eq 1 ]] && echo '  (dry run)')"
echo

if [[ -t 0 ]]; then
    while :; do
        read -r -e -p "  Release notes: " NOTES || die "aborted"
        [[ -n "${NOTES//[[:space:]]/}" ]] && break
        echo "  notes go in the manifest changelog, so they cannot be empty (Ctrl-C to abort)"
    done
else
    # Piped input, so the script stays usable from another script or a test.
    read -r NOTES || die "no release notes on stdin"
    [[ -n "${NOTES//[[:space:]]/}" ]] || die "no release notes on stdin"
fi
echo

# A dry run must leave the tracked manifest exactly as it found it.
if [[ ${DRY_RUN} -eq 1 && -f manifest.json ]]; then
    RESTORE_MANIFEST="$(mktemp)"
    cp manifest.json "${RESTORE_MANIFEST}"
    # shellcheck disable=SC2064
    trap "mv '${RESTORE_MANIFEST}' manifest.json" EXIT
fi

# --- build and package -------------------------------------------------------------------
CHANGELOG="${NOTES}" ./package.sh "${PLUGIN_DIR}" "${VERSION}" >/dev/null

# package.sh is the single source of truth for naming and hashing; read back what it decided.
read -r SOURCE_URL CHECKSUM < <(GUID="${GUID}" VERSION="${VERSION}" python3 - <<'PY'
import json, os
guid, version = os.environ["GUID"], os.environ["VERSION"]
entry = next(v for p in json.load(open("manifest.json")) if p["guid"] == guid
             for v in p["versions"] if v["version"] == version)
print(entry["sourceUrl"], entry["checksum"])
PY
)
ZIP="dist/$(basename "${SOURCE_URL}")"
[[ -f "${ZIP}" ]] || die "expected ${ZIP} to exist after packaging"

# Keep a plain `dotnet build` producing the same version the release shipped.
PROJECT="${PLUGIN_DIR}/$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["project"])' "${PLUGIN_JSON}")"
python3 - "${PROJECT}" "${VERSION}" <<'PY'
import pathlib, re, sys
project, version = sys.argv[1], sys.argv[2]
path = pathlib.Path(project)
text = path.read_text()
for tag in ("Version", "AssemblyVersion", "FileVersion"):
    text = re.sub(rf"<{tag}>[^<]*</{tag}>", f"<{tag}>{version}</{tag}>", text)
path.write_text(text)
PY

echo "  zip       ${ZIP}"
echo "  checksum  ${CHECKSUM}"
echo "  asset     ${SOURCE_URL}"
echo

if [[ ${DRY_RUN} -eq 1 ]]; then
    git checkout -- "${PROJECT}"
    echo "  would commit  manifest.json, ${PROJECT}"
    echo "  would push    ${BRANCH}"
    echo "  would create  release ${TAG} with ${ZIP}"
    echo
    echo "dry run: nothing was committed, pushed or published"
    exit 0
fi

# --- publish -----------------------------------------------------------------------------
# Push the commit first: the release is created against that pushed SHA, so the tag can never
# point at something the remote has not seen.
git add manifest.json "${PROJECT}"
git commit -q -m "Release ${NAME} ${VERSION}"
git push -q origin "${BRANCH}"
echo "==> pushed ${BRANCH}"

# gh creates the tag remotely as part of the release, so a failure here leaves no orphan tag.
if ! gh release create "${TAG}" "${ZIP}" \
        --target "$(git rev-parse HEAD)" \
        --title "${NAME} ${VERSION}" \
        --notes "${NOTES}"; then
    die "the commit is pushed but the release failed -- retry with:
       gh release create ${TAG} ${ZIP} --target $(git rev-parse HEAD) --notes \"${NOTES}\""
fi
echo "==> created release ${TAG}"

# The manifest is only useful if the URL it advertises actually serves the zip.
if curl -sfIL -o /dev/null "${SOURCE_URL}"; then
    echo "==> verified ${SOURCE_URL}"
else
    echo "warning: ${SOURCE_URL} did not respond yet; GitHub can lag a few seconds" >&2
fi

cat <<EOF

Released ${NAME} ${VERSION}. Servers subscribed to this repository will see it:

  https://raw.githubusercontent.com/${SLUG}/${BRANCH}/manifest.json

EOF
