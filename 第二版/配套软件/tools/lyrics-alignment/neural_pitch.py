"""Use CREPE only where the existing two trackers cannot corroborate a vowel."""
import json
import numpy as np
from pitch_evidence import inspect_pitch, PITCH_REVIEW_REASONS


def cross_check(root, tokens, notes, pyin_times, pyin_f0):
    pending = [(i, t) for i, t in enumerate(tokens)
               if any(reason in t['reviewReasons'] for reason in PITCH_REVIEW_REASONS)]
    if not pending:
        return []
    import librosa
    import torch
    import torchcrepe

    cache = root / 'neural-pitch.json'
    cached = json.loads(cache.read_text(encoding='utf-8')) if cache.exists() else {}
    missing = [(i, t) for i, t in pending if str(i) not in cached
        or abs(cached[str(i)]['start'] - t['start']) > .001
        or abs(cached[str(i)]['end'] - t['end']) > .001]
    if missing:
        vocal, sr = librosa.load(root / 'separated/htdemucs/reference/vocals.wav', sr=16000)
        torch.set_num_threads(4)
        for i, token in missing:
            lower = max(0, round((token['start'] - .25) * sr))
            upper = min(len(vocal), round((token['end'] + .25) * sr))
            audio = torch.from_numpy(vocal[lower:upper])[None]
            # CREPE deliberately dithers decoded pitch by up to 20 cents.
            # Seed each clip so repeated imports keep identical diagnostics.
            np.random.seed(i)
            pitch, periodicity = torchcrepe.predict(audio, sr, 160, 65, 700, 'full',
                batch_size=128, device='cpu', return_periodicity=True)
            periodicity = torchcrepe.threshold.Silence(-60.)(periodicity, audio, sr, 160)
            cached[str(i)] = dict(start=token['start'], end=token['end'],
                times=(np.arange(pitch.shape[1]) * .01 + lower / sr).tolist(),
                f0=pitch[0].numpy().tolist(), periodicity=periodicity[0].numpy().tolist())
        cache.write_text(json.dumps(cached, ensure_ascii=False), encoding='utf-8')

    rows = []
    for i, token in pending:
        item = cached[str(i)]
        times = np.array(item['times'])
        f0 = np.array(item['f0'])
        f0[np.array(item['periodicity']) < .21] = np.nan
        indices = np.searchsorted(pyin_times, times)
        indices = np.clip(indices, 1, len(pyin_times) - 1)
        indices -= abs(times - pyin_times[indices - 1]) < abs(times - pyin_times[indices])
        primary = pyin_f0[indices]
        previous = notes[tokens[i - 1]['sourceNotes'][-1]]['pitch'] if i and tokens[i - 1]['line'] == token['line'] else None
        following = notes[tokens[i + 1]['sourceNotes'][0]]['pitch'] if i + 1 < len(tokens) and tokens[i + 1]['line'] == token['line'] else None
        evidence = inspect_pitch(token, notes, times, primary, f0, previous, following)
        evidence['estimators'] = ['pYIN', 'CREPE-full']
        token['neuralPitchEvidence'] = evidence
        if evidence['status'].startswith('corroborated-'):
            token['reviewReasons'] = [r for r in token['reviewReasons'] if r not in PITCH_REVIEW_REASONS]
        token['needsReview'] = bool(token['reviewReasons'])
        rows.append(dict(index=i, text=token['text'], line=token['line'], **evidence,
            remainingReasons=list(token['reviewReasons'])))
    return rows
