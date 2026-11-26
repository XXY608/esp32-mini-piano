"""Check lyric positions against a sourced, explicitly labelled reference score.

This supplies score evidence when a short/weak sung syllable cannot supply
reliable F0. It never changes source notes or calls a pitch discrepancy correct.
"""
from collections import Counter
from fractions import Fraction
from functools import lru_cache
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import re
from urllib.parse import urlparse

from lyric_tokens import normalized


def unique_pitch_run(reference, source):
    """Return a unique ordered pitch match; repeated ambiguous notes stay out."""
    @lru_cache(None)
    def solve(i, j):
        if i == len(reference) or j == len(source):
            return 0, ((),)
        options = [solve(i + 1, j), solve(i, j + 1)]
        if reference[i] == source[j]:
            length, paths = solve(i + 1, j + 1)
            options.append((length + 1, tuple(((i, j),) + path for path in paths)))
        best = max(option[0] for option in options)
        paths = set()
        for length, candidates in options:
            if length == best:
                paths.update(candidates)
        return best, tuple(sorted(paths)[:2])
    length, paths = solve(0, 0)
    if len(paths) == 1 and length and length >= .75 * min(len(reference), len(source)):
        return list(paths[0])
    return []


class ScorePage(HTMLParser):
    def __init__(self):
        super().__init__()
        self.depth = 0
        self.chunks = []

    def handle_starttag(self, tag, attrs):
        if tag == 'div':
            if self.depth:
                self.depth += 1
            elif dict(attrs).get('id') == 'jianpuOut':
                self.depth = 1

    def handle_endtag(self, tag):
        if tag == 'div' and self.depth:
            self.depth -= 1

    def handle_data(self, data):
        if self.depth:
            self.chunks.append(data)


def parse_reference(page):
    parser = ScorePage()
    parser.feed(page)
    score = ''.join(parser.chunks)
    key = re.search(r'/key\(([A-Ga-g])([#b]?)([0-9]?)\)', score)
    if not key or len(re.findall(r'/key\(', score)) != 1:
        raise ValueError('参考带词谱的调号不受支持')
    pc = dict(C=0, D=2, E=4, F=5, G=7, A=9, B=11)[key[1].upper()]
    octave = int(key[3]) if key[3] else (3 if key[1].upper() in 'GAB' else 4)
    tonic = 12 * (octave + 1) + pc + (1 if key[2] == '#' else -1 if key[2] == 'b' else 0)
    score = score[:key.start()] + score[key.end():]
    notes, lyrics = [], []
    beat = Fraction(0)
    note_pattern = re.compile(r'([#bn]{0,2})([0-7-])([\x27,]*)(-+|=*_?)(\.{0,2})')
    for line in score.splitlines():
        line = line.strip()
        if line.startswith('L:'):
            if '"' in line:
                raise ValueError('参考带词谱包含未支持的合并歌词')
            lyrics.extend(re.findall(r"[\u3400-\u9fff\uf900-\ufaff]|[A-Za-z]+(?:['’][A-Za-z]+)*|[_*]", line[2:]))
            continue
        if line.startswith('ｂｐｍ') or not line:
            continue
        pos = 0
        while pos < len(line):
            if line[pos].isspace() or line[pos] in '|]':
                pos += 1
                continue
            match = note_pattern.match(line, pos)
            if not match:
                raise ValueError('参考带词谱包含未支持的记谱内容：' + line[pos:pos + 10])
            accidental, degree, octaves, length, dots = match.groups()
            power = length.count('=') * 2 + length.count('_')
            duration = Fraction(1 + length.count('-'), 2 ** power) * (2 - Fraction(1, 2 ** len(dots)))
            if degree in '1234567':
                pitch = tonic + [0, 0, 2, 4, 5, 7, 9, 11][int(degree)]
                pitch += 12 * (octaves.count("'") - octaves.count(','))
                pitch += accidental.count('#') - accidental.count('b')
                notes.append(dict(index=len(notes), beat=float(beat), duration=float(duration), pitch=pitch))
            beat += duration
            pos = match.end()
    if len(lyrics) > len(notes):
        raise ValueError('参考带词谱的歌词超过音符数量')
    for i, note in enumerate(notes):
        note['text'] = lyrics[i] if i < len(lyrics) and lyrics[i] not in ('_', '*') else ''
    return dict(notes=notes, beats=float(beat))


def prepare_reference(root, tools, url):
    """An unavailable optional score keeps the existing audio diagnostics."""
    import requests
    if urlparse(url).hostname != 'jianpu.space':
        raise ValueError('不支持的带词谱来源')
    cache = tools / 'references' / (hashlib.sha256(url.encode()).hexdigest() + '.html')
    try:
        response = requests.get(url, timeout=20)
        response.raise_for_status()
        raw = response.content
        result = parse_reference(raw.decode('utf-8'))
        cache.parent.mkdir(exist_ok=True)
        cache.write_bytes(raw)
        origin = 'download'
    except (requests.RequestException, ValueError):
        if not cache.exists():
            raise
        raw = cache.read_bytes()
        result = parse_reference(raw.decode('utf-8'))
        origin = 'cached-source'
    result.update(url=url, sha256=hashlib.sha256(raw).hexdigest(), retrieval=origin)
    (root / 'reference-score.html').write_bytes(raw)
    (root / 'reference-score.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')


def match_score(notes, reference, ticks_per_beat):
    """Find a unique global beat offset/key from the whole melody, not its title."""
    source_beats = [n['tick'] / ticks_per_beat for n in notes]
    lookup = {}
    for i, beat in enumerate(source_beats):
        lookup.setdefault(round(beat * 4), []).append(i)
    ref_notes = reference['notes']
    candidates = Counter()
    for source in notes:
        beat = source['tick'] / ticks_per_beat
        for ref in ref_notes:
            offset = round((beat - ref['beat']) * 4) / 4
            if abs(beat - ref['beat'] - offset) <= 1 / 12 + .001:
                candidates[offset, (source['pitch'] - ref['pitch']) % 12] += 1
    ranked = candidates.most_common()
    if not ranked:
        return dict(accepted=False, reason='no-matching-onsets')
    (offset, transpose), _ = ranked[0]
    mapped = {}
    for ref in ref_notes:
        beat = ref['beat'] + offset
        nearby = lookup.get(round(beat * 4), [])
        close = [i for i in nearby if abs(source_beats[i] - beat) <= 1 / 12 + .001]
        if len(close) == 1:
            mapped[ref['index']] = close[0]
    inferred = []
    for ri, ref in enumerate(ref_notes[1:-1], 1):
        if ri in mapped or ri - 1 not in mapped or ri + 1 not in mapped:
            continue
        # A transcription can delay one onset slightly or insert a grace note.
        # Two already matched neighbours can bracket a unique pitched note;
        # this is ordered note evidence, not a wider time tolerance.
        candidates_between = [ni for ni in range(mapped[ri - 1] + 1, mapped[ri + 1])
            if (notes[ni]['pitch'] - ref['pitch']) % 12 == transpose]
        if len(candidates_between) == 1:
            mapped[ri] = candidates_between[0]
            inferred.append(ri)
    bracketed_runs = []
    anchors = sorted(mapped)
    for left, right in zip(anchors, anchors[1:]):
        if right == left + 1:
            continue
        reference_run = list(range(left + 1, right))
        source_run = list(range(mapped[left] + 1, mapped[right]))
        pairs = unique_pitch_run([(ref_notes[ri]['pitch'] + transpose) % 12 for ri in reference_run],
            [notes[si]['pitch'] % 12 for si in source_run])
        if pairs:
            confirmed = {reference_run[ri]: source_run[si] for ri, si in pairs}
            mapped.update(confirmed)
            bracketed_runs.append(list(confirmed))
    matches = sum((notes[ni]['pitch'] - ref_notes[ri]['pitch']) % 12 == transpose for ri, ni in mapped.items())
    runner = ranked[1][1] if len(ranked) > 1 else 0
    # Require almost the entire independently labelled melody and a clearly
    # better placement than other repeated sections of the same song.
    accepted = len(ref_notes) >= 16 and matches / len(ref_notes) >= .9 and matches >= runner * 1.25
    return dict(accepted=accepted, matchedPitches=matches, referenceNotes=len(ref_notes),
        registeredNotes=len(mapped), beatOffset=offset, pitchClassTranspose=transpose,
        runnerUpVotes=runner, bracketedNotes=inferred, bracketedRuns=bracketed_runs, mapping=mapped,
        reason='whole-score-match' if accepted else 'reference-arrangement-not-confirmed')


def corroborate_reference(tokens, notes, reference, ticks_per_beat, seconds_per_tick):
    match = match_score(notes, reference, ticks_per_beat)
    rows = []
    if not match['accepted']:
        return dict(match=match, examined=0, corroborated=0, rows=rows)
    refs = reference['notes']
    mapping = match.pop('mapping')
    by_source = {si: ref for ref in refs if (si := mapping.get(ref['index'])) is not None}
    # Validate surrounding lyric labels too. A repeated word by itself cannot
    # confirm a position in the wrong verse or a shifted run of syllables.
    owners = {span['note']: token for token in tokens for span in token['noteSpans'] if span['startUnit'] == 0}
    pitch_shifts = Counter(notes[si]['pitch'] - refs[ri]['pitch'] for ri, si in mapping.items())
    octave_shift = pitch_shifts.most_common(1)[0][0]
    for i, token in enumerate(tokens):
        if not token['needsReview']:
            continue
        if len(token['noteSpans']) != 1:
            continue
        span = token['noteSpans'][0]
        ni = span['note']
        note = notes[ni]
        full_units = max(1, round((note['scoreEnd'] - note['scoreStart']) / seconds_per_tick / ticks_per_beat * 4))
        if span['startUnit'] != 0 or span['endUnit'] != full_units:
            continue
        ref = by_source.get(ni)
        if not ref or normalized(ref['text']) != normalized(token['text']):
            continue
        neighbours = []
        for other in refs[max(0, ref['index'] - 2):ref['index'] + 3]:
            if other['index'] == ref['index'] or not other['text']:
                continue
            owner = owners.get(mapping.get(other['index']))
            neighbours.append(bool(owner and normalized(owner['text']) == normalized(other['text'])))
        if len(neighbours) < 2 or sum(neighbours) / len(neighbours) < .75:
            continue
        next_word = next((r for r in refs[ref['index'] + 1:] if r['text']), None)
        end_beat = note['scoreEnd'] / seconds_per_tick / ticks_per_beat
        next_source = mapping.get(next_word['index']) if next_word else None
        boundary = (notes[next_source]['tick'] / ticks_per_beat if next_source is not None
            else next_word['beat'] + match['beatOffset'] if next_word else float('inf'))
        if end_beat > boundary + .125:
            continue
        acoustic = list(token['reviewReasons'])
        token['acousticReviewReasons'] = acoustic
        token['reviewReasons'] = [r for r in acoustic if r not in ('vowel-pitch-mismatch', 'no-voiced-evidence', 'distant-onset')]
        token['needsReview'] = bool(token['reviewReasons'])
        evidence = dict(status='corroborated-reference-position', sourceUrl=reference['url'],
            sourceSha256=reference['sha256'], referenceNote=ref['index'], referenceBeat=ref['beat'],
            referenceText=ref['text'], referencePitch=ref['pitch'], sourcePitch=note['pitch'],
            pitchDifference=note['pitch'] - ref['pitch'] - octave_shift,
            sourceNote=ni, neighbouringLabelsMatched=sum(neighbours), neighbouringLabelsChecked=len(neighbours),
            acousticReasonsRetained=acoustic, changesAssignment=False)
        token['referenceScoreEvidence'] = evidence
        rows.append(dict(index=i, text=token['text'], line=token['line'], **evidence,
            remainingReasons=list(token['reviewReasons'])))
    return dict(match=match, examined=sum(bool(t.get('acousticReviewReasons') or t['needsReview']) for t in tokens),
        corroborated=len(rows), rows=rows)
