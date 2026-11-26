"""Build an application-local runtime from the tested, pinned environment.

No user songs, recordings, jobs, credentials or development harness files enter
the distribution. Dereference model-cache links so it works on another PC.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys


def copy_tree(source, target):
    shutil.copytree(source, target, ignore=shutil.ignore_patterns(
        '__pycache__', '*.pyc', '.git', '.agent_harnesses.json'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, required=True)
    parser.add_argument('--target', type=Path, required=True)
    args = parser.parse_args()
    root, target = args.workspace.resolve(), args.target.resolve()
    target.relative_to(root / 'work')
    helper = root / 'work/lyrics-alignment'
    canonical = root / 'work/HarmonicaPlayer-source/tools/lyrics-alignment'
    destination = target / 'work/lyrics-alignment'
    python = destination / 'python'
    python.mkdir(parents=True, exist_ok=False)
    base = Path(sys.base_prefix)
    print('Staging private Python and pinned packages...', flush=True)
    for item in base.iterdir():
        if item.is_file() and (item.suffix.lower() in ('.exe', '.dll') or item.name == 'LICENSE.txt'):
            shutil.copy2(item, python / item.name)
    copy_tree(base / 'DLLs', python / 'DLLs')
    shutil.copytree(base / 'Lib', python / 'Lib', ignore=shutil.ignore_patterns(
        'site-packages', '__pycache__', '*.pyc'))
    copy_tree(helper / 'venv/Lib/site-packages', python / 'Lib/site-packages')
    print('Staging helpers and dereferenced local models...', flush=True)
    # Pin import paths relative to python312.dll, ignoring registry, PYTHONPATH,
    # user packages and the build machine's Codex Python installation.
    (python / 'python312._pth').write_text(
        '.\nLib\nDLLs\nLib/site-packages\n..\n../TIFA-main\nimport site\n', encoding='utf-8')
    for item in canonical.iterdir():
        if item.suffix in ('.py', '.json'):
            shutil.copy2(item, destination / item.name)
    copy_tree(helper / 'TIFA-main', destination / 'TIFA-main')
    copy_tree(helper / 'models/TIFA-1.0', destination / 'models/TIFA-1.0')
    shutil.copy2(canonical / 'g2p-zh.yaml', destination / 'models/TIFA-1.0/g2p-zh.yaml')
    copy_tree(helper / 'models/huggingface/hub/models--adefossez--HTDemucs',
              destination / 'models/huggingface/hub/models--adefossez--HTDemucs')
    shutil.copy2(helper / 'requirements-frozen.txt', destination / 'requirements-frozen.txt')
    # App-local Microsoft release CRT: required by the shipped torch DLLs.
    # Keep their original signatures and avoid a machine-wide redist install.
    crt = Path(os.environ['SystemRoot']) / 'System32'
    crt_names = ['msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
                 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll',
                 'vcruntime140.dll', 'vcruntime140_1.dll',
                 'vcruntime140_threads.dll', 'concrt140.dll']
    for name in crt_names:
        shutil.copy2(crt / name, python / name)
    files = []
    print('Recording installed-file checksums...', flush=True)
    for item in sorted(target.rglob('*')):
        if item.is_file():
            files.append({'path': item.relative_to(target).as_posix(), 'bytes': item.stat().st_size,
                          'sha256': hashlib.file_digest(item.open('rb'), 'sha256').hexdigest()})
    (target / 'runtime-manifest.json').write_text(json.dumps({
        'pythonVersion': sys.version.split()[0], 'algorithmVersion': 14,
        'files': files}, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'files': len(files), 'bytes': sum(f['bytes'] for f in files)}, indent=2))


if __name__ == '__main__':
    main()
