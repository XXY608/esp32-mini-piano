"""Ordered syllable partitions on the player's quarter-beat note grid."""
import numpy as np


def connected_phrases(lines):
    """LRC display lines are not musical boundaries without an actual breath.

    Match adjacent lines together when their syllables touch, so the final
    word of one line cannot consume the next line's pickup note. Keep each
    token's original display line and the existing 200 ms breath boundary.
    """
    phrases = []
    for line in lines:
        if phrases and line[0]['start'] - phrases[-1][-1]['end'] <= .20:
            phrases[-1].extend(line)
        else:
            phrases.append(list(line))
    return phrases


def pitch_errors(pitches, reference):
    return np.abs((np.asarray(pitches) - reference + 6) % 12 - 6)


def partition_phrase(tokens, units, times, f0):
    """A syllable may span notes or share a sustained note with its neighbours.

    Phrase selection excludes instrumental notes. Splits carry a cost, so a
    sustained note is split only when syllable timing warrants it. Real gaps
    retain their time; no extra pitches or durations are invented.
    """
    n, m = len(tokens), len(units)
    if m < n:
        raise ValueError("歌词音节超过曲谱最小可播放时值容量，未导入不完整结果")
    starts = np.array([u['audioStart'] for u in units])
    ends = np.array([u['audioEnd'] for u in units])
    pitches = np.array([u['pitch'] for u in units])
    internal = np.array([u['startUnit'] > 0 for u in units])
    dp = np.full((n + 1, m + 1), np.inf)
    back = np.full((n + 1, m + 1), -1, dtype=int)
    dp[0, 0] = 0
    for i, token in enumerate(tokens):
        lower = token.get('vowelStart', token['start'])
        upper = token['end']
        within = (times >= lower) & (times < upper)
        vt = times[within]
        values = f0[within]
        voiced = values > 0
        vp = np.full(len(values), np.nan)
        vp[voiced] = 69 + 12 * np.log2(values[voiced] / 440)
        for j in range(m):
            if not np.isfinite(dp[i, j]) or abs(starts[j] - lower) > 1.0:
                continue
            for k in range(j + 1, m - (n - i - 1) + 1):
                if ends[k - 1] < lower - .2:
                    continue
                if i < n - 1 and ends[k - 1] > upper + 1.0:
                    break
                onset = min(abs(starts[j] - token['start']) / .20, 8)
                ending = min(abs(ends[k - 1] - upper) / .30, 8)
                acoustic = .5
                if np.any(voiced):
                    mid = (starts[j:k] + ends[j:k]) / 2
                    phase = (mid - starts[j]) / max(ends[k - 1] - starts[j], .01)
                    at = lower + phase * (upper - lower)
                    # Keep unvoiced gaps; neither interpolate a sung glide over
                    # silence nor extend the first/last pitch into missing data.
                    actual = np.interp(at, vt, vp, left=np.nan, right=np.nan)
                    errors = np.where(np.isfinite(actual),
                        np.minimum(pitch_errors(actual, pitches[j:k]), 4) / 2, .5)
                    # Score each source note, so a wrong pickup is not hidden by
                    # the many frames of a long, otherwise correct melisma.
                    note_ids = [u['note'] for u in units[j:k]]
                    acoustic = sum(float(np.mean(errors[np.array(note_ids) == ni]))
                                   for ni in dict.fromkeys(note_ids))
                split = 3.0 * (int(internal[j]) + int(k < m and internal[k]))
                cost = .8 * onset + .4 * ending + acoustic + split
                if dp[i, j] + cost < dp[i + 1, k]:
                    dp[i + 1, k] = dp[i, j] + cost
                    back[i + 1, k] = j
    k = m
    if not np.isfinite(dp[n, k]):
        raise ValueError("当前歌词与旋律无法得到完整顺序对应，未导入不完整结果")
    result = []
    for i in range(n, 0, -1):
        j = back[i, k]
        result.append(units[j:k])
        k = j
    return result[::-1]
