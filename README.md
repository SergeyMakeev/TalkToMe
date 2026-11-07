# TalkToMe

Dictate in Russian, English, or both and insert the result at the cursor on macOS or Windows. TalkToMe includes Whisper and Parakeet for listening, and llama.cpp for optional English translation and editing. Download your models once in the app. Afterward, both steps work offline.

Download a versioned build from [GitHub Releases](https://github.com/SergeyMakeev/TalkToMe/releases). Maintainers can publish from the GitHub UI or by pushing a version tag. [How to publish a release](.github/workflows/README.md)

## Install on macOS

Stable releases include Windows x64, Apple silicon, and Intel Mac downloads together. The macOS insertion, Accessibility, Quit, and shortcut fixes from the previews are included.

1. On macOS 13 or later, download **TalkToMe-macOS-arm64.dmg** for Apple silicon or **TalkToMe-macOS-x86_64.dmg** for Intel. Open the disk image, drag **TalkToMe** onto **Applications**, eject the image, and open the installed app from Applications. For a CI build, extract the Actions artifact to get the DMG. For the **v0.1.0** release, extract the ZIP and move **TalkToMe.app** to Applications. [Releases](https://github.com/SergeyMakeev/TalkToMe/releases) · [Mac builds](https://github.com/SergeyMakeev/TalkToMe/actions/workflows/macos.yml)
2. Launch TalkToMe. In **Settings → Dictation**, grant **Microphone** access for recording and **Accessibility** access for insertion into other apps. Select a microphone or follow the system default.
3. In **Models**, choose a listening model and an output option, then **Download and save**. New installations default to Whisper Base and Qwen 3.5 2B, about 1.34 GB together. Existing model choices are preserved. Choose **Transcript only** to keep the spoken language and download just Whisper and its speech detector (61 MB).
4. To use existing speech files or change Metal acceleration, open **Speech**. Apple silicon uses a compatible physical GPU by default. Intel Macs and Macs with virtual graphics default to CPU. To connect to a separate llama-server, select **Use my own server** in **Models** and enter its details under **Server**.
5. Focus a text field, hold **Option+Space**, speak, and release. **Control+Option+Escape** cancels. Neither shortcut needs Fn or a numeric keypad. Choose another preset or type your own shortcut in **Settings → Dictation** if a combination is already in use. Older factory shortcuts migrate automatically; other custom shortcuts stay unchanged. Closing the window keeps the menu-bar shortcut active. Click the waveform icon to review the transcript, copy the English, open Settings, or quit.

The Mac app checks that the original editable field still has focus before replacing the selection. It can also send single-line Unicode text to compatible editors. Electron apps such as Slack and Visual Studio Code use a normal Command+V paste because their Accessibility writes can be accepted without changing the editor. TalkToMe temporarily places the result on the clipboard, preserves its previous items and formats, and restores them only if no newer copy has replaced them. It leaves results for manual copying if the clipboard cannot be preserved. Release the shortcut keys and keep the original text field focused until processing finishes.

“Text inserted” means TalkToMe verified the expected change in the editor. An accepted write or dispatched paste without readable confirmation is reported as unverified; it is never automatically retried. **Copy insertion details** records the app, delivery method, permission state, character counts, selection offsets and verification result. It does not copy document contents or dictated text into the diagnostics.

If macOS asks for clipboard access, allow it so TalkToMe can preserve the existing clipboard when pasting. If reading the clipboard is denied or fails, TalkToMe leaves its contents untouched and keeps the dictation result available to copy.

For Electron apps such as Slack, TalkToMe requests the app's accessibility tree when its focused editor is initially unavailable, then retries briefly while recording continues. It still checks the original app and window, rejects terminal and secure fields, and confirms the captured field before inserting. A missing focused field is reported separately from a missing macOS permission.

If transcription works but insertion does not, check the status below the shortcut in the result window. **Enable insertion…** opens the Accessibility permission settings. If TalkToMe is already enabled there but the app reports missing access after an update, remove it from the list, add the installed `/Applications/TalkToMe.app` again, and restart TalkToMe. Test in a new TextEdit document. **Review each result before inserting** leaves every result for manual copying; **Retry processing** also leaves the revised result for manual copying to avoid inserting a duplicate.

Settings reports editor access and simulated typing separately because macOS can return different permission states for them. **Recheck access** refreshes those checks; **Show this app** reveals the running copy so you can select the right one in System Settings. If access still fails after re-adding and restarting, **Copy access diagnostics** copies the version, running path, signing identity and both permission results for troubleshooting. It does not include recordings, transcripts or API keys. The app also tries the actual protected editor request instead of rejecting it solely because the permission preflight says no.

If removing and re-adding the app does not fix an enabled-but-denied permission, choose **Reset TalkToMe’s Accessibility approval…** and confirm. It runs `tccutil reset Accessibility com.sergeymakeev.TalkToMe`, then opens the permission settings. Enable TalkToMe again, close the app's Settings sheet, quit, and reopen it. The reset affects only this app’s Accessibility approval. Ad-hoc preview updates can change the code fingerprint stored with that approval; stable Developer ID signing is needed to retain the identity across updates.

### If macOS offers “Move to Trash”

Read the sentence above the buttons. The published **v0.1.0–v0.2.1** Mac releases, **0.2.2–0.2.5 Mac previews**, and ordinary CI builds use an ad-hoc signature and are **not notarized by Apple**. If the alert says Apple **cannot verify** the developer or check the app for malware, and you trust the download, try opening TalkToMe once, dismiss the alert, then open **System Settings → Privacy & Security**. Scroll to the TalkToMe entry under **Security**, click **Open Anyway**, and confirm. This approves that copy of the app. [Apple’s Gatekeeper guide](https://support.apple.com/en-us/102445)

If the alert says the app **is damaged**, download a fresh copy of the correct architecture from the release page. To compare the downloaded file with GitHub’s SHA-256 digest, run `shasum -a 256` followed by the path to the downloaded ZIP or DMG. If macOS says the app **will damage your computer**, leave it blocked and report the exact message and release version. [Apple’s explanation of these alerts](https://support.apple.com/en-us/102445)

If the icon keeps bouncing without a window, check the same **Privacy & Security** panel for a pending TalkToMe approval. Models load when you start dictation, so waiting for a model download will not resolve a launch that has not reached the settings window.

For **v0.2.1**, a launch stall can persist even after **Open Anyway**. The following temporary workaround restored startup on an M2 Max running macOS 26.7.1. Use it only for a copy from the official release that you trust: **force-quit TalkToMe first**, then run these commands in Terminal. They check the bundle signature and remove the download-quarantine marker from **TalkToMe only** before reopening it. This is a workaround for the unnotarized release; normal distribution needs Developer ID signing and Apple notarization.

```sh
codesign --verify --deep --strict --verbose=2 /Applications/TalkToMe.app &&
xattr -dr com.apple.quarantine /Applications/TalkToMe.app &&
open /Applications/TalkToMe.app
```

If the app still fails, report the macOS version and capture a three-second startup sample while the icon is bouncing: `sample TalkToMe 3 -file /tmp/TalkToMe-startup.txt`. A sample stuck entirely at `_dyld_start` points to a problem before the app's startup code; include the signature-check output to help distinguish an altered bundle from a macOS launch-assessment problem.

To build from source on a Mac, install Xcode command line tools, CMake 3.26+, Ninja and Python 3.10+, then run `bash Mac/build.sh`. The script builds the pinned whisper.cpp and llama.cpp runtimes into the app and writes `build/mac/TalkToMe.app` and a drag-to-install DMG.

## Everyday use on Windows

1. Open **TalkToMe.exe**. Closing the panel leaves the app in the notification area.
2. Turn on **Num Lock**, click where you would type, and hold **Ctrl + Numpad 0** on the numeric keypad. Speak, then release. A rising tone signals that the microphone is recording. A falling tone signals that recording has ended, including when you cancel. The recording indicator stays out of the way of keyboard focus.
3. Keep the destination field focused while TalkToMe transcribes and edits. The complete text is inserted at the current selection. Ordinary dictation never sends Enter.
4. Double-click the notification icon to see the source transcript and edit or copy the English. **Quit** releases the hotkey and the models started by the app.

![Idle waveform, recording dot, and processing dots](Desktop/Icons/artwork.svg)

The waveform means idle, one dot means recording, and three dots mean TalkToMe is processing your dictation.

**Ctrl + Numpad 0** is the default for new installations. To use it with an existing setup, open **Settings → Dictation → Recording shortcut**, select the preset, and click **Save**. You can also type a custom combination or choose **Ctrl+Alt+F9** for a keyboard without a keypad.

The panel offers **Auto**, **Russian**, and **English** input and a **Press to toggle** option. Recordings end after five minutes by default. **Ctrl+Alt+F10** cancels an active session. The panel and tray menu also offer Cancel. Open **Settings** to change either shortcut, recording limit, or audio cues.

**Review before pasting** keeps each result in the panel for manual copying. If recognition is wrong, change the input language and click **Retry processing** to reuse the recording. A retry with the same language reuses the transcript when available. All retries finish in the panel, so fixing a result cannot insert a second copy.

## Install on Windows

1. Extract **TalkToMe-Windows-x64.zip** and double-click **TalkToMe.exe**. .NET and the crash monitor are included. Speech and editing engines download automatically to a shared cache.
2. In the first-run **Models** screen, choose how to listen and what to insert. Select **Download and save**. The app shows the download size and verifies each file before using it. Enable microphone access for desktop apps in Windows if prompted.
3. When the panel says **Ready**, focus a text field, hold **Ctrl + Numpad 0**, speak, and release.

## Model choices on either platform

| In Settings → Models | Download | Result |
| --- | ---: | --- |
| Whisper Base | 61 MB including speech detector | Faster multilingual dictation on a CPU |
| Whisper Large v3 Turbo | 575 MB including speech detector | Larger multilingual recognizer |
| Parakeet TDT v3 | 714 MB | Optional multilingual speech recognizer; includes Russian and English |
| TranslateGemma 4B | 2.49 GB | Dedicated Russian → English translation; English is passed through |
| Transcript only | None beyond speech | Inserts the transcript in the spoken language |
| Local Qwen 3.5 0.8B | 563 MB | Smallest download; more omissions and instruction-following errors |
| Local Qwen 3.5 2B | 1.28 GB | Default local translator and editor for new installations |
| Local Qwen 3 4B Instruct 2507 | 2.50 GB | Optional non-thinking model for translation and editing |
| Use my own server | None beyond speech | Sends the transcript, editing instructions and glossary to your configured endpoint |

You can cancel setup and open **Settings → Models** again. Completed downloads are reused. **Verify downloaded models** checks the installed files against the catalog and replaces damaged files. Audio stays on your computer. [Whisper models](https://huggingface.co/ggerganov/whisper.cpp) · [Qwen 0.8B](https://huggingface.co/ggml-org/Qwen3.5-0.8B-GGUF) · [Qwen 2B](https://huggingface.co/unsloth/Qwen3.5-2B-GGUF) · [Qwen 4B](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF)

For fast Russian speech, try **Whisper Large v3 Turbo** and set **Dictation → Spoken language → Russian** when speaking mainly Russian. The default **English / Russian** mode restricts Whisper's language detection to these two languages and supplies software engineering vocabulary. If the source transcript is wrong, changing the translator cannot reliably recover the missing words. Qwen 2B and 4B use the same local engine and support Metal on Apple silicon.

The default editing instructions produce natural US English for software engineering discussions, preserve tone and technical details, and apply spoken self-corrections. Existing backticked code is checked before insertion. Emoji placeholders are restored exactly; prose uses ASCII punctuation and list items have no trailing periods. Custom prompts remain unchanged; choose **Editing → Restore default instructions** to adopt these rules if you saved your own prompt.

The 0.8B model produced omissions, untranslated text and explanatory prose in local checks. The 2B and 4B checks cover counting, questions, corrections, technical dictation, commands as data, negation/numbers, code, emojis and lists. These are focused regression cases, not a broad translation benchmark; formatting checks cannot detect every mistranslation. [Test inputs](Checks/TranslationCases.json)

**Parakeet TDT v3** uses the bundled [NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp) runtime and NVIDIA's Q8_0 model. It loads once and stays ready between recordings. Windows uses bundled CUDA when available, and Apple silicon uses Metal when enabled. A failed GPU load or inference retries on CPU; Intel Macs use CPU. Parakeet detects the spoken language itself. It does not use Whisper's speech detector or glossary prompt.

**TranslateGemma 4B** uses Google's dedicated translation prompt and a [Q4_K_M quantization](https://huggingface.co/mradermacher/translategemma-4b-it-GGUF). It translates Russian or mixed Russian/English text into English; English-only text stays unchanged. It does not apply editing instructions, glossary terms, or spoken corrections as editing commands. Choose Qwen for those features. Long dictations are translated in bounded chunks. Incomplete generations are rejected before insertion. [Translation checks](Checks/TranslateGemmaCases.json)

Both models download and verify inside Settings and run offline afterward. No Python, package manager, separate server, model account, or manual runtime installation is needed. Existing model selections are preserved. Parakeet is by NVIDIA under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); TranslateGemma is by Google under the [Gemma terms](https://ai.google.dev/gemma/terms). See [model notices](Shared/ModelNotices.txt).


### Advanced Windows setups

Existing preferences keep their configured Python/CUDA speech engine and server. You can switch either one in **Models**. For a new manual faster-whisper environment, `setup.ps1` (or `setup.ps1 -Cpu`) is still available and requires x64 Python 3.11 with the Windows `py` launcher. [faster-whisper runtime requirements](https://github.com/SYSTRAN/faster-whisper#requirements)

To build the Windows app, install the .NET 8 SDK, CMake 3.26+ and Visual Studio C++ x64 build tools. Run `cmake -S Desktop/Diagnostics -B build/diagnostics -A x64`, `cmake --build build/diagnostics --config Release`, then `dotnet publish Desktop/TalkToMe.csproj -c Release -r win-x64 --self-contained true -o build/app`. The app downloads its pinned runtime automatically.

Only native-engine developers need Python and CUDA Toolkit 13.0. Build with `py -3 Shared/build_engines.py build/engines --cuda`; set `TALKTOME_ENGINE_DIRECTORY` to that absolute directory for local testing. `py -3 Shared/package_windows_runtime.py` creates immutable runtime assets and their pinned manifest. See [runtime publishing](.github/workflows/README.md).

### Windows performance and diagnostics

Windows automatically uses the bundled CUDA engines on a compatible NVIDIA GPU. The runtime includes kernels for RTX 20, 30, 40 and 50 series GPUs, including Blackwell RTX 5070. Users need their NVIDIA graphics driver, but do not need Python, the CUDA toolkit, a separate server, or a runtime installer. NVIDIA driver compatibility is checked by launching a private GPU probe. Older drivers, unsupported GPUs and computers without NVIDIA graphics use the CPU automatically. AMD and Intel GPU acceleration is not included in the Windows build. macOS continues to use its bundled Metal backend.

Under **Settings → Models**, **Use NVIDIA GPU when available** is enabled by default. Turn it off to compare CPU performance. The app downloads about 13 MB of CPU engines and, on compatible NVIDIA systems, about 679 MB of CUDA engines. It verifies SHA-256 checksums and extracts them under `%LOCALAPPDATA%\TalkToMe\engines\<runtime-version>`. App updates reuse this cache, including offline. No engines folder is required beside the executable. After confirming an upgrade works, you can remove an old installation's bundled `engines` folder. Interrupted downloads do not become usable installations; a failed CUDA download falls back to CPU and retries after restart.

Speech and translation run in separate private helper processes. If a CUDA engine fails to load or stops during inference, the app stops that helper and retries the pending work once on CPU. That engine stays on CPU until its settings change or the app restarts. Cancellation stops the requested speech work without disabling GPU eligibility. Custom Python engines and user-managed translation servers keep their existing behavior.

Windows text-field inspection also uses a small bundled native helper. It uses the UI Automation COM API on an MTA thread with explicit object lifetimes, avoiding the legacy managed pattern-handle finalizers that caused `RawUiaPatternRelease` access violations. If a provider crashes or hangs, only the helper stops; automatic insertion is withheld and the result remains available to copy. The helper restarts on the next field query. Password, read-only and terminal fields remain excluded.

After processing, the main window shows speech, translation and total time. Open **Performance…** to see and copy the last 20 attempts, including retries and failures. Reports show the actual speech and translation backends, GPU device name, translation layers offloaded, and any CPU fallback reason. Reports contain model choices, CPU information, audio duration and timing, but no audio, transcript, prompt, credentials or server address. They stay in memory until the app quits. Timings include failed GPU attempts when a CPU retry was needed.

**Copy debug report** in the main window or **Settings → Diagnostics** includes persistent recent events, versions, model selections and saved crash callstacks on Windows and macOS. Use it after restarting from a crash.

On Windows, **Open diagnostics folder** opens `%LOCALAPPDATA%\TalkToMe\Diagnostics`: `events.log` contains stages and managed exception stacks; `crash-*.txt` and `.dmp` contain native crash information. The small bundled crash monitor captures second-chance failures from a separate process without administrator rights or system registry changes. Per-session `monitor-*.txt` files preserve attachment errors and process exit codes across restarts, including exits without a crash exception. Reports also include recent TalkToMe Application Error metadata from Windows (faulting module, exception code and offset), without unrelated apps or full event messages. Normal exit, forced process termination, and machine power loss may not produce a crash dump.

On macOS, logs are stored in `~/Library/Application Support/TalkToMe/Diagnostics`. Reports include microphone/Accessibility permission status, requested acceleration, processing timings and available TalkToMe crash backtraces from `~/Library/Logs/DiagnosticReports`. **Open macOS crash reports** opens that system folder. macOS may take a moment to write a crash report. Only technical fields and stack frames are copied, excluding exception messages and memory/register contents; the original system reports are left unchanged. Forced termination does not always produce a system crash report.

Text reports exclude transcripts, audio, prompt/glossary contents, API keys and exception messages. Dumps can contain process memory, so inspect them before sharing; nothing uploads automatically. Logs rotate at 2 MB, and startup retains at most three dumps within a 512 MB budget. Open a dump in WinDbg and use `!analyze -v` for native analysis; the text report provides managed exception stacks even without a debugger. Older releases did not save app-managed crash reports; Windows Event Viewer → Windows Logs → Application may still contain their faulting module and exception code.

| Timing | What it measures |
|---|---|
| `audio_finalize` | Microphone flush after releasing the shortcut |
| `speech_queue` | Waiting for a previous recognition or startup warmup |
| `speech_load` | Loading the listening model, when needed |
| `speech_decode` | Speech detection, automatic language detection and transcription |
| `translation_ready` | Starting/loading the translator or checking its readiness |
| `translation_request` | Translation HTTP request, including prompt processing and generation |
| `insertion` | Waiting to dispatch the text, or determining it must be copied |

All durations are milliseconds. Total excludes recording time and ends when text is dispatched or the result is ready to copy. Llama.cpp's `prompt_ms`, `predicted_ms`, token counts and tokens/second appear in `TranslationDetails` when provided by the server. These are parts of `translation_request`, so do not add them to the total again. Missing stages did not run; for example, a resident speech model does not need `speech_load`. Retry may reuse the transcript and is marked accordingly.

For reproducible developer measurements, build `Checks` and run `dotnet run --project Checks -c Release --no-build -- --benchmark build/model-check build/native-src/whisper.cpp/samples/jfk.wav`. It requires models installed by the `--local-models` check. The benchmark runs once cold and three times warm with 16 kHz mono PCM16 audio and changing Russian translation requests. Append `cpu` to disable CUDA or `portable` to compare the compatibility engines. Compare warm runs on the same machine without other inference or builds running. `TALKTOME_CPU=portable` also forces compatibility engines for troubleshooting.

On a machine with an NVIDIA GPU, run `dotnet run --project Checks -c Release --no-build -- --gpu-check build/model-check build/native-src/whisper.cpp/samples/jfk.wav` to check real CUDA inference, recovery after terminating the test helpers, GPU startup failure and cancellation. It only terminates helpers started by that check. `--crash-check` verifies a dump and callstacks from a disposable process; `--capture-check` simulates microphone removal and sound failures on hotkey release. The Windows CI downloads the pinned CPU runtime and tests inference, archive integrity, cancellation, offline reuse and recovery.

To use an existing llama-server, select **Use my own server** in **Models**. Open **Server**, enter its URL (for example, `http://my-gpu-host:8080/v1`), model alias and optional API key. The client calls `POST /v1/chat/completions`.

For example, on the GPU host, use a recent CUDA build of llama.cpp and a Qwen3.6-35B-A3B GGUF. Set the host to its trusted network interface. For a public or untrusted network, use an authenticated HTTPS endpoint or an SSH tunnel. [llama-server options](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

```powershell
hf download ggml-org/Qwen3.6-35B-A3B-GGUF Qwen3.6-35B-A3B-Q8_0.gguf --local-dir C:\Models\Qwen
llama-server -m C:\Models\Qwen\Qwen3.6-35B-A3B-Q8_0.gguf --alias dictation --host 192.168.1.20 --port 8080 --ctx-size 8192 --parallel 1 -ngl all --reasoning off --no-mmproj --log-disable
```

### Start a local llama-server automatically

To use another compatible chat GGUF, select **Use my own server** in **Models**. Under **Server**, enable **Start a local llama-server**, choose the GGUF, and enter an HTTP loopback URL. Leave **Custom llama-server** blank to use the included engine, or choose your own executable. For a split GGUF, choose the first shard and keep the other shards beside it. TalkToMe checks for an existing server and starts its own only when needed. Quit stops the process it started.

## Preferences and data

Open **Settings** from the result window or the menu-bar / notification-area icon. Save applies changes within the running app. Windows also saves its quick language, recording mode and review controls directly from the result window.

| Settings tab | Controls |
| --- | --- |
| Dictation | Microphone, spoken language, hold/toggle, record and cancel shortcuts, recording limit, tones, review mode |
| Models | Listening model, output language mode, optional local translator, download size and verification |
| Speech | Mac model files, Metal and CPU threads. Windows custom Python interpreter, model directory and device options |
| Server | URL, served model alias, API key, timeout, optional local executable and GGUF, context size and GPU layers |
| Editing | Editing instructions, restore-default button, temperature and personal glossary |

Mac preferences and downloaded models live under `~/Library/Application Support/TalkToMe/`. API keys go into the login Keychain for each server origin. Windows stores new preferences and models under `%LOCALAPPDATA%\TalkToMe` and continues using an existing installation’s `settings.json` if present. Saved server keys are encrypted for the current Windows user and server origin.

Speech activity detection filters silence before source-language transcription. Both clients send the transcript as data and request `enable_thinking: false`. Bundled translators also use a constrained JSON response with one final-text field. Only that decoded field can be inserted; raw commentary, extra fields, truncated/empty responses, reasoning tags and unusually large expansions are rejected. This validates the output format, not the correctness of every sentence. Custom servers keep the existing plain-text API. The recognizer stays loaded between utterances. [Default editing prompt](Shared/EnglishPrompt.txt) · [Local output format](Shared/LocalEditing.json)

Recording begins with the hotkey. The latest recording, transcript and English result live in memory. Starting a new recording replaces the previous result. Preferences and downloaded models persist between launches. Explicitly copying a result places it on the system clipboard. A separately managed server controls its own logging.

## Insertion and recovery

TalkToMe checks the foreground window, keyboard focus, and Windows UI Automation element before sending input. Switching fields, holding modifiers, protected or unidentifiable controls, and detected terminal fields can leave the result ready to copy. Use review mode for embedded terminals or applications with custom input handling. Windows integrity levels can block insertion into an elevated application.

The first insertion method is plain Unicode clipboard paste. TalkToMe saves supported clipboard formats under a lock, marks the temporary text to exclude it from Windows clipboard monitoring, and checks ownership before restoring. If the clipboard changed during processing or contains an unsupported format, standard Windows edit controls can receive a selection replacement directly. Other ordinary fields can receive printable single-line Unicode input. When a complete block cannot be sent safely, use **Copy result**. A recipient can ignore a dispatched paste, so the result remains available in the panel.

A missing model, disconnected server, or failed generation leaves available audio and transcript in the current session. Fix the dependency and click **Retry processing**. The panel reports release-to-input-dispatch time and session median/p95 for automatic insertions.

## Verification

GitHub Actions builds the small Windows app and crash monitor, downloads the pinned shared runtime and default models, and checks CPU transcription, editing and failure recovery. Native engines have a separate, manually dispatched build workflow and are reused across app releases. Temporary Windows build artifacts expire after three days.

The macOS workflow builds and verifies the app bundle separately on Apple silicon and Intel. It checks preference persistence and shortcut migration, insertion policy with simulated AX/keyboard responses, a llama-server-compatible HTTP exchange and CPU transcription of a bundled sample with Whisper and the speech detector. The insertion policy checks cover Unicode, permission failures, held modifiers, focus changes, cancellation and avoiding duplicate writes after a timeout. They do not grant CI control of the desktop; actual Accessibility permission and insertion into your own editors need a dictation test in TextEdit after first launch.

The window check uses normal AppKit termination. Additional checks quit with a speech model loaded and during inference, allowing cancellation and freeing model buffers before process teardown. Virtual CI Macs use CPU inference; Metal shutdown still needs a physical Apple silicon test.

The Windows checks use a separate editor process and can also open a temporary Chromium profile. The session check drives the desktop recording and processing lifecycle with a saved clip. WAV input for the checks must be mono 16-bit PCM at 16 kHz. Run the interactive checks while the normal app is closed and let their test windows keep focus. A plain-text or empty clipboard exercises the multiline paste path.

```powershell
dotnet run --project Checks -c Release
dotnet run --project Checks -c Release -- --clipboard
dotnet run --project Checks -c Release -- --desktop
dotnet run --project Checks -c Release -- --browser 'C:\Program Files\Google\Chrome\Application\chrome.exe'
dotnet run --project Checks -c Release -- --session C:\Samples\english.wav
dotnet run --project Checks -c Release -- --desktop C:\Samples\english.wav C:\Samples\russian.wav C:\Samples\mixed.wav
```

The [design brief](Docs/local_bilingual_dictation_implementation_brief.md) includes the proposed personal-microphone evaluation corpus and semantic acceptance criteria.

### Workstation results

Tested on a 96 GB RTX PRO 6000 Blackwell with faster-whisper 1.2.1, CTranslate2 4.6.3, CUDA/float16 ASR, and llama-server build 10318 serving the existing Qwen3.6-35B-A3B **BF16** GGUF. The published launcher started both models, registered the shortcut, reached Ready, and quit cleanly.

With seven warm synthetic utterances, the median time from completed PCM to observed text in a native editor was **0.53 s**, with **0.67 s p95**. These runs used the native edit-control insertion path and excluded the first utterance. The test clock starts with saved audio. The panel measures actual Stop-to-dispatch time when you use the microphone.

The English self-correction produced “Set the timeout to 50 milliseconds.” Russian uncertainty and negation survived translation. A Russian sentence containing RenderDoc preserved the identifier and the instruction to rerun the test. One synthetic pronunciation of “latency” became “Nathan’s,” and short spliced Russian/English audio sometimes confused automatic language detection. Personal microphone accuracy still needs testing.

Separate checks exercised a Chrome textarea with multiline Unicode, a focus change between fields in the same window, cancellation during processing, review-only retries, silence, low-level noise, and clipboard restoration. A dictated question and an instruction-like sentence were returned as text by the real Qwen server.
