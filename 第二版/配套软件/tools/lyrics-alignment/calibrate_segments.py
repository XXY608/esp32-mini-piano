"""Measure a release's LRC lead-in from jointly inferred opening lines."""
import json
from pathlib import Path
import subprocess
import sys

import numpy as np
import soundfile as sf
import textgrid

from lyric_tokens import restore_tokens, spoken, syllables
from prepare_segments import lyric_lines


def calibrate(root, tools):
    selected = json.loads((root / 'selected-lyrics.json').read_text(encoding='utf-8'))
    lines = [(time, text) for time, text in lyric_lines(selected) if syllables(text)][:4]
    if len(lines) < 3:
        return
    audio, sr = sf.read(root / 'separated/htdemucs/reference/vocals.wav')
    lower = max(0, lines[0][0] - 8)
    all_lines = lyric_lines(selected)
    next_time = next((t for t, _ in all_lines if t > lines[-1][0]), lines[-1][0] + 8)
    upper = min(len(audio) / sr, next_time + 8)
    target, output = root / 'lead-in-segments', root / 'lead-in-aligned'
    target.mkdir(exist_ok=True)
    expected = [w for _, text in lines for w in syllables(text)]
    sf.write(target / 'opening.wav', audio[round(lower * sr):round(upper * sr)], sr)
    (target / 'opening.txt').write_text(' '.join(spoken(w) for w in expected), encoding='utf-8')
    subprocess.run([sys.executable, 'infer.py', str(target), '-m', str(tools / 'models/TIFA-1.0/model.ckpt'),
        '--g2p', str(tools / 'models/TIFA-1.0/g2p-zh.yaml'), '-l', 'zh', '-L', 'en',
        '-o', str(output), '--num-workers', '0', '--batch-size', '1', '--skip-handling', 'preserve'],
        cwd=tools / 'TIFA-main', check=True)
    grid = textgrid.TextGrid.fromFile(str(output / 'opening.TextGrid'))
    restored = restore_tokens(expected, [x for x in grid[0] if x.mark])
    cursor, anchors = 0, []
    # The final line borders the extra audio context and is not an offset anchor.
    for time, text in lines[:-1]:
        interval = restored[cursor][1]
        anchors.append(dict(lrc=time, inferred=lower + interval.minTime,
                            offset=lower + interval.minTime - time))
        cursor += len(syllables(text))
    offsets = [a['offset'] for a in anchors]
    offset = float(np.median(offsets))
    agreeing = sum(abs(x - offset) <= .5 for x in offsets)
    accepted = agreeing >= 2 and abs(offset) <= 8
    report = dict(accepted=accepted, offsetSeconds=offset if accepted and abs(offset) > .5 else 0,
                  anchors=anchors, method='joint-opening-word-alignment')
    (root / 'lyric-offset.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(report), flush=True)


if __name__ == '__main__':
    calibrate(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
