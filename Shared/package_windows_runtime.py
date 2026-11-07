"""Create immutable CPU/CUDA runtime assets, independently of app releases."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def package(source, output, manifest):
    output.mkdir(parents=True, exist_ok=True)
    packages = {}
    for variant in ('cpu', 'cuda'):
        base = source / 'cuda' if variant == 'cuda' else source
        files = sorted(p for p in base.rglob('*') if p.is_file()
                       and (variant == 'cuda' or 'cuda' not in p.relative_to(base).parts)
                       and p.name != 'TalkToMeSpeech.dll')
        required = ['TalkToMeSpeechHost.exe', 'TalkToMeParakeetHost.exe', 'llama-server.exe',
                    'nemo_speech_asr.dll', 'nemo_speech_asr_c.dll']
        if variant == 'cpu':
            required += ['avx2/' + name for name in required]
        else:
            required += ['cudart64_13.dll', 'cublas64_13.dll', 'cublasLt64_13.dll', 'CUDA-EULA.txt']
        for name in required:
            if not (base / name).is_file():
                raise RuntimeError(f'Missing {variant} runtime file: {name}')
        archive = output / f'TalkToMe-engines-windows-x64-{variant}.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as target:
            for file in files:
                entry = zipfile.ZipInfo(file.relative_to(base).as_posix(), (2026, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                with file.open('rb') as src, target.open(entry, 'w') as dest:
                    import shutil
                    shutil.copyfileobj(src, dest)
        packages[variant] = dict(name=archive.name, size=archive.stat().st_size, digest=digest(archive),
                                 files={p.relative_to(base).as_posix(): p.stat().st_size for p in files})
    identity = hashlib.sha256(''.join(p['digest'] for p in packages.values()).encode()).hexdigest()[:16]
    version = 'windows-' + identity
    for item in packages.values():
        item['url'] = f'https://github.com/SergeyMakeev/TalkToMe/releases/download/runtime-{version}/{item["name"]}'
    manifest.write_text(json.dumps(dict(version=version, packages=packages), indent=2) + '\n', encoding='utf-8')
    print(version)
    for name, item in packages.items():
        print(f'{name}: {item["size"] / 1e6:.1f} MB, sha256 {item["digest"]}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=ROOT / 'build/engines')
    parser.add_argument('--output', type=Path, default=ROOT / 'build/runtime-assets')
    parser.add_argument('--manifest', type=Path, default=ROOT / 'Shared/WindowsRuntime.json')
    args = parser.parse_args()
    package(args.source, args.output, args.manifest)
