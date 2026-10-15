#!/bin/zsh
# Create a self-signed code-signing identity in the login keychain (once per Mac).
#
# Ad-hoc signatures tie macOS privacy grants (Screen Recording, Accessibility) to the exact binary hash,
# so every rebuild looks like a new app and asks again. Signing with a stable identity keeps the grant.
set -euo pipefail

IDENTITY="SuYanShot Local Signing"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"

if security find-certificate -c "$IDENTITY" "$KEYCHAIN" >/dev/null 2>&1; then
    echo "✓ '$IDENTITY' already exists"
    exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $IDENTITY
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
CNF

/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -config "$WORK/cert.cnf" -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
/usr/bin/openssl pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -name "$IDENTITY" -out "$WORK/identity.p12" -passout pass:suyanshot

security import "$WORK/identity.p12" -k "$KEYCHAIN" -P suyanshot -T /usr/bin/codesign >/dev/null
echo "✓ Created '$IDENTITY' in the login keychain"
