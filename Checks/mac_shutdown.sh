#!/usr/bin/env bash
set -euo pipefail

app=$1
shift
work=$(mktemp -d)
pid=''
cleanup() {
    if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi
    rm -rf "$work"
}
trap cleanup EXIT
for mode in loaded cancelled; do
    extra=()
    if [[ "$mode" == cancelled ]]; then extra+=(--cancel-inference); fi
    # No CI credentials reach the app, or a possible OS crash report.
    env -i HOME="$HOME" PATH=/usr/bin:/bin:/usr/sbin:/sbin TMPDIR="$TMPDIR" \
        "$app" --check-shutdown "$@" "${extra[@]}" > "$work/$mode.log" 2>&1 &
    pid=$!
    for i in {1..90}; do
        if ! kill -0 "$pid" 2>/dev/null; then break; fi
        sleep 1
    done
    if kill -0 "$pid" 2>/dev/null; then
        sample "$pid" 1 -file "$work/sample.txt" || true
        head -100 "$work/sample.txt" || true
        cat "$work/$mode.log"
        echo 'Application shutdown check timed out.' >&2
        exit 1
    fi
    result=0
    wait "$pid" || result=$?
    pid=''
    cat "$work/$mode.log"
    (( result == 0 )) || exit "$result"
    grep -Fq 'Application shutdown completed after releasing its models.' "$work/$mode.log"
    if [[ "$mode" == loaded ]]; then
        grep -Fq 'Speech model loaded for normal application Quit' "$work/$mode.log"
    else
        grep -Fq 'Quit requested during speech processing.' "$work/$mode.log"
    fi
done
echo 'Normal Quit passed with a loaded model and while cancelling inference.'
