#!/usr/bin/env bash
# sign-file.sh <file>  —  Authenticode-sign <file> in place with the release certificate.
# Env: SNP_PFX (pkcs12), SNP_PFX_PASS_FILE (file with the PFX password), optional SNP_TSA / SNP_URL.
# Used by build/release.sh and by the NSIS !finalize/!uninstfinalize hooks.
set -e
F="$1"
[ -n "$F" ] && [ -f "$F" ] || { echo "sign-file.sh: missing file"; exit 2; }
[ -n "$SNP_PFX" ] && [ -n "$SNP_PFX_PASS_FILE" ] || { echo "sign-file.sh: SNP_PFX / SNP_PFX_PASS_FILE not set"; exit 2; }

ARGS=(sign -pkcs12 "$SNP_PFX" -readpass "$SNP_PFX_PASS_FILE" -h sha256 -n "StickyNotes Pro")
if [ -n "$SNP_URL" ]; then ARGS+=( -i "$SNP_URL" ); fi

if [ -n "$SNP_TSA" ]; then
    if ! osslsigncode "${ARGS[@]}" -ts "$SNP_TSA" -in "$F" -out "$F.signed" >/dev/null 2>&1; then
        echo "sign-file.sh: timestamping failed - signing without timestamp" >&2
        osslsigncode "${ARGS[@]}" -in "$F" -out "$F.signed" >/dev/null
    fi
else
    osslsigncode "${ARGS[@]}" -in "$F" -out "$F.signed" >/dev/null
fi
mv "$F.signed" "$F"
echo "signed+timestamped: $(basename "$F")"
