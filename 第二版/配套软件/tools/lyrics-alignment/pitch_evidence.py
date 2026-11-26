"""Corroborate flagged pitches without treating every sung glide as a bad binding.

The original 65% / 0.75-semitone diagnostic remains available. A second
estimator can resolve it only with sustained target evidence in score order,
or a continuous transition to/from an adjacent score note. Conflicting and weak
evidence stays flagged; these checks never change a score pitch or assignment.
"""
import numpy as np
from word_alignment import pitch_errors

PITCH_REVIEW_REASONS = ('vowel-pitch-mismatch', 'melisma-order-unverified')


def request_melisma_review(tokens, notes):
    """A high fraction near *any* target does not prove every note in order."""
    for token in tokens:
        if len({notes[i]['pitch'] % 12 for i in token['sourceNotes']}) > 1:
            if 'melisma-order-unverified' not in token['reviewReasons']:
                token['reviewReasons'].append('melisma-order-unverified')
            token['needsReview'] = True


def longest_run(mask, step):
    edges = np.diff(np.r_[False, mask, False].astype(int))
    starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
    return float(max(ends - starts, default=0) * step)


def ordered_targets(p, q, agree, pitches, step):
    """Require sustained visits in score order, including a return to a pitch.

    Consecutive copies of the same pitch class may be tied or octave-adjusted
    by the score. Different classes must each have their own unambiguous run;
    an overlapping tolerance around a semitone midpoint cannot prove both.
    """
    sequence = []
    for pitch in pitches:
        if not sequence or sequence[-1] != pitch:
            sequence.append(pitch)
    classes = sorted(set(sequence))
    errors_p = np.array([pitch_errors(p, pitch) for pitch in classes])
    errors_q = np.array([pitch_errors(q, pitch) for pitch in classes])
    cursor = 0
    required = max(1, int(np.ceil((.08 - 1e-6) / step)))
    for pitch in sequence:
        index = classes.index(pitch)
        others = [j for j in range(len(classes)) if j != index]
        mask = agree & (errors_p[index] <= .75) & (errors_q[index] <= .75)
        if others:
            mask &= (errors_p[index] < np.min(errors_p[others], axis=0) - 1e-6)
            mask &= (errors_q[index] < np.min(errors_q[others], axis=0) - 1e-6)
        mask[:cursor] = False
        edges = np.diff(np.r_[False, mask, False].astype(int))
        starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
        matches = starts[ends - starts >= required]
        if not len(matches):
            return sequence, False
        cursor = int(matches[0]) + required
    return sequence, True


def inspect_pitch(token, notes, times, primary, secondary, previous=None, following=None):
    step = float(np.median(np.diff(times)))
    lower, upper = token['vowelStart'], token['end']
    select = (times >= lower) & (times < upper)
    first, second = primary[select], secondary[select]
    valid = np.isfinite(first) & np.isfinite(second) & (first > 0) & (second > 0)
    p = np.full(len(first), np.nan)
    q = np.full(len(second), np.nan)
    p[valid] = 69 + 12 * np.log2(first[valid] / 440)
    q[valid] = 69 + 12 * np.log2(second[valid] / 440)
    expected = sorted(set(notes[i]['pitch'] % 12 for i in token['sourceNotes']))
    errors_p = np.min([pitch_errors(p, pitch) for pitch in expected], axis=0)
    errors_q = np.min([pitch_errors(q, pitch) for pitch in expected], axis=0)
    agree = valid & (pitch_errors(p, q) <= .5)
    target = agree & (errors_p <= .75) & (errors_q <= .75)
    runs = {str(pitch): longest_run(agree & (pitch_errors(p, pitch) <= .75)
        & (pitch_errors(q, pitch) <= .75), step) for pitch in expected}
    sequence, ordered = ordered_targets(p, q, agree,
        [notes[i]['pitch'] % 12 for i in token['sourceNotes']], step) if len(expected) > 1 else (expected, True)
    support = float(np.mean(errors_q[valid] <= .75)) if np.any(valid) else 0.
    coverage = float(np.mean(valid)) if len(valid) else 0.
    run = longest_run(target, step)
    status = 'insufficient-voicing'
    if np.sum(valid) * step >= .08 - 1e-6:
        status = 'pitch-evidence-conflict'
        if support >= .65 and min(runs.values()) >= .08 - 1e-6 and coverage >= .5:
            status = 'corroborated-target' if ordered else 'out-of-order-targets'
        elif len(expected) == 1 and run >= .08 - 1e-6 and np.mean(target) >= .25:
            # Require the *other* pitches to trace a continuous approach or
            # release towards a neighbouring score pitch, not an arbitrary
            # short visit to the target inside a mismatched vowel.
            reference = notes[token['sourceNotes'][0]]['pitch']
            # Keep melodic intervals across a tritone or octave. Reducing
            # every interval to the nearest pitch class turns a descending
            # fifth into an ascending fourth and rejects a real release.
            target_pitch = float(np.nanmedian(q[target]))
            octave_shift = 12 * round((target_pitch - reference) / 12)
            centered = q - (reference + octave_shift)
            matching = np.flatnonzero(target)
            before = agree & (np.arange(len(q)) < matching[0])
            after = agree & (np.arange(len(q)) > matching[-1])
            middle = agree & (np.arange(len(q)) >= matching[0]) & (np.arange(len(q)) <= matching[-1])
            continuous = True
            for mask, neighbour in ((before, previous), (after, following)):
                off = centered[mask & (abs(centered) > .75)]
                if len(off) * step < .04:
                    continue
                if neighbour is None:
                    continuous = False
                    break
                edge = neighbour - reference
                if abs(edge) < .75:
                    continuous = False
                    break
                # A slide must stay between the target and the neighbour;
                # permit the same 0.75-semitone tolerance at both ends.
                if np.mean((off >= min(0., edge) - .75) & (off <= max(0., edge) + .75)) < .9:
                    continuous = False
                    break
            if (continuous and np.mean(agree[valid]) >= .8
                    and np.mean(abs(centered[middle]) <= .75) >= .8):
                status = 'corroborated-transition'
            if status == 'pitch-evidence-conflict' and np.mean(agree[valid]) >= .8:
                # An onset scoop can begin off the notated neighbour (notably
                # at a phrase entrance). It must approach one stable target,
                # with no return to the wrong pitch or middle detour. Keep
                # both trackers, the sustained-target duration and the pitch
                # tolerance; do not accept a brief crossing of the target.
                attack = centered[agree & (np.arange(len(q)) < matching[0])]
                tail = (np.arange(len(q)) >= max(0, len(q) - round(.08 / step))) & valid
                attack_ok = False
                if len(attack) and np.sum(tail) * step >= .06 - 1e-6:
                    direction = 1 if np.median(attack[:min(3, len(attack))]) < 0 else -1
                    progress = direction * attack
                    attack_ok = (np.ptp(attack) <= 12
                        and np.mean(progress <= .75) >= .9
                        and np.mean(np.maximum.accumulate(progress) - progress <= .75) >= .9)
                if (attack_ok and np.mean(abs(centered[tail]) <= .75) >= .8
                        and np.mean(abs(centered[middle]) <= .75) >= .8):
                    status = 'corroborated-onset-scoop'
        if status == 'pitch-evidence-conflict':
            if np.mean(agree[valid]) < .8:
                status = 'trackers-disagree'
            elif run < .08 - 1e-6:
                status = 'no-sustained-target'
            else:
                status = 'unresolved-pitch-gesture'
    evidence = dict(status=status, independentSupportFraction=support,
        pairedVoicedFraction=coverage, sustainedTargetSeconds=run,
        targetRunsSeconds=runs,
        independentMedianPitch=float(np.nanmedian(q)) if np.any(valid) else None,
        changesAssignment=False)
    if len(expected) > 1:
        evidence.update(targetPitchOrder=sequence, targetOrderSatisfied=ordered)
    return evidence


def corroborate(tokens, notes, primary_times, primary_f0, times, secondary_f0):
    # Nearest samples preserve unvoiced gaps. Interpolation through a zero or a
    # subharmonic jump would invent pitches that neither estimator observed.
    indices = np.searchsorted(primary_times, times)
    indices = np.clip(indices, 1, len(primary_times) - 1)
    indices -= abs(times - primary_times[indices - 1]) < abs(times - primary_times[indices])
    primary = primary_f0[indices]
    rows = []
    for i, token in enumerate(tokens):
        if not any(reason in token['reviewReasons'] for reason in PITCH_REVIEW_REASONS):
            continue
        previous = notes[tokens[i - 1]['sourceNotes'][-1]]['pitch'] if i and tokens[i - 1]['line'] == token['line'] else None
        following = notes[tokens[i + 1]['sourceNotes'][0]]['pitch'] if i + 1 < len(tokens) and tokens[i + 1]['line'] == token['line'] else None
        evidence = inspect_pitch(token, notes, times, primary, secondary_f0, previous, following)
        token['pitchEvidence'] = evidence
        original_reasons = list(token['reviewReasons'])
        if evidence['status'].startswith('corroborated-'):
            token['reviewReasons'] = [r for r in token['reviewReasons'] if r not in PITCH_REVIEW_REASONS]
        token['needsReview'] = bool(token['reviewReasons'])
        rows.append(dict(index=i, text=token['text'], line=token['line'],
            sourceNotes=token['sourceNotes'], originalPitchSupport=token['pitchSupportFraction'],
            start=token['start'], vowelStart=token['vowelStart'], end=token['end'],
            noteSpans=token['noteSpans'], originalReasons=original_reasons,
            **evidence, remainingReasons=list(token['reviewReasons'])))
    return rows
