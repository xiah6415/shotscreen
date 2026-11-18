#!/bin/zsh
# Build, zip and publish 拾光 (Glean.app) as a GitHub Release, which the app's update check picks up.
#
#   scripts/release.sh                     # notes = commit subjects since the previous tag
#   scripts/release.sh notes.md            # notes from a file
#   scripts/release.sh --dry-run [notes]   # build and zip only, publish nothing
#
# Bump VERSION first and push the commit; the release is tagged v<VERSION> on that commit.
# If v<VERSION> already exists (the Windows build was published first), the Mac zip is added to it.
# Refuses to publish when the build's certificate differs from the installed /Applications/Glean.app, because the
# in-app update rejects a new certificate (ALLOW_NEW_CERTIFICATE=1 to publish anyway).
set -euo pipefail

APP_NAME="Glean"
REPO="xiah6415/shotscreen"
SIGNING_IDENTITY="SuYanShot Local Signing"

ROOT="${0:A:h:h}"
cd "$ROOT"

DRY_RUN=false
if [[ "${1:-}" == "--dry-run" ]]; then DRY_RUN=true; shift; fi
NOTES_FILE="${1:-}"

VERSION="$(tr -d '[:space:]' < VERSION)"
TAG="v$VERSION"
[[ "$VERSION" =~ '^[0-9]+(\.[0-9]+)*$' ]] || { echo "✗ VERSION '$VERSION' must look like 1.2.3"; exit 1; }

RELEASE_EXISTS=false
if ! $DRY_RUN; then
    [[ -z "$(git status --porcelain)" ]] || { echo "✗ Commit or stash changes first: the release must match a commit"; exit 1; }
    git fetch --quiet origin
    git merge-base --is-ancestor HEAD origin/main || { echo "✗ Push HEAD to origin/main first"; exit 1; }
    if EXISTING="$(gh release view "$TAG" --repo "$REPO" --json assets --jq '.assets[].name' 2>/dev/null)"; then
        RELEASE_EXISTS=true
        if print -r -- "$EXISTING" | grep -qx "$APP_NAME-$VERSION.zip"; then echo "✗ $TAG already has the Mac build; bump VERSION"; exit 1; fi
    fi
fi

scripts/make-app.sh
APP="$ROOT/.build/app/$APP_NAME.app"

# Updates are only accepted when they carry the same certificate as the installed app.
SIGNATURE="$(codesign -dvv "$APP" 2>&1)"
[[ "$SIGNATURE" == *"Authority=$SIGNING_IDENTITY"* ]] \
    || { echo "✗ The app is not signed with '$SIGNING_IDENTITY' (run scripts/make-signing-cert.sh, or import the backed-up certificate)"; exit 1; }

# The update only installs builds signed with the same certificate as the installed app.
leaf_sha1() {
    local dir; dir="$(mktemp -d)"
    codesign -d --extract-certificates="$dir/cert" "$1" >/dev/null 2>&1 && shasum -a 1 "$dir/cert0" | cut -c1-40
    rm -rf "$dir"
}
INSTALLED="/Applications/$APP_NAME.app"
if [[ -d "$INSTALLED" && "${ALLOW_NEW_CERTIFICATE:-}" != 1 ]]; then
    BUILT_LEAF="$(leaf_sha1 "$APP")"; INSTALLED_LEAF="$(leaf_sha1 "$INSTALLED" || true)"
    if [[ -n "$INSTALLED_LEAF" && "$BUILT_LEAF" != "$INSTALLED_LEAF" ]]; then
        echo "✗ This build is signed with certificate $BUILT_LEAF, but the installed app uses $INSTALLED_LEAF."
        echo "  Its in-app update would refuse this release. Publish from the Mac that has that certificate,"
        echo "  or import it (scripts/backup-signing-cert.sh --restore …). ALLOW_NEW_CERTIFICATE=1 publishes anyway."
        $DRY_RUN || exit 1
    fi
fi

OUT="$ROOT/.build/release-zips"
ZIP="$OUT/Glean-$VERSION.zip"
mkdir -p "$OUT"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "✓ Zipped $ZIP ($(du -h "$ZIP" | cut -f1))"

NOTES="$OUT/notes-$VERSION.md"
if [[ -n "$NOTES_FILE" ]]; then
    cp "$NOTES_FILE" "$NOTES"
else
    PREVIOUS="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)"
    git log --no-merges --format='- %s' ${PREVIOUS:+"$PREVIOUS..HEAD"} > "$NOTES"
fi
echo "── Release notes ──"; cat "$NOTES"; echo "───────────────────"

if $DRY_RUN; then echo "✓ Dry run: nothing published"; exit 0; fi

if $RELEASE_EXISTS; then
    read "REPLY?Add the Mac build to the existing $TAG on github.com/$REPO? [y/N] " || true
    [[ "$REPLY" == [yY]* ]] || { echo "Cancelled"; exit 1; }
    gh release upload "$TAG" "$ZIP" --repo "$REPO"
else
    read "REPLY?Publish $TAG to github.com/$REPO? [y/N] " || true
    [[ "$REPLY" == [yY]* ]] || { echo "Cancelled"; exit 1; }
    gh release create "$TAG" "$ZIP" --repo "$REPO" --target "$(git rev-parse HEAD)" --title "拾光 Glean $VERSION" --notes-file "$NOTES"
fi
echo "✓ Published $TAG"
