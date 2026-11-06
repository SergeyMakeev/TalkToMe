#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

[[ "${RELEASE_TAG:-}" =~ ^(v[0-9]+\.[0-9]+\.[0-9]+|macos-v[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+)$ ]] || { echo 'Expected a stable or Mac preview release tag.' >&2; exit 1; }
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
existing_pids=" $(pgrep -x TalkToMe | tr '\n' ' ' || true) "

# A quarantined app may be translocated to a different path. In this fresh CI
# session, track new TalkToMe PIDs and leave any pre-existing copies alone.
app_pid() {
    local pid
    while read -r pid; do
        if [[ -n "$pid" && "$existing_pids" != *" $pid "* ]]; then printf '%s\n' "$pid"; fi
    done < <(pgrep -x TalkToMe || true)
}
cleanup() {
    local result=$?
    local pid
    if (( result != 0 )); then
        while read -r pid; do
            [[ -z "$pid" ]] || sample "$pid" 3 -file "$out/sample-$pid.txt" || true
        done < <(app_pid)
        # Exclude Launch Services / launchd messages: they can dump the caller's
        # entire environment. Only collect this app's security-assessment events.
        log show --last 3m --style compact --info --predicate '(process == "syspolicyd" OR process == "amfid" OR sender == "AppleSystemPolicy" OR sender == "AppleMobileFileIntegrity") AND eventMessage CONTAINS[c] "TalkToMe"' > "$out/launch-policy.txt" 2>&1 || true
    fi
    while read -r pid; do [[ -z "$pid" ]] || kill "$pid" 2>/dev/null || true; done < <(app_pid)
    if [[ -n "$launcher" ]]; then kill "$launcher" 2>/dev/null || true; fi
    if [[ "$attached" == true ]]; then hdiutil detach "$mount" >/dev/null || return; fi
    rm -rf "$work"
}
trap cleanup EXIT

gh release download "$RELEASE_TAG" --repo "$GITHUB_REPOSITORY" --pattern TalkToMe-macOS-arm64.dmg --dir "$work"
unset GH_TOKEN GITHUB_TOKEN
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
# Launch Services may log the launched environment, so give the app only the
# OS paths it needs. No CI credentials should reach the app or its launch logs.
env -i HOME="$HOME" PATH=/usr/bin:/bin:/usr/sbin:/sbin TMPDIR="$TMPDIR" \
    /usr/bin/open -n -W -a "$installed" --stdout "$out/stdout.txt" --stderr "$out/stderr.txt" --args --check-window &
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
