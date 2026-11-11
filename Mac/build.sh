#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
root="$(pwd)"
out="$root/build/mac"
vendor="$out/whisper.cpp"
revision=2eeeba56e9edd762b4b38467bab96c2517163158
mkdir -p "$out"
if [[ ! -d "$vendor/.git" ]]; then git init -q "$vendor"; fi
if [[ "$(git -C "$vendor" rev-parse --verify HEAD 2>/dev/null || true)" != "$revision" ]]; then
    git -C "$vendor" fetch --depth 1 https://github.com/ggml-org/whisper.cpp.git "$revision"
    git -C "$vendor" checkout --detach "$revision"
fi
export MACOSX_DEPLOYMENT_TARGET=13.0
cmake -S Mac -B "$out/native" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 -DWHISPER_SOURCE="$vendor"
cmake --build "$out/native" --target TalkToMe
app="$out/TalkToMe.app/Contents"
mkdir -p "$app/MacOS" "$app/Resources" "$out/TalkToMe.iconset"
cp "$out/native/TalkToMe" "$app/MacOS/"
python3 Shared/build_engines.py "$out/engines"
mkdir -p "$app/Helpers"
cp "$out/engines/llama-server" "$app/Helpers/"
cp "$out/engines/TalkToMeParakeetHost" "$out/engines/"*.dylib "$app/Helpers/"
for helper in "$app/Helpers/"*.dylib "$app/Helpers/TalkToMeParakeetHost"; do codesign --force --sign - "$helper"; done
codesign --force --sign - "$app/Helpers/llama-server"
cp "$out/engines/"*.txt "$app/Resources/"
cp Shared/{GemmaTerms.txt,GemmaNotice.txt,ModelNotices.txt} "$app/Resources/"
cp Mac/Info.plist "$app/"
if [[ -n "${APP_VERSION:-}" ]]; then
    plutil -replace CFBundleShortVersionString -string "$APP_VERSION" "$app/Info.plist"
    plutil -replace CFBundleVersion -string "$APP_VERSION" "$app/Info.plist"
fi
cp Shared/{EnglishPrompt.txt,TranslateGemmaPrompt.txt,Models.json,LocalEditing.json,EngineeringTerms.json} Desktop/Sounds/{start,stop}.wav "$app/Resources/"
cp Checks/{EditingResponses,TranslationCases,TranslateGemmaCases,VocabularyCases,EngineeringCases}.json "$app/Resources/"
cp LICENSE "$app/Resources/TalkToMe-LICENSE.txt"
cp "$vendor/LICENSE" "$app/Resources/whisper.cpp-LICENSE.txt"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" Mac/TalkToMe.png --out "$out/TalkToMe.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" Mac/TalkToMe.png --out "$out/TalkToMe.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$out/TalkToMe.iconset" -o "$app/Resources/TalkToMe.icns"
codesign --force --sign - "$out/TalkToMe.app"
codesign --verify --strict "$out/TalkToMe.app"
python3 -m venv "$out/dmg-tools"
"$out/dmg-tools/bin/python" -m pip --disable-pip-version-check install --quiet 'dmgbuild==1.6.7'
if [[ "${1:-}" != --defer-dmg ]]; then bash Mac/package-dmg.sh; fi
