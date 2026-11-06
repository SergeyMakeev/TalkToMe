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


def build(src, target, output, filename, options, avx2=False, cuda=False):
    directory = BUILD / "native" / (target + ("-cuda-vs17" if cuda else "-avx2" if avx2 else ""))
    generator = ["-A", "x64"] if WINDOWS else ["-G", "Ninja"]
    if cuda:
        toolkit = next(o.split("=", 1)[1] for o in options if o.startswith("-DCUDAToolkit_ROOT="))
        generator = ["-G", "Visual Studio 17 2022", "-A", "x64", "-T", f"cuda={toolkit}"]
    flags = ["-DBUILD_SHARED_LIBS=OFF", "-DGGML_NATIVE=OFF", "-DGGML_OPENMP=OFF",
             "-DCMAKE_BUILD_TYPE=Release", "-DGGML_METAL_EMBED_LIBRARY=ON",
             "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded",
             f"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE={directory / 'bin'}"]
    if cuda:
        flags += ["-DGGML_CUDA=ON", "-DCMAKE_CUDA_ARCHITECTURES=75-real;86-real;89-real;120-real;120-virtual",
                  "-DGGML_CUDA_NCCL=OFF", "-DGGML_CUDA_NO_VMM=ON", "-DCMAKE_CUDA_RUNTIME_LIBRARY=Static"]
    # Portable CPU kernels for the x64 release, including Intel Macs without AVX2.
    for feature in ("AVX", "AVX2", "BMI2", "FMA", "F16C"):
        flags.append(f"-DGGML_{feature}={'ON' if avx2 else 'OFF'}")
    run("cmake", "-S", src, "-B", directory, *generator, *flags, *options)
    jobs = str(min(6, os.cpu_count() or 1))
    run("cmake", "--build", directory, "--config", "Release", "--target", target, "--parallel", jobs)
    shutil.copy2(directory / "bin" / filename, output / filename)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--only-avx2", action="store_true", help="Rebuild only the optimized Windows engines")
    parser.add_argument("--only-cuda", action="store_true", help="Rebuild only the NVIDIA Windows engines")
    parser.add_argument("--cuda", action="store_true", help="Include NVIDIA engines and CUDA runtime libraries (requires CUDA 13.0)")
    args = parser.parse_args()
    if (args.only_avx2 or args.only_cuda or args.cuda) and not WINDOWS:
        parser.error("CPU variants and CUDA builds are only available for Windows")
    if args.only_avx2 and args.only_cuda:
        parser.error("Choose one incremental build variant")
    args.output.mkdir(parents=True, exist_ok=True)
    llama = source("llama.cpp", "4cf5cab65d5257be31e7623eb552b1861e969c75")
    if not (args.only_avx2 or args.only_cuda):
        build(llama, "llama-server", args.output, "llama-server.exe" if WINDOWS else "llama-server",
              ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF"])
    shutil.copy2(llama / "LICENSE", args.output / "llama.cpp-LICENSE.txt")
    if WINDOWS:
        whisper = source("whisper.cpp", "2eeeba56e9edd762b4b38467bab96c2517163158")
        speech_options = [f"-DWHISPER_SOURCE={whisper}", f"-DLLAMA_SOURCE={llama}"]
        if not (args.only_avx2 or args.only_cuda):
            build(ROOT / "Desktop/Native", "TalkToMeSpeechHost", args.output, "TalkToMeSpeechHost.exe", speech_options)
        optimized = args.output / "avx2"
        optimized.mkdir(exist_ok=True)
        if not args.only_cuda:
            build(llama, "llama-server", optimized, "llama-server.exe",
                  ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF"], avx2=True)
            build(ROOT / "Desktop/Native", "TalkToMeSpeechHost", optimized, "TalkToMeSpeechHost.exe", speech_options, avx2=True)
        if args.cuda or args.only_cuda:
            gpu = args.output / "cuda"
            gpu.mkdir(exist_ok=True)
            toolkit = Path(os.environ.get("CUDA_PATH", "C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.0"))
            cuda_bin = toolkit / "bin" / "x64"
            if not cuda_bin.is_dir():
                cuda_bin = toolkit / "bin"
            for name in ("cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"):
                shutil.copy2(cuda_bin / name, gpu / name)
            shutil.copy2(toolkit / "EULA.txt", gpu / "CUDA-EULA.txt")
            cuda_options = [f"-DCUDAToolkit_ROOT={toolkit}", f"-DCMAKE_CUDA_COMPILER={toolkit / 'bin/nvcc.exe'}"]
            build(llama, "llama-server", gpu, "llama-server.exe",
                  ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF", *cuda_options], avx2=True, cuda=True)
            build(ROOT / "Desktop/Native", "TalkToMeSpeechHost", gpu, "TalkToMeSpeechHost.exe",
                  [*speech_options, *cuda_options], avx2=True, cuda=True)
        shutil.copy2(whisper / "LICENSE", args.output / "whisper.cpp-LICENSE.txt")
        shutil.copy2(ROOT / "Shared/nlohmann-json-LICENSE.txt", args.output / "nlohmann-json-LICENSE.txt")
