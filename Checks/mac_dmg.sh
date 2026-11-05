#!/usr/bin/env bash
set -euo pipefail

dmg=$1
work=$(mktemp -d)
mount="$work/mounted"
installed="$work/Installed Apps/TalkToMe.app"
mkdir -p "$mount" "$(dirname "$installed")"
attached=false
cleanup() {
    if [[ "$attached" == true ]]; then hdiutil detach "$mount" >/dev/null || return; fi
    rm -rf "$work"
}
trap cleanup EXIT

hdiutil verify "$dmg"
hdiutil attach -readonly -nobrowse -noautoopen -mountpoint "$mount" "$dmg" >/dev/null
attached=true
[[ -f "$mount/.DS_Store" && "$(readlink "$mount/Applications")" == /Applications ]]
ditto "$mount/TalkToMe.app" "$installed"
hdiutil detach "$mount" >/dev/null
attached=false
codesign --verify --deep --strict --verbose=2 "$installed"
if [[ "${NOTARIZE:-false}" == true ]]; then
    spctl --assess --type execute --verbose=4 "$installed"
fi
"$installed/Contents/MacOS/TalkToMe" --self-test
