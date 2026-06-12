#!/usr/bin/env bash
# Generate a self-signed cert for Kestrel TLS.
# Usage: ./generate-cert.sh <host-or-ip> [out-dir] [password]
# Example: ./generate-cert.sh 13.234.56.78
#          ./generate-cert.sh 13.234.56.78 ./certs MyPfxPass

set -euo pipefail

HOST="${1:?Usage: $0 <host-or-ip> [out-dir] [password]}"
OUT_DIR="${2:-./certs}"
PASS="${3:-$(openssl rand -hex 16)}"

mkdir -p "$OUT_DIR"

# Detect whether HOST looks like an IP address
if [[ "$HOST" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  SAN_ENTRY="IP.1 = $HOST"
else
  SAN_ENTRY="DNS.1 = $HOST"
fi

cat > "$OUT_DIR/cert.cnf" <<EOF
[ req ]
default_bits       = 2048
distinguished_name = req_distinguished_name
req_extensions     = req_ext
prompt             = no

[ req_distinguished_name ]
CN = $HOST
O  = PSR Service

[ req_ext ]
subjectAltName = @alt_names

[ alt_names ]
$SAN_ENTRY
DNS.2 = localhost
EOF

openssl req -x509 -nodes -days 730 -newkey rsa:2048 \
  -keyout "$OUT_DIR/psr.key" \
  -out    "$OUT_DIR/psr.crt" \
  -config "$OUT_DIR/cert.cnf" \
  -extensions req_ext \
  -sha256 \
  >/dev/null 2>&1

openssl pkcs12 -export \
  -out    "$OUT_DIR/psr.pfx" \
  -inkey  "$OUT_DIR/psr.key" \
  -in     "$OUT_DIR/psr.crt" \
  -passout "pass:$PASS" \
  -name "psr-service"

# SHA-256 fingerprint for WPF client pinning
THUMB=$(openssl x509 -in "$OUT_DIR/psr.crt" -noout -fingerprint -sha256 \
  | sed 's/://g' | cut -d= -f2)

# Tighten permissions.
# .key: 600 — private key, only owner.
# .pfx: 644 — the non-root container user must be able to read it (file is password-protected anyway).
chmod 600 "$OUT_DIR/psr.key"
chmod 644 "$OUT_DIR/psr.pfx" "$OUT_DIR/psr.crt"
rm -f "$OUT_DIR/cert.cnf"

cat <<EOF

=================================================================
 Cert generated successfully.

   PFX file : $OUT_DIR/psr.pfx
   CRT file : $OUT_DIR/psr.crt   (public, share with WPF clients)
   Password : $PASS
   Valid    : 2 years from today

 ACTION 1 — add to deploy/.env on the server:
   Kestrel__Endpoints__Https__Certificate__Password=$PASS

 ACTION 2 — paste this SHA-256 thumbprint into the WPF
            appsettings.json "ApiCertThumbprint" field:

   $THUMB

=================================================================
EOF
