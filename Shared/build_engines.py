"""Build the private native runtimes during packaging. End users only download models."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "build"
WINDOWS = os.name == "nt"


def run(*args):
    subprocess.run(args, check=True)


def source(name, revision):
    path = BUILD / "native-src" / name
    path.mkdir(parents=True, exist_ok=True)
    if not (path / ".git").exists():
        run("git", "init", "-q", path)
    head = subprocess.run(["git", "-C", path, "rev-parse", "--verify", "HEAD"],
                          capture_output=True, text=True)
    if head.stdout.strip() != revision:
        run("git", "-C", path, "fetch", "--depth", "1", f"https://github.com/ggml-org/{name}.git", revision)
        run("git", "-C", path, "checkout", "--detach", revision)
    return path


def build(src, target, output, filename, options):
    directory = BUILD / "native" / target
    generator = ["-A", "x64"] if WINDOWS else ["-G", "Ninja"]
    flags = ["-DBUILD_SHARED_LIBS=OFF", "-DGGML_NATIVE=OFF", "-DGGML_OPENMP=OFF",
             "-DCMAKE_BUILD_TYPE=Release", "-DGGML_METAL_EMBED_LIBRARY=ON",
             "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded",
             f"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE={directory / 'bin'}"]
    # Portable CPU kernels for the x64 release, including Intel Macs without AVX2.
    for feature in ("AVX", "AVX2", "BMI2", "FMA", "F16C"):
        flags.append(f"-DGGML_{feature}=OFF")
    run("cmake", "-S", src, "-B", directory, *generator, *flags, *options)
    run("cmake", "--build", directory, "--config", "Release", "--target", target, "--parallel", "6")
    shutil.copy2(directory / "bin" / filename, output / filename)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    llama = source("llama.cpp", "4cf5cab65d5257be31e7623eb552b1861e969c75")
    build(llama, "llama-server", args.output, "llama-server.exe" if WINDOWS else "llama-server",
          ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF"])
    shutil.copy2(llama / "LICENSE", args.output / "llama.cpp-LICENSE.txt")
    if WINDOWS:
        whisper = source("whisper.cpp", "2eeeba56e9edd762b4b38467bab96c2517163158")
        build(ROOT / "Desktop/Native", "TalkToMeSpeech", args.output, "TalkToMeSpeech.dll",
              [f"-DWHISPER_SOURCE={whisper}"])
        shutil.copy2(whisper / "LICENSE", args.output / "whisper.cpp-LICENSE.txt")
