"""Create a complete monophonic bundle from explicit quarter-beat spans."""
from pathlib import Path
import copy
import io
import json
import sys
import zipfile

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent
data = json.loads((root / 'candidate-alignment.json').read_text(encoding='utf-8'))
midi = json.loads((root / 'midi.json').read_text(encoding='utf-8-sig'))
score = json.loads((root / 'score.json').read_text(encoding='utf-8-sig'))
notes, tokens = data['notes'], data['tokens']
tpb = next(iter(midi['measures'].values()))['duration'] / midi['beats']
sec_tick = midi['tempos'][0]['tempo'] / 1e6 / tpb
owners = {}
for ti, token in enumerate(tokens):
    if not token.get('noteSpans'):
        raise ValueError('歌词没有音符区间，未导入：' + token['text'])
    for span in token['noteSpans']:
        for unit in range(span['startUnit'], span['endUnit']):
            key = (span['note'], unit)
            if key in owners:
                raise ValueError('歌词区间重叠，未导入')
            owners[key] = ti

for part in score['parts']:
    for measure in part['measures']:
        for helper in measure['notes']:
            helper.pop('lyrics', None)
original = {(e.get('part', 0), e.get('id'), e.get('tick')): e
            for e in midi['events'] if e.get('track') == midi['rightHandTrack']}
replacement = []
anchors = set()
split_count = 0
for ni, note in enumerate(notes):
    event = original[(note.get('part', 0), note['sourceId'], note['tick'])]
    units = max(1, round((note['scoreEnd'] - note['scoreStart']) / sec_tick / tpb * 4))
    boundaries = [0] + [u for u in range(1, units) if owners.get((ni, u)) != owners.get((ni, u - 1))] + [units]
    split_count += len(boundaries) - 2
    for part, (lower, upper) in enumerate(zip(boundaries, boundaries[1:])):
        new = copy.deepcopy(event)
        source_id = f'aligned_{ni}_{part}'
        new.update(id=source_id, elem_ids=[source_id], tick=note['tick'] + lower * tpb / 4,
                   duration=(upper - lower) * tpb / 4)
        replacement.append(new)
        helper = {'staff': 1, 'v': 1, 'elems': [{'id': source_id}]}
        ti = owners.get((ni, lower))
        if ti is not None:
            anchors.add(ti)
            helper['lyrics'] = [dict(num=0, text=tokens[ti]['text'], alignmentIndex=ti, explicitSpan=True,
                                     endLine=tokens[ti]['endLine'], needsReview=tokens[ti]['needsReview'])]
        score['parts'][event.get('part', 0)]['measures'][event['measure']]['notes'].append(helper)
if len(anchors) != len(tokens):
    raise ValueError('部分歌词无法生成播放锚点，未导入')
midi['events'] = [e for e in midi['events'] if e.get('track') != midi['rightHandTrack']] + replacement
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED) as archive:
    archive.writestr('data.ccmid', json.dumps(midi, ensure_ascii=False))
    archive.writestr('data.ccxml', json.dumps(score, ensure_ascii=False))
(root / 'aligned.ccmz').write_bytes(b'\x01' + buffer.getvalue())
data['splitNotes'] = split_count
data['unmappedTokens'] = 0
(root / 'candidate-alignment.json').write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(dict(splitNotes=split_count, unmapped=0)), flush=True)
