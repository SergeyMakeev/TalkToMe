# TalkToMe

Dictate in Russian, English, or both and insert written English at the cursor in a Windows application. Whisper transcribes microphone audio on the PC. Qwen translates and edits each completed utterance through llama.cpp’s `llama-server`, which can run on the same PC or on another machine.

## Everyday use

1. Open **TalkToMe.cmd** after setup. Closing the panel leaves the app in the notification area.
2. Click where you would type. Hold **Ctrl+Alt+F9**, speak, then release. A rising tone signals that the microphone is recording. A falling tone signals that recording has ended, including when you cancel. The recording indicator stays out of the way of keyboard focus.
3. Keep the destination field focused while TalkToMe transcribes and edits. The complete text is inserted at the current selection. Ordinary dictation never sends Enter.
4. Double-click the notification icon to see the source transcript and edit or copy the English. **Quit** releases the hotkey and the models started by the app.

The panel offers **Auto**, **Russian**, and **English** input and a **Press to toggle** option for longer dictation. A recording ends at five minutes. **Ctrl+Alt+F10** cancels an active session when Windows can register that shortcut. The panel and tray menu also offer Cancel.

**Review before pasting** keeps each result in the panel for manual copying. If recognition is wrong, change the input language and click **Retry processing** to reuse the recording. A retry with the same language reuses the transcript when available. All retries finish in the panel, so fixing a result cannot insert a second copy.

## Install on Windows

Use an x64 installation of Python 3.11 with the Windows `py` launcher, the .NET 8 SDK with Windows Desktop support, and a microphone enabled for desktop apps. The CUDA configuration uses cuBLAS for CUDA 12 and cuDNN 9. Their DLL directories must be on PATH or supplied with `-CudaLibraryDirectory`. For CPU transcription, add `-Cpu` to setup. [faster-whisper runtime requirements](https://github.com/SYSTRAN/faster-whisper#requirements)

Run these commands in PowerShell in the repository. Setup creates a project-local Python environment, downloads the pinned Whisper large-v3-turbo CTranslate2 model, publishes the desktop executable under `build/app`, and writes personal preferences to `settings.json`. Setup can be rerun. The launcher works offline after provisioning when the configured server is reachable. [Whisper model](https://huggingface.co/dropbox-dash/faster-whisper-large-v3-turbo)

### Use an existing llama-server, local or remote

```powershell
.\setup.ps1 -ServerUrl 'http://my-gpu-host:8080/v1'
.\TalkToMe.cmd
```

Give the served model the alias `dictation`, or change `model` in `settings.json` to the server’s advertised ID. The client calls `POST /v1/chat/completions`. If the server uses an API key, set `$env:TALKTOME_API_KEY` in the shell launching the app. A remote server receives the source transcript and glossary. Audio stays on the dictating PC.

For example, on the GPU host, use a recent CUDA build of llama.cpp and a Qwen3.6-35B-A3B GGUF. Set the host to its trusted network interface. For a public or untrusted network, use an authenticated HTTPS endpoint or an SSH tunnel. [llama-server options](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)

```powershell
hf download ggml-org/Qwen3.6-35B-A3B-GGUF Qwen3.6-35B-A3B-Q8_0.gguf --local-dir C:\Models\Qwen
llama-server -m C:\Models\Qwen\Qwen3.6-35B-A3B-Q8_0.gguf --alias dictation --host 192.168.1.20 --port 8080 --ctx-size 8192 --parallel 1 -ngl all --reasoning off --no-mmproj --log-disable
```

### Start a local llama-server automatically

Supply the executable and GGUF paths at setup. For a split GGUF, give the first shard and keep the other shards beside it.

```powershell
.\setup.ps1 -LlamaServer C:\Tools\llama\llama-server.exe -QwenModel C:\Models\Qwen\Qwen3.6-35B-A3B-Q8_0.gguf
.\TalkToMe.cmd
```

With the default loopback URL, TalkToMe first checks for a running server. It starts the configured executable if needed and stops its own server on Quit. To switch this installation to a remote host, change `serverUrl` and relaunch. An externally managed server can also be used on loopback with `localServer` set to `null`.

## Preferences and data

The checked-in [example settings](settings.example.json) lists the available fields. Paths resolve relative to the repository. Language, recording mode, and review mode are saved from the panel. Quit and relaunch after manually changing the file.

| Setting | Use |
| --- | --- |
| `serverUrl`, `model`, `temperature`, `timeoutSeconds` | llama-server base URL including `/v1`, served alias, sampling temperature, and request timeout |
| `localServer` | Optional pair of `executable` and `modelFile` paths for an app-managed HTTP loopback server |
| `whisperDirectory`, `python` | Local CTranslate2 model directory and worker interpreter |
| `device`, `computeType`, `cudaLibraryDirectory` | Defaults to CUDA/float16. CPU uses `cpu` and `int8` |
| `shortcut`, `microphone` | A Windows shortcut such as `Ctrl+Alt+F9` and WinMM input-device index. `-1` uses the system default |
| `glossary` | A short JSON array of preferred names and technical terms, passed as hints to both models |
| `language`, `toggle`, `copyOnly` | Initial preferences for the three panel controls |

Speech activity detection filters silence before source-language transcription. The resident Whisper worker requires local files. The [editing prompt and response checks](Desktop/EnglishEditor.cs) send the transcript as data, request `enable_thinking: false`, and reject truncated, empty, or reasoning-bearing completions. Both models stay loaded between utterances. [Qwen model card](https://huggingface.co/Qwen/Qwen3.6-35B-A3B)

Recording begins with the hotkey. The latest audio, source, English, and timings live in memory. Starting a new recording replaces the previous result. The app writes settings only. Explicitly copying a result places it on the normal Windows clipboard. A separately managed server controls its own logging.

## Insertion and recovery

TalkToMe checks the foreground window, keyboard focus, and Windows UI Automation element before sending input. Switching fields, holding modifiers, protected or unidentifiable controls, and detected terminal fields can leave the result ready to copy. Use review mode for embedded terminals or applications with custom input handling. Windows integrity levels can block insertion into an elevated application.

The first insertion method is plain Unicode clipboard paste. TalkToMe saves supported clipboard formats under a lock, marks the temporary text to exclude it from Windows clipboard monitoring, and checks ownership before restoring. If the clipboard changed during processing or contains an unsupported format, standard Windows edit controls can receive a selection replacement directly. Other ordinary fields can receive printable single-line Unicode input. When a complete block cannot be sent safely, use **Copy English**. A recipient can ignore a dispatched paste, so the result remains available in the panel.

A missing model, disconnected server, or failed generation leaves available audio and transcript in the current session. Fix the dependency and click **Retry processing**. The panel reports release-to-input-dispatch time and session median/p95 for automatic insertions.

## Verification

GitHub Actions builds the Windows app on each push and pull request, runs the noninteractive checks, and checks the Python worker syntax. Each successful run uploads the published desktop binaries as **TalkToMe-Windows**, ready to extract into a configured checkout’s `build/app` folder.

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
