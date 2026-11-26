"""Check local positions using pitched neighbours and independently timed words.

The target word's F0 is excluded. Four pitch-checked neighbours calibrate local
score time; a six-neighbour model can additionally learn consonant timing after
held-out validation. All competing onsets and model conflicts are checked.
This validates position, not intonation, and never moves a word or score pitch.
"""
import numpy as np

from pitch_evidence import inspect_pitch


def pitched_anchors(tokens, notes, primary_times, primary_f0, times, f0):
    indices = np.searchsorted(primary_times, times)
    indices = np.clip(indices, 1, len(primary_times) - 1)
    indices -= abs(times - primary_times[indices - 1]) < abs(times - primary_times[indices])
    primary = primary_f0[indices]
    anchors = []
    for i, token in enumerate(tokens):
        if token['needsReview'] or token['pitchSupportFraction'] < .65 or len(token['noteSpans']) != 1:
            continue
        if token['noteSpans'][0]['startUnit']:
            continue
        if inspect_pitch(token, notes, times, primary, f0)['status'] == 'corroborated-target':
            anchors.append(i)
    return anchors


def fit_phonetic_times(x, y, consonants):
    """A pitched attack may lie between consonant onset and vowel onset.

    Learn that fraction from neighbouring performed words, bounded to this
    physical interval. Center score time to avoid numerical conditioning on
    long recordings. No target-word time or pitch enters the fit.
    """
    center = float(np.mean(x))
    matrix = np.column_stack([x - center, np.ones(len(x)), consonants])
    if np.linalg.matrix_rank(matrix) < 3:
        return None
    slope, intercept, coefficient = np.linalg.lstsq(matrix, y, rcond=None)[0]
    intercept -= slope * center
    if not -1 <= coefficient <= 0:
        coefficient = float(np.clip(coefficient, -1, 0))
        slope, intercept = np.polyfit(x, y - coefficient * consonants, 1)
    return float(slope), float(intercept), float(coefficient)


def timing_model(tokens, notes, indices, phonetic=False):
    x = np.array([notes[tokens[a]['noteSpans'][0]['note']]['scoreStart'] for a in indices])
    y = np.array([tokens[a]['start'] for a in indices])
    z = np.array([tokens[a]['vowelStart'] - tokens[a]['start'] for a in indices])
    if np.any(np.diff(x) <= 0) or np.any(np.diff(y) <= 0):
        return None
    if phonetic:
        if np.any(z < -1e-6) or any(tokens[a]['vowelStart'] > tokens[a]['end'] for a in indices):
            return None
        parameters = fit_phonetic_times(x, y, z)
        if parameters is None:
            return None
        # Hold out each of the four inner anchors. Fitting six points well
        # is insufficient: the model must predict unseen word onsets too.
        errors = []
        for j in range(1, len(indices) - 1):
            held_out = fit_phonetic_times(np.delete(x, j), np.delete(y, j), np.delete(z, j))
            if held_out is None:
                return None
            a, b, c = held_out
            errors.append(abs(a * x[j] + b + c * z[j] - y[j]))
        validation = float(max(errors))
    else:
        a, b = np.polyfit(x, y, 1)
        parameters = float(a), float(b), 0.
        validation = float(np.max(abs(a * x + b - y)))
    a, b, c = parameters
    if a <= 0 or validation > .12:
        return None
    return dict(indices=indices, x=x, y=y, z=z, parameters=parameters,
                validation=validation, phonetic=phonetic,
                residual=float(np.max(abs(a * x + b + c * z - y))))


def with_anchor_capacity(model, tokens, notes, seconds_per_unit):
    """A frozen anchor's single grid unit cannot hold another syllable.

    Longer notes may be shared. Only the model's existing anchor dependencies
    reserve units, so editing any owner invalidates the saved evidence too.
    """
    if model is None:
        return None
    occupied = {}
    for index in model['indices']:
        span = tokens[index]['noteSpans'][0]
        ni = span['note']
        units = max(1, round((notes[ni]['scoreEnd'] - notes[ni]['scoreStart']) / seconds_per_unit))
        if units == 1 and span['startUnit'] == 0 and span['endUnit'] == 1:
            occupied[ni] = index
    return dict(model, occupiedSingleUnitNotes=occupied)


def unique_position(token, notes, model):
    if model is None:
        return None
    a, b, c = model['parameters']
    consonant = token['vowelStart'] - token['start']
    if model['phonetic'] and not min(model['z']) - .04 - 1e-6 <= consonant <= max(model['z']) + .04 + 1e-6:
        return None
    uncertainty = .04 + model['validation']
    candidates = sorted((abs(a * n['scoreStart'] + b + c * consonant - token['start']), ni)
                        for ni, n in enumerate(notes) if model['x'][0] < n['scoreStart'] < model['x'][-1]
                        and ni not in model.get('occupiedSingleUnitNotes', {}))
    if len(candidates) < 2:
        return None
    best, alternative = candidates[:2]
    if best[0] > .12 or alternative[0] - best[0] <= 2 * uncertainty:
        return None
    if token['end'] > a * notes[best[1]]['scoreEnd'] + b + c * consonant + uncertainty:
        return None
    return dict(model=model, best=best, alternative=alternative, uncertainty=uncertainty)


def corroborate_positions(tokens, notes, primary_times, primary_f0, times, f0,
                         seconds_per_unit):
    # Freeze anchors first. A newly inferred word cannot validate the next
    # word and turn an uncertain run into its own evidence.
    anchors = pitched_anchors(tokens, notes, primary_times, primary_f0, times, f0)

    rows = []
    for i, token in enumerate(tokens):
        if not token['needsReview'] or len(token['noteSpans']) != 1:
            continue
        span = token['noteSpans'][0]
        note_index = span['note']
        note = notes[note_index]
        full_units = max(1, round((note['scoreEnd'] - note['scoreStart']) / seconds_per_unit))
        # An onset cannot prove the division of a shared note, or all ranges
        # of a melisma. Those still need direct evidence.
        if span['startUnit'] or span['endUnit'] != full_units:
            continue
        left = [a for a in anchors if i - 8 <= a < i][-2:]
        right = [a for a in anchors if i < a <= i + 8][:2]
        models = []
        if len(left) == len(right) == 2:
            models.append(with_anchor_capacity(timing_model(tokens, notes, left + right),
                                               tokens, notes, seconds_per_unit))
        left = [a for a in anchors if i - 10 <= a < i][-3:]
        right = [a for a in anchors if i < a <= i + 10][:3]
        if len(left) == len(right) == 3:
            models.append(with_anchor_capacity(timing_model(tokens, notes, left + right, phonetic=True),
                                               tokens, notes, seconds_per_unit))
        proposals = [p for m in models if (p := unique_position(token, notes, m)) is not None]
        # A confident competing model is a conflict, not permission to pick
        # whichever model agrees with the current assignment.
        if not proposals or any(p['best'][1] != note_index for p in proposals):
            continue
        proposal = proposals[0]
        model = proposal['model']
        surrounding, x, y = model['indices'], model['x'], model['y']
        slope, intercept, coefficient = model['parameters']
        best, alternative = proposal['best'], proposal['alternative']
        original = list(token['reviewReasons'])
        # Timing cannot prove that a syllable was actually sung in a silent
        # interval. Only a sourced labelled score may resolve that case.
        remaining = [r for r in original if r not in ('vowel-pitch-mismatch', 'distant-onset')]
        if remaining == original:
            continue
        evidence = dict(status='corroborated-local-position',
                        method=('six-pitched-anchors-and-phonetic-onset' if model['phonetic']
                                else 'four-pitched-anchors-and-unique-phoneme-onset'),
                        anchorIndices=surrounding,
                        anchorSourceNotes=[tokens[a]['noteSpans'][0]['note'] for a in surrounding],
                        anchorScoreTimes=x.tolist(), anchorAudioTimes=y.tolist(),
                        localTempoScale=float(slope), localOffsetSeconds=float(intercept),
                        maximumAnchorResidualSeconds=model['residual'], timingUncertaintySeconds=proposal['uncertainty'],
                        phoneticLeadScale=-coefficient,
                        heldOutMaximumErrorSeconds=model['validation'] if model['phonetic'] else None,
                        sourceNote=note_index, onsetErrorSeconds=float(best[0]),
                        alternativeSourceNote=alternative[1], alternativeErrorSeconds=float(alternative[0]),
                        changesAssignment=False, acousticReasonsRetained=original)
        if model.get('occupiedSingleUnitNotes'):
            evidence['occupiedSingleUnitNotes'] = model['occupiedSingleUnitNotes']
        token['positionEvidence'] = evidence
        token['acousticReviewReasons'] = original
        token['reviewReasons'] = remaining
        token['needsReview'] = bool(remaining)
        rows.append(dict(index=i, text=token['text'], **evidence, remainingReasons=remaining))
    return dict(examined=sum(t['needsReview'] or bool(t.get('positionEvidence')) for t in tokens),
                corroborated=len(rows), anchors=len(anchors), rows=rows)
