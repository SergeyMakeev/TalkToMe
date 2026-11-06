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
for helper in "$installed/Contents/Helpers/"*; do
    # A copied installer must not resolve libraries from the CI build directory,
    # Homebrew, or a separately installed runtime on the user's machine.
    while IFS= read -r dependency; do
        case "$dependency" in
            @rpath/*|@loader_path/*|/usr/lib/*|/System/Library/*) ;;
            *) echo "Unexpected runtime dependency in $helper: $dependency" >&2; exit 1 ;;
        esac
    done < <(otool -L "$helper" | tail -n +2 | sed -E 's/^[[:space:]]+//; s/ \(compatibility version.*$//')
done
if [[ "${NOTARIZE:-false}" == true ]]; then
    spctl --assess --type execute --verbose=4 "$installed"
fi
"$installed/Contents/MacOS/TalkToMe" --self-test
"$installed/Contents/MacOS/TalkToMe" --check-insertion
"$installed/Contents/MacOS/TalkToMe" --check-parakeet build/mac/whisper.cpp/samples/jfk.wav
