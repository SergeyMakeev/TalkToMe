#!/usr/bin/env bash
set -euo pipefail

dmg=$1
: "${NOTARY_KEY_ID:?Missing App Store Connect key ID}"
: "${NOTARY_ISSUER_ID:?Missing App Store Connect issuer ID}"
: "${NOTARY_KEY_P8:?Missing App Store Connect private key}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf '%s\n' "$NOTARY_KEY_P8" > "$work/AuthKey.p8"
unset NOTARY_KEY_P8
auth=(--key "$work/AuthKey.p8" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER_ID")
if ! xcrun notarytool submit "$dmg" "${auth[@]}" --wait --timeout 40m --output-format json > "$work/result.json"; then
    cat "$work/result.json" >&2
    exit 1
fi
if [[ "$(plutil -extract status raw "$work/result.json")" != Accepted ]]; then
    xcrun notarytool log "$(plutil -extract id raw "$work/result.json")" "${auth[@]}" >&2
    exit 1
fi
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl --assess --type open --context context:primary-signature --verbose=4 "$dmg"
