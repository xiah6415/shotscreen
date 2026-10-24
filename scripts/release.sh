#!/bin/zsh
# Build, zip and publish 拾光 (Glean.app) as a GitHub Release, which the app's update check picks up.
#
#   scripts/release.sh                     # notes = commit subjects since the previous tag
#   scripts/release.sh notes.md            # notes from a file
#   scripts/release.sh --dry-run [notes]   # build and zip only, publish nothing
#
# Bump VERSION first and push the commit; the release is tagged v<VERSION> on that commit.
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

if ! $DRY_RUN; then
    [[ -z "$(git status --porcelain)" ]] || { echo "✗ Commit or stash changes first: the release must match a commit"; exit 1; }
    git fetch --quiet origin
    git merge-base --is-ancestor HEAD origin/main || { echo "✗ Push HEAD to origin/main first"; exit 1; }
    if gh release view "$TAG" --repo "$REPO" >/dev/null 2>&1; then echo "✗ $TAG already exists; bump VERSION"; exit 1; fi
fi

scripts/make-app.sh
APP="$ROOT/.build/app/$APP_NAME.app"

# Updates are only accepted when they carry the same certificate as the installed app.
SIGNATURE="$(codesign -dvv "$APP" 2>&1)"
[[ "$SIGNATURE" == *"Authority=$SIGNING_IDENTITY"* ]] \
    || { echo "✗ The app is not signed with '$SIGNING_IDENTITY' (run scripts/make-signing-cert.sh, or import the backed-up certificate)"; exit 1; }

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

read "REPLY?Publish $TAG to github.com/$REPO? [y/N] " || true
[[ "$REPLY" == [yY]* ]] || { echo "Cancelled"; exit 1; }
gh release create "$TAG" "$ZIP" --repo "$REPO" --target "$(git rev-parse HEAD)" --title "拾光 Glean $VERSION" --notes-file "$NOTES"
echo "✓ Published $TAG"
