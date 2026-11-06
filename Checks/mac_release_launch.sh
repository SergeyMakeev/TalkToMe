#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

[[ "${RELEASE_TAG:-}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Expected a stable release tag.' >&2; exit 1; }
out="$PWD/build/mac-launch"
mkdir -p "$out"
sw_vers > "$out/system.txt"
uname -m >> "$out/system.txt"
[[ "$(uname -m)" == arm64 ]] || { echo 'This check requires Apple silicon.' >&2; exit 1; }
work=$(mktemp -d)
mount="$work/mounted"
installed="$work/Installed Apps/TalkToMe.app"
mkdir -p "$mount" "$(dirname "$installed")"
attached=false
launcher=''

# Match only the app installed by this check, even if another copy is running.
app_pid() {
    ps -axo pid=,comm= -ww | while read -r pid executable; do
        if [[ "$executable" == "$installed/Contents/MacOS/TalkToMe" ]]; then printf '%s\n' "$pid"; fi
    done
}
cleanup() {
    local result=$?
    local pid
    if (( result != 0 )); then
        while read -r pid; do
            [[ -z "$pid" ]] || sample "$pid" 3 -file "$out/sample-$pid.txt" || true
        done < <(app_pid)
        log show --last 3m --style compact --info --predicate 'eventMessage CONTAINS[c] "TalkToMe"' > "$out/launch-policy.txt" 2>&1 || true
    fi
    while read -r pid; do [[ -z "$pid" ]] || kill "$pid" 2>/dev/null || true; done < <(app_pid)
    if [[ -n "$launcher" ]]; then kill "$launcher" 2>/dev/null || true; fi
    if [[ "$attached" == true ]]; then hdiutil detach "$mount" >/dev/null || return; fi
    rm -rf "$work"
}
trap cleanup EXIT

gh release download "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --pattern TalkToMe-macOS-arm64.dmg --dir "$work"
hdiutil verify "$work/TalkToMe-macOS-arm64.dmg"
hdiutil attach -readonly -nobrowse -noautoopen -mountpoint "$mount" "$work/TalkToMe-macOS-arm64.dmg" >/dev/null
attached=true
ditto "$mount/TalkToMe.app" "$installed"
hdiutil detach "$mount" >/dev/null
attached=false
codesign --verify --deep --strict --verbose=2 "$installed"
codesign --display --verbose=4 "$installed" > "$out/signature.txt" 2>&1
otool -L "$installed/Contents/MacOS/TalkToMe" > "$out/libraries.txt"
if [[ "${QUARANTINE_RELEASE:-false}" == true ]]; then
    # CLI downloads are not quarantined. Apply the first-launch marker only to
    # this disposable test copy; no policy or approval settings are changed.
    quarantine="0083;$(printf '%x' "$(date +%s)");TalkToMeLaunchCheck;$(uuidgen)"
    xattr -w com.apple.quarantine "$quarantine" "$installed"
fi
xattr -p com.apple.quarantine "$installed" > "$out/quarantine.txt" 2>&1 || true
touch "$out/stdout.txt" "$out/stderr.txt"
open -n -W -a "$installed" --stdout "$out/stdout.txt" --stderr "$out/stderr.txt" --args --check-window &
launcher=$!
for i in {1..45}; do
    if ! kill -0 "$launcher" 2>/dev/null; then
        wait "$launcher"
        launcher=''
        cat "$out/stdout.txt" "$out/stderr.txt"
        grep -Fq 'Result window, Settings sheet and menu-bar icon appeared.' "$out/stdout.txt"
        echo 'Installed release opened through Launch Services.'
        exit 0
    fi
    sleep 1
done
cat "$out/stdout.txt" "$out/stderr.txt"
echo 'The installed release did not finish its launch check within 45 seconds.' >&2
exit 1
