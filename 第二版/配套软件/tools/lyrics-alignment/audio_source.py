"""Reuse a previously downloaded source only when its URL and hash agree."""
import hashlib
import json


def cached_recording(tools, url):
    manifests = list((tools / 'jobs').glob('*/audio/provenance.json'))
    for manifest in sorted(manifests, key=lambda p: p.stat().st_mtime, reverse=True):
        try:
            provenance = json.loads(manifest.read_text(encoding='utf-8'))
            if provenance.get('webpage_url') != url or not provenance.get('sha256'):
                continue
            for recording in manifest.parent.glob('reference.*'):
                if recording.suffix in ('.wav', '.part', '.ytdl') or not recording.is_file():
                    continue
                if hashlib.sha256(recording.read_bytes()).hexdigest() == provenance['sha256']:
                    return recording, provenance
        except (OSError, ValueError):
            continue
    return None
