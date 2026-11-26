"""Prepare reproducible short singing samples; never edits the source CCMZ."""
from pathlib import Path
import json
import re
import sys
import soundfile as sf
from lyric_tokens import syllables, spoken

def lyric_lines(selected):
    lines = []
    for raw in selected['syncedLyrics'].splitlines():
        match = re.match(r'\[(\d+):(\d+(?:\.\d+)?)\](.*)', raw)
        if match:
            lines.append((int(match[1]) * 60 + float(match[2]), match[3].strip()))
    return lines


def prepare(ROOT):
    selected = json.loads((ROOT / 'selected-lyrics.json').read_text(encoding='utf-8'))
    offset_file = ROOT / 'lyric-offset.json'
    offset = json.loads(offset_file.read_text(encoding='utf-8'))['offsetSeconds'] if offset_file.exists() else 0
    lines = [(time + offset, text) for time, text in lyric_lines(selected)]
    vocal = ROOT / 'separated/htdemucs/reference/vocals.wav'
    audio, sr = sf.read(vocal if vocal.exists() else ROOT / 'audio/reference.wav')
    target = ROOT / 'segments'
    target.mkdir(exist_ok=True)
    manifest = []
    for index, (start, text) in enumerate(lines):
        if not text:
            continue
        # LRC times only crop the audio. TIFA must infer individual character times.
        has_following_words = any(syllables(text) for _, text in lines[index + 1:])
        # A final blank LRC timestamp is not a reliable end of the last vowel.
        # Let the acoustic model see the recording's tail rather than clipping it.
        end = lines[index + 1][0] if has_following_words else len(audio) / sr
        lower, upper = max(0, start - .35), min(len(audio) / sr, end + .15)
        name = f'line-{index:02d}'
        sf.write(target / (name + '.wav'), audio[round(lower * sr):round(upper * sr)], sr)
        words = syllables(text)
        if not words:
            continue
        (target / (name + '.txt')).write_text(' '.join(spoken(w) for w in words), encoding='utf-8')
        manifest.append(dict(name=name, offset=lower, end=upper, text=text, syllables=words))
    (ROOT / 'segments.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f"Prepared {len(manifest)} segments from LRCLIB {selected['id']}")


if __name__ == '__main__':
    prepare(Path(sys.argv[1]).resolve())
