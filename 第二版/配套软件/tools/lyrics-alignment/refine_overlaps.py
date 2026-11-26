"""Reinfer overlapping lyric boundaries once with both lines as context."""
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import soundfile as sf
import textgrid

from lyric_tokens import restore_tokens, spoken, syllables


def overlapping_groups(segments, intervals):
    groups = []
    for i in range(1, len(segments)):
        left, right = intervals[i - 1], intervals[i]
        if left and right and segments[i - 1]['offset'] + left[-1].maxTime > segments[i]['offset'] + right[0].minTime + .001:
            if groups and groups[-1][-1] == i - 1:
                groups[-1].append(i)
            else:
                groups.append([i - 1, i])
    return groups


def expand_groups(groups, previous):
    result = []
    for group in groups:
        merged = set(group)
        while True:
            expanded = merged.union(*(set(p) for p in previous + result if merged.intersection(p)))
            if expanded == merged:
                break
            merged = expanded
        result = [p for p in result if not merged.intersection(p)]
        result.append(sorted(merged))
    return result


def main(root, tools):
    segments = json.loads((root / 'segments.json').read_text(encoding='utf-8'))
    intervals = [[x for x in textgrid.TextGrid.fromFile(str(root / 'aligned' / (s['name'] + '.TextGrid')))[0] if x.mark]
                 for s in segments]
    groups = overlapping_groups(segments, intervals)
    overrides = {}
    previous = []
    round_index = 0
    while groups:
        groups = expand_groups(groups, previous)
        if any(group in previous for group in groups):
            raise ValueError('联合推断后仍存在逐字时间重叠，未导入不完整结果')
        source = root / 'separated/htdemucs/reference/vocals.wav'
        audio, sr = sf.read(source)
        target = root / 'context-segments' / f'round-{round_index}'
        output = root / 'context-aligned' / f'round-{round_index}'
        target.mkdir(parents=True, exist_ok=True)
        for gi, indices in enumerate(groups):
            lower, upper = segments[indices[0]]['offset'], segments[indices[-1]]['end']
            words = [w for i in indices for w in segments[i].get('syllables', syllables(segments[i]['text']))]
            sf.write(target / f'context-{gi}.wav', audio[round(lower * sr):round(upper * sr)], sr)
            (target / f'context-{gi}.txt').write_text(' '.join(spoken(w) for w in words), encoding='utf-8')
        subprocess.run([sys.executable, 'infer.py', str(target), '-m', str(tools / 'models/TIFA-1.0/model.ckpt'),
                        '--g2p', str(tools / 'models/TIFA-1.0/g2p-zh.yaml'), '-l', 'zh', '-L', 'en',
                        '-o', str(output), '--num-workers', '0', '--batch-size', '1', '--skip-handling', 'preserve'],
                       cwd=tools / 'TIFA-main', check=True)
        for gi, indices in enumerate(groups):
            grid = textgrid.TextGrid.fromFile(str(output / f'context-{gi}.TextGrid'))
            actual = [x for x in grid[0] if x.mark]
            expected = [w for i in indices for w in segments[i].get('syllables', syllables(segments[i]['text']))]
            restored = restore_tokens(expected, actual)
            offset = segments[indices[0]]['offset']
            cursor = 0
            for si in indices:
                count = len(segments[si].get('syllables', syllables(segments[si]['text'])))
                tokens = []
                for word, interval in restored[cursor:cursor + count]:
                    phones = [p for p in grid[2] if p.mark and p.maxTime > interval.minTime and p.minTime < interval.maxTime]
                    tokens.append(dict(text=word, start=offset + interval.minTime, end=offset + interval.maxTime,
                                       vowelStart=offset + (phones[-1].minTime if phones else interval.minTime)))
                overrides[segments[si]['name']] = tokens
                cursor += count
        previous = [p for p in previous if not any(set(p).intersection(g) for g in groups)] + groups
        current = [[SimpleNamespace(minTime=t['start'] - s['offset'], maxTime=t['end'] - s['offset'])
                    for t in overrides[s['name']]] if s['name'] in overrides else original
                   for s, original in zip(segments, intervals)]
        groups = overlapping_groups(segments, current)
        round_index += 1
    (root / 'refined-tokens.json').write_text(json.dumps(overrides, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Context refinement: {round_index} rounds, {len(previous)} context groups, {len(overrides)} lyric lines', flush=True)


if __name__ == '__main__':
    main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
