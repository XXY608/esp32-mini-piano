"""Leave each reliable word out of timing prediction on saved real recordings.

These are internal timing/false-proposal checks, not human lyric accuracy.
The target's onset and pitch never enter either model's training inputs.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from position_evidence import pitched_anchors, fit_phonetic_times, timing_model, unique_position, with_anchor_capacity


def summary(rows):
    return dict(count=len(rows),
                baselineMeanErrorMs=float(np.mean([r['oldError'] for r in rows]) * 1000) if rows else None,
                phoneticMeanErrorMs=float(np.mean([r['newError'] for r in rows]) * 1000) if rows else None,
                baselineP90ErrorMs=float(np.percentile([r['oldError'] for r in rows], 90) * 1000) if rows else None,
                phoneticP90ErrorMs=float(np.percentile([r['newError'] for r in rows], 90) * 1000) if rows else None)


def evaluate(job):
    raw = (job / 'candidate-alignment.json').read_bytes()
    data = json.loads(raw)
    tokens, notes = data['tokens'], data['notes']
    midi = json.loads((job / 'midi.json').read_text(encoding='utf-8-sig'))
    unit_seconds = midi['tempos'][0]['tempo'] / 1e6 / 4
    primary = np.load(job / 'audio-features.npz')
    secondary = np.load(job / 'pyin-pitch.npz')
    anchors = pitched_anchors(tokens, notes, primary['ftime'], primary['f0'], secondary['times'], secondary['f0'])
    rows, wrong_old, wrong_new = [], [], []
    for i in anchors:
        left = [a for a in anchors if i - 10 <= a < i][-3:]
        right = [a for a in anchors if i < a <= i + 10][:3]
        old_left = [a for a in anchors if i - 8 <= a < i][-2:]
        old_right = [a for a in anchors if i < a <= i + 8][:2]
        if len(left) != 3 or len(right) != 3 or len(old_left) != 2 or len(old_right) != 2:
            continue
        ids = left + right
        x = np.array([notes[tokens[a]['sourceNotes'][0]]['scoreStart'] for a in ids])
        y = np.array([tokens[a]['start'] for a in ids])
        z = np.array([tokens[a]['vowelStart'] - tokens[a]['start'] for a in ids])
        parameters = fit_phonetic_times(x, y, z)
        if parameters is None:
            continue
        old_ids = old_left + old_right
        ox = np.array([notes[tokens[a]['sourceNotes'][0]]['scoreStart'] for a in old_ids])
        oy = np.array([tokens[a]['start'] for a in old_ids])
        old_prediction = float(np.polyval(np.polyfit(ox, oy, 1), notes[tokens[i]['sourceNotes'][0]]['scoreStart']))
        a, b, c = parameters
        lead = tokens[i]['vowelStart'] - tokens[i]['start']
        prediction = a * notes[tokens[i]['sourceNotes'][0]]['scoreStart'] + b + c * lead
        model = timing_model(tokens, notes, ids, phonetic=True)
        eligible = model is not None and min(z) - .04 - 1e-6 <= lead <= max(z) + .04 + 1e-6
        rows.append(dict(index=i, text=tokens[i]['text'], oldError=abs(old_prediction - tokens[i]['start']),
                         newError=float(abs(prediction - tokens[i]['start'])), validated=bool(eligible)))
        for candidate_model, errors in ((timing_model(tokens, notes, old_ids), wrong_old), (model, wrong_new)):
            proposal = unique_position(tokens[i], notes, with_anchor_capacity(candidate_model, tokens, notes, unit_seconds))
            if proposal is not None and proposal['best'][1] != tokens[i]['sourceNotes'][0]:
                errors.append(dict(index=i, proposed=proposal['best'][1], expected=tokens[i]['sourceNotes'][0]))
    return dict(job=str(job.resolve()), candidateSha256=hashlib.sha256(raw).hexdigest(), anchors=len(anchors),
                allComparable=summary(rows), crossValidated=summary([r for r in rows if r['validated']]),
                wrongBaselineProposals=wrong_old, wrongPhoneticProposals=wrong_new, rows=rows)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('jobs', nargs='+', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    results = [evaluate(job) for job in args.jobs]
    report = dict(modelSha256=hashlib.sha256((Path(__file__).resolve().parents[1] / 'position_evidence.py').read_bytes()).hexdigest(),
                  evaluation='held-out-word-timing-and-source-onset-proposals', results=results)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    for result in results:
        print(json.dumps({k: v for k, v in result.items() if k != 'rows'}, ensure_ascii=False))
    if any(r['wrongPhoneticProposals'] for r in results):
        raise SystemExit('Phonetic model proposed a wrong source position on a reliable held-out word.')
