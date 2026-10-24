#!/bin/zsh
# Back up (or restore) the "SuYanShot Local Signing" identity that every 拾光 release is signed with.
#
# Installed copies only accept updates signed by this exact certificate. If it is lost (new Mac, wiped keychain),
# nobody who already has 拾光 can update any more. Keep the .p12 somewhere safe, e.g. a password manager
# or an encrypted disk — not in this repository.
#
#   scripts/backup-signing-cert.sh [output.p12]     # export; asks for a password to protect the file
#   scripts/backup-signing-cert.sh --restore file.p12
set -euo pipefail

IDENTITY="SuYanShot Local Signing"
KEYCHAIN="${KEYCHAIN:-$HOME/Library/Keychains/login.keychain-db}"

if [[ "${1:-}" == "--restore" ]]; then
    FILE="${2:?usage: --restore file.p12}"
    security import "$FILE" -k "$KEYCHAIN" -T /usr/bin/codesign
    echo "✓ Imported. Check with: security find-identity -p codesigning | grep '$IDENTITY'"
    exit 0
fi

OUT="${1:-$HOME/Desktop/SuYanShot-Local-Signing-$(date +%Y%m%d).p12}"
[[ "$OUT" == *.p12 ]] || { echo "✗ Output must end in .p12"; exit 1; }
case "${OUT:A}" in "${0:A:h:h}"/*) echo "✗ Don't save the backup inside the repository"; exit 1;; esac

security find-identity -p codesigning "$KEYCHAIN" | grep -q "\"$IDENTITY\"" || { echo "✗ '$IDENTITY' is not in the login keychain"; exit 1; }

echo "Choose a password for the backup file (you'll need it to restore)."
read -s "PASS1?Password: "; echo
read -s "PASS2?Again: "; echo
[[ -n "$PASS1" && "$PASS1" == "$PASS2" ]] || { echo "✗ Passwords were empty or didn't match"; exit 1; }

# macOS may ask once to allow exporting the private key from the keychain.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
security export -k "$KEYCHAIN" -t identities -f pkcs12 -P "$PASS1" -o "$WORK/all.p12" >/dev/null
# The export contains every identity in the keychain; keep only ours.
openssl pkcs12 -in "$WORK/all.p12" -passin "pass:$PASS1" -nodes -legacy 2>/dev/null > "$WORK/all.pem" \
    || openssl pkcs12 -in "$WORK/all.p12" -passin "pass:$PASS1" -nodes > "$WORK/all.pem"
awk -v id="$IDENTITY" '
    /friendlyName:/ { keep = index($0, id) > 0 }
    /^-----BEGIN/ { block = keep }
    block { print }
    /^-----END/ { block = 0 }
' "$WORK/all.pem" > "$WORK/ours.pem"
grep -q "PRIVATE KEY" "$WORK/ours.pem" || { echo "✗ Couldn't find the private key for '$IDENTITY' in the export"; exit 1; }
/usr/bin/openssl pkcs12 -export -in "$WORK/ours.pem" -name "$IDENTITY" -passout "pass:$PASS1" -out "$OUT"
chmod 600 "$OUT"
unset PASS1 PASS2
echo "✓ Saved $OUT"
echo "  Move it somewhere safe (password manager / encrypted disk), then delete it from the Desktop."
