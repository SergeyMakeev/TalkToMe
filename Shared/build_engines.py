"""Build the private native runtimes during packaging. End users only download models."""
import argparse
import os
import platform
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "build"
WINDOWS = os.name == "nt"


def run(*args):
    subprocess.run(args, check=True)


def source(name, revision, repository=None):
    path = BUILD / "native-src" / name
    path.mkdir(parents=True, exist_ok=True)
    if not (path / ".git").exists():
        run("git", "init", "-q", path)
    head = subprocess.run(["git", "-C", path, "rev-parse", "--verify", "HEAD"],
                          capture_output=True, text=True)
    if head.stdout.strip() != revision:
        run("git", "-C", path, "fetch", "--depth", "1", repository or f"https://github.com/ggml-org/{name}.git", revision)
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
    return directory


def parakeet(output, avx2=False, cuda=False, cuda_options=()):
    nemo = source("nemo-speech", "536b17a41bef62d2a67a40ed3df61df26a4389fe", "https://github.com/NVIDIA/NeMo-Speech.cpp.git")
    run("git", "-C", nemo, "submodule", "update", "--init", "--depth", "1", "llama.cpp")
    spm = source("sentencepiece", "31646a467d2051eb904e0b45de3a73e91fe1c1e3", "https://github.com/google/sentencepiece.git")
    spm_build = BUILD / "native" / "sentencepiece"
    prefix = BUILD / "native" / "dependencies" / "sentencepiece"
    generator = ["-A", "x64"] if WINDOWS else ["-G", "Ninja"]
    run("cmake", "-S", spm, "-B", spm_build, *generator, "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
        "-DCMAKE_BUILD_TYPE=Release", "-DSPM_ENABLE_SHARED=OFF", "-DSPM_ENABLE_TCMALLOC=OFF",
        "-DSPM_ENABLE_MSVC_MT_BUILD=ON", "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded",
        "-DCMAKE_POSITION_INDEPENDENT_CODE=ON", f"-DCMAKE_INSTALL_PREFIX={prefix}")
    run("cmake", "--build", spm_build, "--config", "Release", "--parallel", "6")
    run("cmake", "--install", spm_build, "--config", "Release")
    options = [f"-DCMAKE_PROJECT_nemo_speech_INCLUDE={ROOT / 'Shared/ParakeetHost.cmake'}",
               f"-DNEMO_SPEECH_DEPENDENCY_PREFIX={prefix.parent}", f"-DCMAKE_PREFIX_PATH={prefix}",
               "-DCMAKE_BUILD_WITH_INSTALL_RPATH=ON", "-DNEMO_SPEECH_BUILD_ASR=ON",
               "-DGGML_METAL=" + ("ON" if platform.system() == "Darwin" and platform.machine() == "arm64" else "OFF"), *cuda_options]
    for feature in ("DIAR", "TTS", "NMT", "S2S", "SPEECHLM", "CLI", "MIC_CAPTURE", "HTTP", "GRPC", "TESTS", "EXAMPLES", "TOOLS"):
        options.append(f"-DNEMO_SPEECH_BUILD_{feature}=OFF")
    options += ["-DBUILD_TESTING=OFF", "-DNEMO_SPEECH_WITH_FLASHLIGHT=OFF", "-DNEMO_SPEECH_WITH_NORM=OFF"]
    directory = build(nemo, "TalkToMeParakeetHost", output,
                      "TalkToMeParakeetHost.exe" if WINDOWS else "TalkToMeParakeetHost", options, avx2, cuda)
    # The public NeMo SDK is shared even when ggml and SentencePiece are static.
    for library in (directory / "bin").rglob("*.dll" if WINDOWS else "*.dylib"):
        shutil.copy2(library, output / library.name)
    for name in ("LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"):
        shutil.copy2(nemo / name, output / ("NeMo-Speech-" + name + ".txt"))
    shutil.copy2(spm / "LICENSE", output / "SentencePiece-LICENSE.txt")
    for dependency in ("protobuf-lite", "absl", "darts_clone", "esaxx"):
        shutil.copy2(spm / "third_party" / dependency / "LICENSE", output / ("SentencePiece-" + dependency + "-LICENSE.txt"))
    shutil.copy2(nemo / "llama.cpp/LICENSE", output / "NeMo-ggml-LICENSE.txt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--only-avx2", action="store_true", help="Rebuild only the optimized Windows engines")
    parser.add_argument("--only-cuda", action="store_true", help="Rebuild only the NVIDIA Windows engines")
    parser.add_argument("--cuda", action="store_true", help="Include NVIDIA engines and CUDA runtime libraries (requires CUDA 13.0)")
    parser.add_argument("--engine", choices=("all", "llama", "whisper", "parakeet"), default="all", help="Build one runtime independently")
    args = parser.parse_args()
    if (args.only_avx2 or args.only_cuda or args.cuda) and not WINDOWS:
        parser.error("CPU variants and CUDA builds are only available for Windows")
    if args.only_avx2 and args.only_cuda:
        parser.error("Choose one incremental build variant")
    args.output.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ROOT / "Shared/ModelNotices.txt", args.output / "ModelNotices.txt")
    shutil.copy2(ROOT / "Shared/nlohmann-json-LICENSE.txt", args.output / "nlohmann-json-LICENSE.txt")
    llama = source("llama.cpp", "4cf5cab65d5257be31e7623eb552b1861e969c75")
    selected = lambda name: args.engine in ("all", name)
    if not (args.only_avx2 or args.only_cuda):
        if selected("llama"): build(llama, "llama-server", args.output, "llama-server.exe" if WINDOWS else "llama-server",
              ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF"])
        if selected("parakeet"): parakeet(args.output)
    shutil.copy2(llama / "LICENSE", args.output / "llama.cpp-LICENSE.txt")
    if WINDOWS:
        whisper = source("whisper.cpp", "2eeeba56e9edd762b4b38467bab96c2517163158")
        speech_options = [f"-DWHISPER_SOURCE={whisper}", f"-DLLAMA_SOURCE={llama}"]
        if selected("whisper") and not (args.only_avx2 or args.only_cuda):
            build(ROOT / "Desktop/Native", "TalkToMeSpeechHost", args.output, "TalkToMeSpeechHost.exe", speech_options)
        optimized = args.output / "avx2"
        optimized.mkdir(exist_ok=True)
        if not args.only_cuda:
            if selected("llama"): build(llama, "llama-server", optimized, "llama-server.exe",
                  ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF"], avx2=True)
            if selected("whisper"): build(ROOT / "Desktop/Native", "TalkToMeSpeechHost", optimized, "TalkToMeSpeechHost.exe", speech_options, avx2=True)
            if selected("parakeet"): parakeet(optimized, avx2=True)
        if args.cuda or args.only_cuda:
            gpu = args.output / "cuda"
            gpu.mkdir(exist_ok=True)
            toolkit = Path(os.environ.get("CUDA_PATH", "C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.0"))
            cuda_bin = toolkit / "bin" / "x64"
            if not cuda_bin.is_dir():
                cuda_bin = toolkit / "bin"
            for name in ("cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"):
                shutil.copy2(cuda_bin / name, gpu / name)
            # Minimal CUDA installers omit documentation, including the EULA.
            # Keep the license in every distribution even with that CI install.
            cuda_license = toolkit / "EULA.txt"
            if not cuda_license.is_file():
                cuda_license = ROOT / "Shared/CUDA-13.0-EULA.txt"
            shutil.copy2(cuda_license, gpu / "CUDA-EULA.txt")
            cuda_options = [f"-DCUDAToolkit_ROOT={toolkit}", f"-DCMAKE_CUDA_COMPILER={toolkit / 'bin/nvcc.exe'}"]
            if selected("llama"): build(llama, "llama-server", gpu, "llama-server.exe",
                  ["-DLLAMA_BUILD_UI=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_BUILD_TESTS=OFF", *cuda_options], avx2=True, cuda=True)
            if selected("whisper"): build(ROOT / "Desktop/Native", "TalkToMeSpeechHost", gpu, "TalkToMeSpeechHost.exe",
                  [*speech_options, *cuda_options], avx2=True, cuda=True)
            if selected("parakeet"): parakeet(gpu, avx2=True, cuda=True, cuda_options=cuda_options)
        shutil.copy2(whisper / "LICENSE", args.output / "whisper.cpp-LICENSE.txt")
        shutil.copy2(ROOT / "Shared/nlohmann-json-LICENSE.txt", args.output / "nlohmann-json-LICENSE.txt")
