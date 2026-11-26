"""Experimental audio/CCMZ alignment. Outputs candidates, not confirmed bindings."""
from pathlib import Path
import json
import sys
import numpy as np
import librosa
import parselmouth
import textgrid
from scipy.ndimage import gaussian_filter1d
from lyric_tokens import restore_tokens, syllables
from word_alignment import connected_phrases, partition_phrase, pitch_errors
from pitch_evidence import corroborate, request_melisma_review
from neural_pitch import cross_check
from reference_score import corroborate_reference
from position_evidence import corroborate_positions
from score_melody import arpeggio_accompaniment, is_arpeggio_accompaniment

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent
STEP = .04
midi_path = ROOT / "midi.json"
midi = json.loads(midi_path.read_text(encoding="utf-8-sig"))
notation = arpeggio_accompaniment(json.loads((ROOT / 'score.json').read_text(encoding='utf-8-sig')))
tpb = next(iter(midi["measures"].values()))["duration"] / midi["beats"]
sec_tick = midi["tempos"][0]["tempo"] / 1e6 / tpb
events = [e for e in midi["events"] if e.get("duration", 0) > 0
          and e.get("event", [0])[0] & 240 == 144 and e["event"][2] > 0]
onsets = {}
filtered_accompaniment = []
for e in events:
    if e.get("track") == midi["rightHandTrack"]:
        if is_arpeggio_accompaniment(e, notation):
            filtered_accompaniment.append(dict(sourceId=e.get('id'), part=e.get('part', 0), tick=e['tick'], pitch=e['event'][1]))
            continue
        onsets.setdefault(e["tick"], []).append(e)
melody = [max(group, key=lambda e: e["event"][1]) for tick, group in sorted(onsets.items())]
score_duration = max((e["tick"] + e["duration"]) * sec_tick for e in events)

cache = ROOT / "audio-features.npz"
if not cache.exists():
    print("Computing reference chroma and isolated vocal pitch", flush=True)
    audio, sr = librosa.load(ROOT / "audio/reference.wav", sr=22050)
    chroma = librosa.feature.chroma_stft(y=audio, sr=sr, hop_length=882, n_fft=4096)
    sound = parselmouth.Sound(str(ROOT / "separated/htdemucs/reference/vocals.wav"))
    pitch = sound.to_pitch_ac(time_step=STEP, pitch_floor=70, pitch_ceiling=700)
    f0 = pitch.selected_array["frequency"]
    ftime = pitch.xs()
    np.savez(cache, chroma=chroma, f0=f0, ftime=ftime)
with np.load(cache) as data:
    chroma, f0, ftime = data["chroma"], data["f0"], data["ftime"]

# Match the entire harmonic structure first; this also retains instrumental gaps.
score = np.zeros((12, int(np.ceil(score_duration / STEP)) + 1), dtype=np.float32)
for e in events:
    lower = int(round(e["tick"] * sec_tick / STEP))
    upper = max(lower + 1, int(round((e["tick"] + e["duration"]) * sec_tick / STEP)))
    score[e["event"][1] % 12, lower:upper] += (e["event"][2] / 127) ** .5
score = gaussian_filter1d(score, .6, axis=1)
score /= np.maximum(np.linalg.norm(score, axis=0, keepdims=True), 1e-7)
chroma /= np.maximum(np.linalg.norm(chroma, axis=0, keepdims=True), 1e-7)
if score.shape[1] * chroma.shape[1] > 80000000:
    raise ValueError("曲谱或录音过长，超出本机逐字对齐的内存范围；请先按乐章分段")
cost = (1 - score.T @ chroma).astype(np.float32)
# Vocals disambiguate melody changes that share the same accompaniment chord.
frame_pitch = np.full(score.shape[1], np.nan, dtype=np.float32)
for e in melody:
    lower = int(round(e["tick"] * sec_tick / STEP))
    upper = max(lower + 1, int(round((e["tick"] + e["duration"]) * sec_tick / STEP)))
    frame_pitch[lower:upper] = e["event"][1]
audio_f0 = np.interp(np.arange(chroma.shape[1]) * STEP, ftime, f0)
audio_pitch = librosa.hz_to_midi(np.maximum(audio_f0, 1))
pitch_cost = np.abs((frame_pitch[:, None] - audio_pitch[None, :] + 6) % 12 - 6)
pitch_cost = np.nan_to_num(pitch_cost, nan=1.) / 3
pitch_cost[:, audio_f0 < 70] = 0
cost += 2 * pitch_cost.astype(np.float32)
del pitch_cost
print("Aligning", cost.shape, flush=True)
D, path = librosa.sequence.dtw(C=cost, step_sizes_sigma=np.array([[1, 1], [1, 2], [2, 1]]),
                             weights_add=np.array([0, .15, .15]))
path = path[::-1]
mapped = np.interp(np.arange(score.shape[1]), path[:, 0], path[:, 1]) * STEP
np.savez(ROOT / "score-audio-path.npz", path=path, score_to_audio=mapped)

def audio_time(tick):
    return float(np.interp(tick * sec_tick / STEP, np.arange(len(mapped)), mapped))

notes = []
for index, e in enumerate(melody):
    duration = max(.25 * tpb, e["duration"])
    if index + 1 < len(melody):
        gap = melody[index + 1]["tick"] - e["tick"] - duration
        if gap <= .5 * tpb:
            duration += gap
    notes.append(dict(sourceId=e["id"], part=e.get("part", 0), tick=e["tick"], pitch=e["event"][1],
                      scoreStart=e["tick"] * sec_tick, scoreEnd=(e["tick"] + duration) * sec_tick,
                      audioStart=audio_time(e["tick"]), audioEnd=audio_time(e["tick"] + duration)))

tokens = []
refined_file = ROOT / 'refined-tokens.json'
refined = json.loads(refined_file.read_text(encoding='utf-8')) if refined_file.exists() else {}
for segment in json.loads((ROOT / "segments.json").read_text(encoding="utf-8")):
    if segment['name'] in refined:
        tokens.extend(dict(t, line=segment['name']) for t in refined[segment['name']])
        continue
    file = ROOT / "aligned" / (segment["name"] + ".TextGrid")
    if not file.exists():
        raise ValueError("缺少歌词片段：" + segment["name"])
    grid = textgrid.TextGrid.fromFile(str(file))
    intervals = [item for item in grid[0] if item.mark.strip()]
    expected = segment.get("syllables", syllables(segment["text"]))
    for text, item in restore_tokens(expected, intervals):
        phones = [p for p in grid[2] if p.mark and p.maxTime > item.minTime and p.minTime < item.maxTime]
        # The last phone is the sung vowel/final. Consonant lead-ins must not
        # borrow the pitch of the previous syllable in diagnostics or scoring.
        vowel = phones[-1].minTime if phones else item.minTime
        tokens.append(dict(text=text, start=item.minTime + segment["offset"],
                           vowelStart=vowel + segment["offset"],
                           end=item.maxTime + segment["offset"], line=segment["name"]))

units = []
for ni, note in enumerate(notes):
    count = max(1, round((note['scoreEnd'] - note['scoreStart']) / sec_tick / tpb * 4))
    for unit in range(count):
        lower = note['tick'] + unit * tpb / 4
        upper = min(note['tick'] + (unit + 1) * tpb / 4, note['scoreEnd'] / sec_tick)
        units.append(dict(note=ni, pitch=note['pitch'], startUnit=unit, endUnit=unit + 1,
                          audioStart=audio_time(lower), audioEnd=audio_time(upper)))

lines = {}
for token in tokens:
    lines.setdefault(token['line'], []).append(token)
used = set()
phrases = connected_phrases(lines.values())
for pi, phrase in enumerate(phrases):
    phrase_end = phrase[-1]['end']
    if pi + 1 < len(phrases):
        phrase_end = min(phrase_end, phrases[pi + 1][0]['start'])
    candidates = [u for u in units if notes[u['note']]['audioStart'] >= phrase[0]['start'] - .4
                  and notes[u['note']]['audioStart'] < phrase_end - .03
                  and (u['note'], u['startUnit']) not in used]
    # Breaths separate musical subphrases. Otherwise an extra ornament after a
    # breath can mask a missing syllable before it and shift the whole phrase.
    chunks = []
    begin = 0
    for i in range(1, len(phrase)):
        if phrase[i]['start'] - phrase[i - 1]['end'] > .20:
            cut = (phrase[i]['start'] + phrase[i - 1]['end']) / 2
            chunks.append((phrase[begin:i], cut))
            begin = i
    chunks.append((phrase[begin:], float('inf')))
    groups = []
    previous = float('-inf')
    for chunk, cut in chunks:
        selected = [u for u in candidates if previous <= notes[u['note']]['audioStart'] < cut]
        groups.extend(partition_phrase(chunk, selected, ftime, f0))
        previous = cut
    for token, group in zip(phrase, groups):
        spans = []
        for u in group:
            used.add((u['note'], u['startUnit']))
            if spans and spans[-1]['note'] == u['note']:
                spans[-1]['endUnit'] = u['endUnit']
            else:
                spans.append(dict(note=u['note'], startUnit=u['startUnit'], endUnit=u['endUnit']))
        token['noteSpans'] = spans
        token['sourceNotes'] = [s['note'] for s in spans]
        token['mappedStart'] = group[0]['audioStart']
        token['mappedEnd'] = group[-1]['audioEnd']
        voiced = (ftime >= token['vowelStart']) & (ftime < token['end']) & (f0 > 0)
        pitch_samples = librosa.hz_to_midi(f0[voiced])
        related = [notes[j] for j in token['sourceNotes']]
        errors = np.min([pitch_errors(pitch_samples, n['pitch']) for n in related], axis=0) if len(pitch_samples) else np.array([])
        token['medianPitchError'] = float(np.median(errors)) if len(errors) else None
        token['pitchSupportFraction'] = float(np.mean(errors <= .75)) if len(errors) else 0
        reasons = []
        if not len(errors):
            reasons.append('no-voiced-evidence')
        elif token['pitchSupportFraction'] < .65:
            reasons.append('vowel-pitch-mismatch')
        if token['end'] - token['start'] < .08:
            reasons.append('collapsed-acoustic-token')
        if abs(token['mappedStart'] - token['vowelStart']) > .45:
            reasons.append('distant-onset')
        token['reviewReasons'] = reasons
        token['needsReview'] = bool(reasons)
for i, token in enumerate(tokens):
    token['alignmentIndex'] = i
    token['endLine'] = i == len(tokens) - 1 or tokens[i + 1]['line'] != token['line']

request_melisma_review(tokens, notes)
initial_review_count = sum(t['needsReview'] for t in tokens)
review = []
if any(token['needsReview'] for token in tokens):
    independent_cache = ROOT / 'pyin-pitch.npz'
    if not independent_cache.exists():
        print('Cross-checking flagged vowels with independent pYIN pitch', flush=True)
        vocal, sr = librosa.load(ROOT / 'separated/htdemucs/reference/vocals.wav', sr=16000)
        independent_f0, _, probability = librosa.pyin(vocal, sr=sr, fmin=65, fmax=700,
            frame_length=1024, hop_length=160)
        independent_times = librosa.times_like(independent_f0, sr=sr, hop_length=160)
        np.savez(independent_cache, f0=independent_f0, times=independent_times, probability=probability)
    with np.load(independent_cache) as independent:
        review = corroborate(tokens, notes, ftime, f0, independent['times'], independent['f0'])
        print('Checking unresolved vowels with independent neural pitch', flush=True)
        neural = {row['index']: row for row in cross_check(ROOT, tokens, notes, independent['times'], independent['f0'])}
        for row in review:
            if row['index'] in neural:
                row['traditionalStatus'] = row['status']
                row['neuralEvidence'] = neural[row['index']]
                row['status'] = neural[row['index']]['status']
                row['remainingReasons'] = list(tokens[row['index']]['reviewReasons'])
(ROOT / 'pitch-review.json').write_text(json.dumps(review, ensure_ascii=False, indent=2), encoding='utf-8')

reference_review = dict(examined=0, corroborated=0, match=dict(accepted=False, reason='no-sourced-reference'))
if (ROOT / 'reference-score.json').exists():
    reference = json.loads((ROOT / 'reference-score.json').read_text(encoding='utf-8'))
    reference_review = corroborate_reference(tokens, notes, reference, tpb, sec_tick)
    (ROOT / 'reference-review.json').write_text(json.dumps(reference_review, ensure_ascii=False, indent=2), encoding='utf-8')

position_review = dict(examined=0, corroborated=0, anchors=0, rows=[])
if any(token['needsReview'] for token in tokens):
    with np.load(ROOT / 'pyin-pitch.npz') as independent:
        position_review = corroborate_positions(tokens, notes, ftime, f0, independent['times'], independent['f0'],
                                               sec_tick * tpb / 4)
(ROOT / 'position-review.json').write_text(json.dumps(position_review, ensure_ascii=False, indent=2), encoding='utf-8')

lyrics = json.loads((ROOT / 'selected-lyrics.json').read_text(encoding='utf-8'))
result = dict(status='automatic-with-diagnostics', algorithmVersion=14, source='audio/provenance.json',
              lyricsId=lyrics['id'], scoreDuration=score_duration, step=STEP, notes=notes, tokens=tokens,
              melodyExtraction=dict(method='explicit-notated-arpeggio-top-tone', filteredAccompaniment=filtered_accompaniment),
              pitchReview=dict(examined=len(review), corroborated=sum(r['status'].startswith('corroborated-') for r in review),
                  unresolved=sum(not r['status'].startswith('corroborated-') for r in review),
                  changedAssignments=0, report='pitch-review.json'),
              referenceReview=dict(examined=reference_review['examined'], corroborated=reference_review['corroborated'],
                  match=reference_review['match'], report='reference-review.json' if (ROOT / 'reference-score.json').exists() else None),
              positionReview=dict(examined=position_review['examined'], corroborated=position_review['corroborated'],
                                  anchors=position_review['anchors'], report='position-review.json'),
              alignmentReview=dict(examined=initial_review_count, remaining=sum(t['needsReview'] for t in tokens),
                  acousticDiagnosticsRetained=sum(bool(t.get('acousticReviewReasons')) for t in tokens)),
              coverage=dict(expected=len(tokens), aligned=len(tokens), missing=0))
(ROOT / 'candidate-alignment.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(dict(tokens=len(tokens), mapped=sum(bool(t['sourceNotes']) for t in tokens),
                      multiNote=sum(len(t['sourceNotes']) > 1 for t in tokens),
                      needsReview=sum(t['needsReview'] for t in tokens))), flush=True)

