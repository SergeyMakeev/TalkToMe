# TalkToMe

Dictate in Russian, English, or both and insert written English at the cursor on macOS or Windows. Whisper transcribes microphone audio on your computer. Qwen translates and edits the transcript through a local or remote llama.cpp `llama-server`.

Download a versioned build from [GitHub Releases](https://github.com/SergeyMakeev/TalkToMe/releases). Maintainers can publish from the GitHub UI or by pushing a version tag. [How to publish a release](.github/workflows/README.md)

## Install on macOS

1. Download **TalkToMe-macOS-arm64.zip** for Apple silicon or **TalkToMe-macOS-x86_64.zip** for Intel from GitHub Releases. Extract the zip and move **TalkToMe.app** to Applications. You can also download a CI build from a successful Actions run and extract the zip inside its artifact. The app requires macOS 13 or later. CI uses an ad-hoc signature. If Gatekeeper asks for approval, review the app in **System Settings → Privacy & Security**. [Mac builds](https://github.com/SergeyMakeev/TalkToMe/actions/workflows/macos.yml) · [Apple’s guide to opening downloaded apps](https://support.apple.com/en-us/102445)
2. Launch TalkToMe. In **Settings → Dictation**, grant **Microphone** access for recording and **Accessibility** access for insertion into other apps. Select a microphone or follow the system default.
3. In **Speech**, click **Download recommended models** to install Whisper large-v3-turbo and the speech detector. You can also choose existing whisper.cpp GGML files. Metal acceleration is enabled by default on Apple silicon. Intel defaults to CPU.
4. In **Server**, enter your llama-server URL including `/v1`, the model alias and an optional API key. To run Qwen on this Mac, enable **Start a local llama-server** and choose your installed executable and GGUF. Click **Save**.
5. Focus a text field, hold **Ctrl + Numpad 0**, speak, and release. On a keyboard without a numeric keypad, choose **Control+Option+F9** from **Settings → Dictation → Recording shortcut → Presets**, or type your own shortcut and save. **Control+Option+F10** cancels. Closing the window keeps the menu-bar shortcut active. Click the waveform icon to review the transcript, copy the English, open Settings, or quit.

The Mac app checks that the original editable field still has focus before replacing the selection. It can also send single-line Unicode text to compatible editors. If a field is secure, unsupported, or loses focus, the English stays in the result window. Only the explicit **Copy** buttons write to the Mac clipboard.

To build from source on a Mac, install Xcode command line tools, CMake 3.26+ and Ninja, then run `bash Mac/build.sh`. The script builds the pinned whisper.cpp runtime into the app and writes `build/mac/TalkToMe.app` and a distributable zip.

## Everyday use on Windows

1. Open **TalkToMe.cmd** after setup. Closing the panel leaves the app in the notification area.
2. Turn on **Num Lock**, click where you would type, and hold **Ctrl + Numpad 0** on the numeric keypad. Speak, then release. A rising tone signals that the microphone is recording. A falling tone signals that recording has ended, including when you cancel. The recording indicator stays out of the way of keyboard focus.
3. Keep the destination field focused while TalkToMe transcribes and edits. The complete text is inserted at the current selection. Ordinary dictation never sends Enter.
4. Double-click the notification icon to see the source transcript and edit or copy the English. **Quit** releases the hotkey and the models started by the app.

![Idle waveform, recording dot, and processing dots](Desktop/Icons/artwork.svg)

The waveform means idle, one dot means recording, and three dots mean TalkToMe is processing your dictation.

**Ctrl + Numpad 0** is the default for new installations. To use it with an existing setup, open **Settings → Dictation → Recording shortcut**, select the preset, and click **Save**. You can also type a custom combination or choose **Ctrl+Alt+F9** for a keyboard without a keypad.

The panel offers **Auto**, **Russian**, and **English** input and a **Press to toggle** option. Recordings end after five minutes by default. **Ctrl+Alt+F10** cancels an active session. The panel and tray menu also offer Cancel. Open **Settings** to change either shortcut, recording limit, or audio cues.

**Review before pasting** keeps each result in the panel for manual copying. If recognition is wrong, change the input language and click **Retry processing** to reuse the recording. A retry with the same language reuses the transcript when available. All retries finish in the panel, so fixing a result cannot insert a second copy.

## Install on Windows

Extract **TalkToMe-Windows-x64.zip** from GitHub Releases. The release includes the app and its .NET runtime. For a source checkout, install the .NET 8 SDK with Windows Desktop support.

Both installations use x64 Python 3.11 with the Windows `py` launcher and a microphone enabled for desktop apps. The CUDA configuration uses cuBLAS for CUDA 12 and cuDNN 9. Their DLL directories must be on PATH or supplied with `-CudaLibraryDirectory`. For CPU transcription, add `-Cpu` to setup. [faster-whisper runtime requirements](https://github.com/SYSTRAN/faster-whisper#requirements)

Run the commands below in PowerShell in the extracted release folder or repository. Setup creates a local Python environment, downloads the pinned Whisper model, and writes personal preferences to `settings.json`. A source checkout also builds the desktop executable. Setup can be rerun. [Whisper model](https://huggingface.co/dropbox-dash/faster-whisper-large-v3-turbo)

### Use an existing llama-server, local or remote

```powershell
.\setup.ps1
.\TalkToMe.cmd
```

Open **Settings → Server**, enter the URL (for example, `http://my-gpu-host:8080/v1`), choose the server’s model alias, and enter its optional API key. The client calls `POST /v1/chat/completions`. A remote server receives the source transcript, editing instructions and glossary. Audio stays on the dictating computer.

For example, on the GPU host, use a recent CUDA build of llama.cpp and a Qwen3.6-35B-A3B GGUF. Set the host to its trusted network interface. For a public or untrusted network, use an authenticated HTTPS endpoint or an SSH tunnel. [llama-server options](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

```powershell
hf download ggml-org/Qwen3.6-35B-A3B-GGUF Qwen3.6-35B-A3B-Q8_0.gguf --local-dir C:\Models\Qwen
llama-server -m C:\Models\Qwen\Qwen3.6-35B-A3B-Q8_0.gguf --alias dictation --host 192.168.1.20 --port 8080 --ctx-size 8192 --parallel 1 -ngl all --reasoning off --no-mmproj --log-disable
```

### Start a local llama-server automatically

In **Settings → Server**, enable **Start a local llama-server**, choose the executable and GGUF, and enter an HTTP loopback URL. For a split GGUF, choose the first shard and keep the other shards beside it. TalkToMe checks for an existing server and starts its own only when needed. Quit stops the process it started. To switch to another machine, turn off the local server option, enter the remote URL and save.

## Preferences and data

Open **Settings** from the result window or the menu-bar / notification-area icon. Save applies changes within the running app. Windows also saves its quick language, recording mode and review controls directly from the result window.

| Settings tab | Controls |
| --- | --- |
| Dictation | Microphone, spoken language, hold/toggle, record and cancel shortcuts, recording limit, tones, review mode |
| Speech | Mac model files, model download, Metal and CPU threads. Windows model directory, Python interpreter, compute device, precision and CUDA library directory |
| Server | URL, served model alias, API key, timeout, optional local executable and GGUF, context size and GPU layers |
| Editing | Editing instructions, restore-default button, temperature and personal glossary |

Mac preferences and downloaded models live under `~/Library/Application Support/TalkToMe/`. API keys go into the login Keychain for each server origin. Windows keeps preferences in the installation’s `settings.json` and encrypts a saved key for the current Windows user and server origin. Existing Windows setups can still use `TALKTOME_API_KEY` through the checkbox in **Server**.

Speech activity detection filters silence before source-language transcription. Both clients send the transcript as data, request `enable_thinking: false`, and reject truncated, empty, or reasoning-bearing completions. The recognizer stays loaded between utterances. [Default editing prompt](Shared/EnglishPrompt.txt) · [Qwen model card](https://huggingface.co/Qwen/Qwen3.6-35B-A3B)

Recording begins with the hotkey. The latest recording, transcript and English result live in memory. Starting a new recording replaces the previous result. Preferences and downloaded models persist between launches. Explicitly copying a result places it on the system clipboard. A separately managed server controls its own logging.

## Insertion and recovery

TalkToMe checks the foreground window, keyboard focus, and Windows UI Automation element before sending input. Switching fields, holding modifiers, protected or unidentifiable controls, and detected terminal fields can leave the result ready to copy. Use review mode for embedded terminals or applications with custom input handling. Windows integrity levels can block insertion into an elevated application.

The first insertion method is plain Unicode clipboard paste. TalkToMe saves supported clipboard formats under a lock, marks the temporary text to exclude it from Windows clipboard monitoring, and checks ownership before restoring. If the clipboard changed during processing or contains an unsupported format, standard Windows edit controls can receive a selection replacement directly. Other ordinary fields can receive printable single-line Unicode input. When a complete block cannot be sent safely, use **Copy English**. A recipient can ignore a dispatched paste, so the result remains available in the panel.

A missing model, disconnected server, or failed generation leaves available audio and transcript in the current session. Fix the dependency and click **Retry processing**. The panel reports release-to-input-dispatch time and session median/p95 for automatic insertions.

## Verification

GitHub Actions builds the Windows app on each push and pull request, runs the noninteractive checks, and checks the Python worker syntax. Each successful run uploads the published desktop binaries as **TalkToMe-Windows**, ready to extract into a configured checkout’s `build/app` folder.

The macOS workflow builds and verifies the app bundle separately on Apple silicon and Intel. It checks preference persistence, shortcuts, a llama-server-compatible HTTP exchange and CPU transcription of a bundled sample with Whisper and the speech detector. Microphone permission and insertion into your own editors can be checked by dictating into TextEdit after first launch.

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
