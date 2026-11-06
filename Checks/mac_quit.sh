#!/usr/bin/env bash
set -euo pipefail

app=$1
out=$2
mkdir -p "$out"
work=$(mktemp -d)
pid=''
cleanup() {
    if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi
    rm -rf "$work"
}
trap cleanup EXIT
swiftc Checks/MacQuitRequest.swift -o "$work/request-quit"
env -i HOME="$HOME" PATH=/usr/bin:/bin:/usr/sbin:/sbin TMPDIR="$TMPDIR" \
    "$app/Contents/MacOS/TalkToMe" > "$out/stdout.txt" 2> "$out/stderr.txt" &
pid=$!
"$work/request-quit" "$pid"
for i in {1..30}; do
    if ! kill -0 "$pid" 2>/dev/null; then
        if wait "$pid"; then
            pid=''
            cat "$out/stdout.txt" "$out/stderr.txt"
            echo 'Normal application Quit completed without a crash.'
            exit 0
        else
            result=$?
            pid=''
            cat "$out/stdout.txt" "$out/stderr.txt"
            echo "Normal Quit failed with exit status $result." >&2
            exit "$result"
        fi
    fi
    sleep 1
done
sample "$pid" 1 -file "$out/quit-sample.txt" || true
echo 'TalkToMe did not finish quitting within 30 seconds.' >&2
exit 1
