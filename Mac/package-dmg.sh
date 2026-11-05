#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
out="$(pwd)/build/mac"
app="$out/TalkToMe.app"
dmg="$out/TalkToMe-macOS-$(uname -m).dmg"

# The release workflow passes the fingerprint of its imported Developer ID Application identity.
if (( $# )); then
    [[ "$1" =~ ^[[:xdigit:]]{40}$ ]] || { echo "Invalid signing identity" >&2; exit 1; }
    codesign --force --sign "$1" --timestamp --options runtime "$app/Contents/Helpers/llama-server"
    codesign --force --sign "$1" --timestamp --options runtime --entitlements Mac/audio-input.plist "$app"
fi
codesign --verify --deep --strict "$app"
"$out/dmg-tools/bin/dmgbuild" -s Mac/dmg-settings.py -D "app=$app" TalkToMe "$dmg"
if (( $# )); then
    codesign --force --sign "$1" --timestamp --identifier com.sergeymakeev.TalkToMe.dmg "$dmg"
    codesign --verify --strict "$dmg"
fi
