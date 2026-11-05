"""Resident offline Whisper worker. stdin/stdout are UTF-8 JSON lines, one reply per request."""
import base64
import json
import os
from pathlib import Path
import sys


def reply(**values):
    print(json.dumps(values, ensure_ascii=True), flush=True)


def main():
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    model_dir, device, compute_type = sys.argv[1:4]
    for filename in ("model.bin", "tokenizer.json", "config.json"):
        if not (Path(model_dir) / filename).is_file():
            raise ValueError(f"Missing {filename} in {model_dir}. Choose the model folder in Settings → Speech.")

    import numpy as np
    from faster_whisper import WhisperModel
    from faster_whisper.vad import VadOptions, get_speech_timestamps

    model = WhisperModel(model_dir, device=device, compute_type=compute_type, local_files_only=True)
    reply(ready=True)
    for line in sys.stdin.buffer:
        try:
            request = json.loads(line)
            pcm = base64.b64decode(request["pcm"], validate=True)
            audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768
            if audio.size == 0 or not np.any(audio):
                reply(text="")
                continue
            speech = get_speech_timestamps(audio, VadOptions(min_silence_duration_ms=500))
            if not speech:
                reply(text="")
                continue
            audio = np.concatenate([audio[span["start"]:span["end"]] for span in speech])
            segments, _ = model.transcribe(
                audio, task="transcribe", language=request["language"],
                beam_size=3,
                condition_on_previous_text=False, hotwords=", ".join(request["glossary"]) or None,
            )
            reply(text=" ".join(s.text.strip() for s in segments if s.text.strip()))
        except Exception as exc:
            reply(error=str(exc))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        reply(error=str(exc))
