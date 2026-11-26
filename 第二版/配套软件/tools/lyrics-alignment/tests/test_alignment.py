import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import tempfile
import hashlib
from types import SimpleNamespace

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lyric_tokens import restore_tokens, syllables
from word_alignment import connected_phrases, partition_phrase
from refine_overlaps import overlapping_groups, expand_groups
from pitch_evidence import inspect_pitch, corroborate, request_melisma_review
from reference_score import parse_reference, prepare_reference, corroborate_reference, unique_pitch_run
from source_selection import select_lyrics
from prepare_segments import prepare
from audio_source import cached_recording
from position_evidence import corroborate_positions, timing_model, unique_position, with_anchor_capacity
from score_melody import arpeggio_accompaniment, is_arpeggio_accompaniment


def note_units(notes):
    result = []
    for ni, (start, end, pitch) in enumerate(notes):
        count = round((end - start) * 4)
        result.extend(dict(note=ni, startUnit=u, endUnit=u + 1, pitch=pitch,
                           audioStart=start + u / 4, audioEnd=start + (u + 1) / 4) for u in range(count))
    return result


def reference_fixture():
    text = '甲乙丙丁戊己庚辛壬癸春夏秋冬风雨山河星月'
    pitches = [60, 62, 64, 65, 67, 69, 71, 72, 74, 76, 77, 79, 81, 83, 84, 86, 88, 89, 91, 93]
    reference = dict(url='https://jianpu.space/zh-tw/songList/test', sha256='a' * 64,
        notes=[dict(index=i, beat=i / 2, pitch=p, duration=.5, text=text[i]) for i, p in enumerate(pitches)])
    notes = [dict(tick=(32 + i / 2) * 480, pitch=p + 24, scoreStart=32 + i / 2,
        scoreEnd=32.5 + i / 2) for i, p in enumerate(pitches)]
    tokens = [dict(text=text[i], line='line-00', sourceNotes=[i], start=32 + i / 2,
        vowelStart=32.05 + i / 2, end=32.5 + i / 2,
        noteSpans=[dict(note=i, startUnit=0, endUnit=2)],
        reviewReasons=['vowel-pitch-mismatch', 'distant-onset'] if i == 5 else [], needsReview=i == 5)
        for i in range(len(notes))]
    return tokens, notes, reference


class AlignmentTests(unittest.TestCase):
    def capacity_fixture(self):
        notes = [dict(scoreStart=start, scoreEnd=end) for start, end in
                 [(.5, .75), (1., 1.25), (2., 2.18), (2.18, 2.43), (3., 3.5), (4., 4.25)]]
        anchors = [dict(noteSpans=[dict(note=ni, startUnit=0, endUnit=1)]) for ni in [0, 1, 3, 5]]
        model = dict(indices=[0, 1, 2, 3], x=np.array([.5, 1., 2.18, 4.]),
                     parameters=(1., 0., 0.), phonetic=False, validation=.06)
        token = dict(start=2., vowelStart=2.02, end=2.16)
        return token, notes, anchors, model

    def test_single_unit_owned_by_a_frozen_anchor_cannot_hold_a_second_word(self):
        token, notes, anchors, model = self.capacity_fixture()
        self.assertIsNone(unique_position(token, notes, model))
        constrained = with_anchor_capacity(model, anchors, notes, .25)
        result = unique_position(token, notes, constrained)
        self.assertEqual(result['best'][1], 2)
        self.assertEqual(result['alternative'][1], 4)
        self.assertEqual(constrained['occupiedSingleUnitNotes'][3], 2)
        self.assertNotIn('occupiedSingleUnitNotes', model)

    def test_capacity_preserves_shareable_notes_and_unfrozen_owners(self):
        for problem in ('two-units', 'unfrozen-owner', 'partial-range'):
            token, notes, anchors, model = self.capacity_fixture()
            if problem == 'two-units':
                notes[3]['scoreEnd'] = 2.68
            elif problem == 'unfrozen-owner':
                model['indices'] = [0, 1, 3]
            else:
                anchors[2]['noteSpans'][0]['startUnit'] = 1
            constrained = with_anchor_capacity(model, anchors, notes, .25)
            self.assertNotIn(3, constrained['occupiedSingleUnitNotes'])
            self.assertIsNone(unique_position(token, notes, constrained), problem)

    def test_capacity_cannot_hide_a_free_ambiguous_candidate_or_validate_a_bad_model(self):
        token, notes, anchors, model = self.capacity_fixture()
        notes.append(dict(scoreStart=2.1, scoreEnd=2.35))
        self.assertIsNone(unique_position(token, notes, with_anchor_capacity(model, anchors, notes, .25)))
        self.assertIsNone(with_anchor_capacity(None, anchors, notes, .25))

    def partition_gap_fixture(self, final_pitch=64):
        times = np.arange(0, .81, .01)
        tokens = [dict(start=0, end=.3), dict(start=.3, end=.8)]
        units = [dict(note=i, pitch=pitch, startUnit=0, endUnit=1, audioStart=start, audioEnd=end)
                 for i, pitch, start, end in [(0, 60, 0, .3), (1, 60, .3, .5), (2, final_pitch, .5, .8)]]
        return tokens, units, times

    def test_partition_cannot_extend_late_pitch_back_over_an_unvoiced_pickup(self):
        tokens, units, times = self.partition_gap_fixture()
        f0 = np.where(times < .3, 440 * 2 ** ((60 - 69) / 12),
                      np.where(times >= .6, 440 * 2 ** ((64 - 69) / 12), 0))
        groups = partition_phrase(tokens, units, times, f0)
        self.assertEqual([[u['note'] for u in group] for group in groups], [[0], [1, 2]])

    def test_partition_cannot_invent_a_glide_across_an_internal_unvoiced_gap(self):
        tokens, units, times = self.partition_gap_fixture(final_pitch=69)
        f0 = np.where(times < .33, 440 * 2 ** ((60 - 69) / 12), np.where(times >= .55, 440, 0))
        groups = partition_phrase(tokens, units, times, f0)
        self.assertEqual([[u['note'] for u in group] for group in groups], [[0], [1, 2]])

    def test_partition_keeps_continuous_and_fully_unvoiced_timing(self):
        tokens, units, times = self.partition_gap_fixture()
        continuous = 440 * 2 ** ((np.where(times < .5, 60, 64) - 69) / 12)
        for f0 in (continuous, np.zeros(len(times)), np.full(len(times), np.nan)):
            groups = partition_phrase(tokens, units, times, f0)
            self.assertEqual([[u['note'] for u in group] for group in groups], [[0], [1, 2]])

    def test_explicit_arpeggio_filters_lower_chord_elements_even_with_early_onsets(self):
        score = dict(parts=[dict(measures=[dict(notes=[dict(arts=[dict(type='arpeggiate')],
            elems=[dict(id='lo', step=1, octave=5, alter=1), dict(id='hi', step=3, octave=5)])])])])
        notation = arpeggio_accompaniment(score)
        self.assertTrue(is_arpeggio_accompaniment(dict(id='lo', tick=440), notation))
        self.assertFalse(is_arpeggio_accompaniment(dict(id='hi', tick=480), notation))
        self.assertTrue(is_arpeggio_accompaniment(dict(elem_ids=['lo', 'tie-end']), notation))
        self.assertFalse(is_arpeggio_accompaniment(dict(id='unknown'), notation))

    def test_arpeggio_filter_requires_explicit_complete_notation_and_respects_parts(self):
        chord = dict(elems=[dict(id='lo', step=1, octave=5), dict(id='hi', step=3, octave=5)])
        score = dict(parts=[dict(measures=[dict(notes=[chord])])])
        self.assertFalse(is_arpeggio_accompaniment(dict(id='lo'), arpeggio_accompaniment(score)))
        chord['arts'] = [dict(type='arpeggiate')]
        score['parts'].append(dict(measures=[dict(notes=[dict(elems=[dict(id='lo', step=4, octave=5)])])]))
        notation = arpeggio_accompaniment(score)
        self.assertTrue(is_arpeggio_accompaniment(dict(id='lo', part=0), notation))
        self.assertFalse(is_arpeggio_accompaniment(dict(id='lo', part=1), notation))
        del chord['elems'][1]['octave']
        self.assertFalse(is_arpeggio_accompaniment(dict(id='lo'), arpeggio_accompaniment(score)))

    def test_arpeggio_rank_uses_accidentals_and_preserves_top_unisons(self):
        chord = dict(arts=[dict(type='arpeggiate')], elems=[dict(id='flat', step=3, octave=5, alter=-1),
            dict(id='sharp', step=2, octave=5, alter=2), dict(id='unison', step=3, octave=5)])
        notation = arpeggio_accompaniment(dict(parts=[dict(measures=[dict(notes=[chord])])]))
        self.assertTrue(notation[0,'flat'])
        self.assertFalse(notation[0,'sharp'])
        self.assertFalse(notation[0,'unison'])

    def phonetic_fixture(self):
        times = np.arange(0, 9, .01)
        consonants = [.10, .20, .02, .04, .24, .06, .00, .15, .10, .20]
        notes = [dict(pitch=60 + i, scoreStart=i * .5, scoreEnd=(i + 1) * .5) for i in range(10)]
        tokens = []
        f0 = np.zeros(len(times))
        for i, lead in enumerate(consonants):
            start = 2 + i * .6 - .75 * lead
            tokens.append(dict(text=str(i), line='line-00', start=start, vowelStart=start + lead,
                               end=start + .45, pitchSupportFraction=0. if i == 4 else 1.,
                               sourceNotes=[i], noteSpans=[dict(note=i, startUnit=0, endUnit=2)],
                               needsReview=i == 4, reviewReasons=['vowel-pitch-mismatch'] if i == 4 else []))
            f0[(times >= start + lead) & (times < start + .45)] = 440 * 2 ** ((60 + i + (2 if i == 4 else 0) - 69) / 12)
        return tokens, notes, times, f0

    def test_phonetic_model_predicts_unseen_word_without_stretching_its_time(self):
        tokens, notes, times, f0 = self.phonetic_fixture()
        before = json.dumps(tokens)
        self.assertIsNone(unique_position(tokens[4], notes, timing_model(tokens, notes, [2, 3, 5, 6])))
        result = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
        self.assertEqual(result['corroborated'], 1)
        evidence = tokens[4]['positionEvidence']
        self.assertEqual(evidence['anchorIndices'], [1, 2, 3, 5, 6, 7])
        self.assertAlmostEqual(evidence['phoneticLeadScale'], .75)
        self.assertLess(evidence['heldOutMaximumErrorSeconds'], .001)
        original = json.loads(before)
        self.assertEqual([(t['start'], t['end'], t['noteSpans']) for t in tokens],
                         [(t['start'], t['end'], t['noteSpans']) for t in original])

    def test_phonetic_model_rejects_shifted_ambiguous_and_unseen_articulation(self):
        for problem in ('shift', 'ambiguous', 'extrapolation'):
            tokens, notes, times, f0 = self.phonetic_fixture()
            if problem == 'shift':
                tokens[4]['noteSpans'][0]['note'] = 5
                tokens[4]['sourceNotes'] = [5]
            elif problem == 'ambiguous':
                notes.append(dict(pitch=64, scoreStart=2.02, scoreEnd=2.5))
            else:
                tokens[4]['vowelStart'] = tokens[4]['start'] + .4
            result = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
            self.assertEqual(result['corroborated'], 0, problem)
            self.assertTrue(tokens[4]['needsReview'])

    def test_conflicting_timing_models_cannot_cherry_pick_the_current_assignment(self):
        tokens, notes, times, f0 = self.position_fixture()
        with patch('position_evidence.timing_model', return_value={'indices': []}), \
             patch('position_evidence.unique_position', side_effect=[{'best': (.02, 3)}, {'best': (.01, 4)}]):
            result = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
        self.assertEqual(result['corroborated'], 0)
        self.assertTrue(tokens[4]['needsReview'])

    def position_fixture(self):
        times = np.arange(0, 6, .01)
        notes = [dict(pitch=60 + i, scoreStart=i * .5, scoreEnd=(i + 1) * .5) for i in range(10)]
        tokens = [dict(text=str(i), line='line-00', start=i * .6, vowelStart=i * .6 + .05,
                       end=i * .6 + .5, pitchSupportFraction=1. if i != 4 else 0.,
                       sourceNotes=[i], noteSpans=[dict(note=i, startUnit=0, endUnit=2)],
                       needsReview=i == 4, reviewReasons=['vowel-pitch-mismatch'] if i == 4 else [])
                  for i in range(10)]
        pitch = np.array([60 + min(9, int(t / .6)) for t in times])
        f0 = 440 * 2 ** ((pitch - 69) / 12)
        f0[(times >= tokens[4]['start']) & (times < tokens[4]['end'])] = 440 * 2 ** ((66 - 69) / 12)
        return tokens, notes, times, f0

    def test_local_position_uses_neighbours_without_inventing_target_pitch(self):
        tokens, notes, times, f0 = self.position_fixture()
        before = json.dumps([t['noteSpans'] for t in tokens])
        report = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
        self.assertEqual(report['corroborated'], 1)
        self.assertFalse(tokens[4]['needsReview'])
        self.assertEqual(tokens[4]['acousticReviewReasons'], ['vowel-pitch-mismatch'])
        self.assertEqual(tokens[4]['positionEvidence']['anchorIndices'], [2, 3, 5, 6])
        self.assertEqual(json.dumps([t['noteSpans'] for t in tokens]), before)
        self.assertFalse(tokens[4]['positionEvidence']['changesAssignment'])

    def test_local_position_rejects_shifted_shared_and_ambiguous_notes(self):
        for problem in ('shift', 'shared', 'ambiguous', 'bad-anchor-time'):
            tokens, notes, times, f0 = self.position_fixture()
            if problem == 'shift':
                tokens[4]['noteSpans'][0]['note'] = 5
                tokens[4]['sourceNotes'] = [5]
            elif problem == 'shared':
                tokens[4]['noteSpans'][0]['endUnit'] = 1
            elif problem == 'ambiguous':
                notes.append(dict(pitch=64, scoreStart=2.02, scoreEnd=2.5))
            else:
                tokens[3]['start'] += .5
            report = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
            self.assertEqual(report['corroborated'], 0, problem)
            self.assertTrue(tokens[4]['needsReview'], problem)

    def test_local_position_cannot_bootstrap_unvoiced_words_as_anchors(self):
        tokens, notes, times, f0 = self.position_fixture()
        for token in tokens[:5]:
            token.update(needsReview=True, reviewReasons=['no-voiced-evidence'], pitchSupportFraction=0.)
        report = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
        self.assertEqual(report['corroborated'], 0)
        self.assertTrue(all(t['needsReview'] for t in tokens[:5]))

    def test_timing_alone_cannot_confirm_a_missing_or_unvoiced_syllable(self):
        tokens, notes, times, f0 = self.position_fixture()
        tokens[4]['reviewReasons'] = ['no-voiced-evidence']
        f0[(times >= tokens[4]['start']) & (times < tokens[4]['end'])] = 0
        report = corroborate_positions(tokens, notes, times, f0, times, f0, .25)
        self.assertEqual(report['corroborated'], 0)
        self.assertTrue(tokens[4]['needsReview'])

    def test_touching_display_lines_share_pickups_without_crossing_a_breath(self):
        tokens = [dict(text='吗', line='line-00', start=0, end=1.03),
                  dict(text='事', line='line-01', start=1.08, end=1.3),
                  dict(text='到', line='line-01', start=1.3, end=1.5),
                  dict(text='如', line='line-01', start=1.5, end=2),
                  dict(text='任', line='line-02', start=2.5, end=3)]
        lines = [tokens[:1], tokens[1:4], tokens[4:]]
        phrases = connected_phrases(lines)
        self.assertEqual([len(p) for p in phrases], [4, 1])
        units = note_units([(0, 1, 59), (1, 1.25, 59), (1.25, 1.5, 59), (1.5, 2, 67)])
        times = np.arange(0, 2, .01)
        f0 = 440 * 2 ** ((np.where(times < 1.5, 59, 67) - 69) / 12)
        groups = partition_phrase(phrases[0], units, times, f0)
        self.assertEqual([[u['note'] for u in g] for g in groups], [[0] * 4, [1], [2], [3] * 2])
        self.assertEqual([t['line'] for t in tokens], ['line-00', 'line-01', 'line-01', 'line-01', 'line-02'])
        self.assertEqual([len(line) for line in lines], [1, 3, 1])

    def test_audio_cache_requires_same_url_and_matching_original_bytes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            audio = root / 'jobs/real/audio'
            audio.mkdir(parents=True)
            raw = b'previously downloaded recording'
            recording = audio / 'reference.webm'
            recording.write_bytes(raw)
            (audio / 'provenance.json').write_text(json.dumps(dict(webpage_url='https://example.test/source',
                sha256=hashlib.sha256(raw).hexdigest())), encoding='utf-8')
            self.assertIsNone(cached_recording(root, 'https://example.test/other'))
            self.assertEqual(cached_recording(root, 'https://example.test/source')[0], recording)
            recording.write_bytes(b'changed bytes')
            self.assertIsNone(cached_recording(root, 'https://example.test/source'))
    def test_context_extension_includes_previously_inferred_neighbours(self):
        self.assertEqual(expand_groups([[50, 51]], [[49, 50]]), [[49, 50, 51]])
        self.assertEqual(expand_groups([[2, 3], [5, 6]], [[0, 1, 2], [3, 4, 5]]), [list(range(7))])
    def test_recording_lead_in_moves_crops_without_moving_lyric_text_or_gaps(self):
        import soundfile as sf
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'audio').mkdir()
            sf.write(root / 'audio/reference.wav', np.zeros(40 * 8000), 8000)
            (root / 'selected-lyrics.json').write_text(json.dumps(dict(id=1,
                syncedLyrics='[00:20.35]那是我\n[00:26.64]到底\n[00:30.20]\n[00:33.76]也许')), encoding='utf-8')
            (root / 'lyric-offset.json').write_text(json.dumps(dict(offsetSeconds=5.11)), encoding='utf-8')
            prepare(root)
            manifest = json.loads((root / 'segments.json').read_text('utf-8'))
            self.assertEqual([s['name'] for s in manifest], ['line-00', 'line-01', 'line-03'])
            self.assertEqual([s['text'] for s in manifest], ['那是我', '到底', '也许'])
            self.assertAlmostEqual(manifest[1]['offset'], 31.4)
            self.assertAlmostEqual(manifest[1]['end'], 35.46)
            self.assertAlmostEqual(sf.info(root / 'segments/line-01.wav').duration, 4.06)

    def test_song_identity_precedes_duration_and_rejects_truncated_metadata(self):
        def candidate(title, artist, duration, end):
            return dict(trackName=title, artistName=artist, duration=duration,
                        syncedLyrics=f'[00:20.35]那是我\n[{end}]亲爱的')
        correct = candidate('老男孩', '筷子兄弟', 300, '04:31.91')
        choices = [candidate('牧马城市-《老男孩》电视剧片尾曲', '毛不易', 258, '04:00'),
                   candidate('老男孩 (烟嗓版)', '半吨兄弟', 251, '04:00'),
                   candidate('老男孩', '筷子兄弟', 205, '04:31.91'), correct]
        score = dict(title=dict(composer='艺术家/歌手：筷子兄弟\n作曲：大桥卓弥'))
        self.assertIs(select_lyrics(choices, '老男孩', 258.33, score), correct)
        self.assertIs(select_lyrics(choices, '老男孩', 263.55,
            dict(title=dict(composer='艺术家/歌手：老男孩')),
            [dict(title='老男孩', artist='筷子兄弟 / Chopsticks Brothers')]), correct)
        with self.assertRaises(ValueError):
            select_lyrics(choices[:3], '老男孩', 258.33, score)
        with self.assertRaises(ValueError):
            select_lyrics(choices, '不存在的歌曲', 258.33, {})
        translated = candidate('夜空中最亮的星', 'Escape Plan', 250, '04:00')
        self.assertIs(select_lyrics([translated], '夜空中最亮的星', 246,
            dict(title=dict(composer='艺术家/歌手：逃跑计划')),
            [dict(title='夜空中最亮的星', artist='逃跑计划 / Escape Plan')]), translated)

    def test_reference_parser_keeps_pickups_durations_rests_and_explicit_words(self):
        score = parse_reference('<div id="jianpuOut">/key(B2)\nｂｐｍ１０８\n3_2_3--0_5,_|1.21.|\nL:夜空中能否聽清</div>')
        self.assertEqual([n['pitch'] for n in score['notes']], [51, 49, 51, 42, 47, 49, 47])
        self.assertEqual([n['text'] for n in score['notes']], list('夜空中能否聽清'))
        self.assertEqual(score['notes'][3]['beat'], 4.5)
        self.assertEqual(score['beats'], 9)
        with self.assertRaises(ValueError):
            parse_reference('<div id="jianpuOut">/key(C4)\ntriplet(123)\nL:春风来</div>')

    def test_reference_confirms_position_and_retains_acoustic_diagnostics(self):
        tokens, notes, reference = reference_fixture()
        before = json.dumps([t['noteSpans'] for t in tokens])
        report = corroborate_reference(tokens, notes, reference, 480, 1 / 480)
        self.assertTrue(report['match']['accepted'])
        self.assertEqual(report['match']['beatOffset'], 32)
        self.assertEqual(report['corroborated'], 1)
        self.assertFalse(tokens[5]['needsReview'])
        self.assertEqual(tokens[5]['acousticReviewReasons'], ['vowel-pitch-mismatch', 'distant-onset'])
        self.assertEqual(tokens[5]['referenceScoreEvidence']['sourceSha256'], 'a' * 64)
        self.assertEqual(json.dumps([t['noteSpans'] for t in tokens]), before)

    def test_reference_rejects_wrong_arrangement_shifted_labels_and_shared_note(self):
        tokens, notes, reference = reference_fixture()
        for note in reference['notes'][::2]:
            note['pitch'] += 1
        self.assertFalse(corroborate_reference(tokens, notes, reference, 480, 1 / 480)['match']['accepted'])
        self.assertTrue(tokens[5]['needsReview'])
        tokens, notes, reference = reference_fixture()
        reference['notes'][4]['text'] = '错'
        reference['notes'][6]['text'] = '错'
        self.assertEqual(corroborate_reference(tokens, notes, reference, 480, 1 / 480)['corroborated'], 0)
        self.assertTrue(tokens[5]['needsReview'])
        tokens, notes, reference = reference_fixture()
        tokens[5]['noteSpans'][0]['endUnit'] = 1
        self.assertEqual(corroborate_reference(tokens, notes, reference, 480, 1 / 480)['corroborated'], 0)

    def test_bracketed_reference_notes_need_unique_pitch_order(self):
        self.assertEqual(unique_pitch_run([1, 3, 6], [1, 3]), [(0, 0), (1, 1)])
        self.assertEqual(unique_pitch_run([1, 1], [1]), [])
        self.assertEqual(unique_pitch_run([1, 3], [4, 6]), [])

    def test_unavailable_reference_reuses_sourced_cache_without_manual_work(self):
        import requests
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            job, tools = root / 'job', root / 'tools'
            job.mkdir()
            (tools / 'references').mkdir(parents=True)
            url = 'https://jianpu.space/zh-tw/songList/test'
            page = '<div id="jianpuOut">/key(C4)\n123\nL:春风来</div>'.encode('utf-8')
            (tools / 'references' / (hashlib.sha256(url.encode()).hexdigest() + '.html')).write_bytes(page)
            with patch('requests.get', side_effect=requests.ConnectionError('offline')):
                prepare_reference(job, tools, url)
            result = json.loads((job / 'reference-score.json').read_text('utf-8'))
            self.assertEqual(result['retrieval'], 'cached-source')
            self.assertEqual(result['sha256'], hashlib.sha256(page).hexdigest())
            self.assertEqual([n['text'] for n in result['notes']], list('春风来'))
    def test_independent_pitch_resolves_target_and_neighbour_glide(self):
        times = np.arange(0, .4, .01)
        notes = [dict(pitch=63)]
        token = dict(vowelStart=0, end=.4, sourceNotes=[0])
        pitches = np.r_[np.linspace(61, 63, 30), np.full(10, 63.)]
        f0 = 440 * 2 ** ((pitches - 69) / 12)
        evidence = inspect_pitch(token, notes, times, f0, f0, previous=61)
        self.assertEqual(evidence['status'], 'corroborated-transition')
        self.assertFalse(evidence['changesAssignment'])
        stable = np.full(len(times), 440 * 2 ** ((63 - 69) / 12))
        self.assertEqual(inspect_pitch(token, notes, times, stable, stable)['status'], 'corroborated-target')

    def test_pitch_review_keeps_wrong_pitch_brief_crossing_and_weak_voice_flagged(self):
        times = np.arange(0, .4, .01)
        notes = [dict(pitch=63)]
        token = dict(vowelStart=0, end=.4, sourceNotes=[0])
        for pitches in (np.full(40, 61.), np.r_[np.full(18, 61.), np.full(4, 63.), np.full(18, 61.)]):
            f0 = 440 * 2 ** ((pitches - 69) / 12)
            self.assertFalse(inspect_pitch(token, notes, times, f0, f0, previous=61, following=61)['status'].startswith('corroborated-'))
        f0 = np.full(40, np.nan)
        f0[:4] = 440 * 2 ** ((63 - 69) / 12)
        self.assertEqual(inspect_pitch(token, notes, times, f0, f0)['status'], 'insufficient-voicing')

    def test_descending_fifth_release_is_not_changed_into_ascending_fourth(self):
        times = np.arange(0, .5, .01)
        pitches = np.r_[np.full(15, 66.), np.linspace(66, 59, 35)]
        f0 = 440 * 2 ** ((pitches - 69) / 12)
        token = dict(vowelStart=0, end=.5, sourceNotes=[0])
        evidence = inspect_pitch(token, [dict(pitch=90)], times, f0, f0, following=83)
        self.assertEqual(evidence['status'], 'corroborated-transition')

    def test_phrase_entry_scoop_needs_stable_final_target(self):
        times = np.arange(0, .4, .01)
        token = dict(vowelStart=0, end=.4, sourceNotes=[0])
        pitches = np.r_[np.linspace(58, 63, 25), np.full(15, 63.)]
        f0 = 440 * 2 ** ((pitches - 69) / 12)
        evidence = inspect_pitch(token, [dict(pitch=87)], times, f0, f0)
        self.assertEqual(evidence['status'], 'corroborated-onset-scoop')
        pitches[-10:] = 61
        f0 = 440 * 2 ** ((pitches - 69) / 12)
        self.assertFalse(inspect_pitch(token, [dict(pitch=87)], times, f0, f0)['status'].startswith('corroborated-'))

    def test_multi_note_word_cannot_pass_on_only_one_of_its_pitches(self):
        times = np.arange(0, .4, .01)
        token = dict(vowelStart=0, end=.4, sourceNotes=[0, 1])
        notes = [dict(pitch=63), dict(pitch=66)]
        f0 = np.full(len(times), 440 * 2 ** ((63 - 69) / 12))
        self.assertFalse(inspect_pitch(token, notes, times, f0, f0)['status'].startswith('corroborated-'))

    def test_high_union_pitch_support_still_requests_melisma_order_review(self):
        notes = [dict(pitch=p) for p in [62, 64, 74]]
        tokens = [dict(sourceNotes=ids, reviewReasons=[], needsReview=False, pitchSupportFraction=1.)
                  for ids in ([0, 1], [0], [0, 2])]
        request_melisma_review(tokens, notes)
        request_melisma_review(tokens, notes)
        self.assertEqual(tokens[0]['reviewReasons'], ['melisma-order-unverified'])
        self.assertTrue(tokens[0]['needsReview'])
        self.assertEqual([t['needsReview'] for t in tokens[1:]], [False, False])

    def test_melisma_order_request_is_cleared_only_by_full_order_evidence(self):
        times = np.arange(0, .4, .01)
        notes = [dict(pitch=62), dict(pitch=64)]
        for pitches, correct in (([62., 64.], True), ([64., 62.], False), ([62., 62.], False)):
            token = dict(text='风', line='line-00', start=0, vowelStart=0, end=.4,
                         sourceNotes=[0, 1], reviewReasons=['distant-onset'], needsReview=True,
                         pitchSupportFraction=1., noteSpans=[dict(note=0), dict(note=1)])
            request_melisma_review([token], notes)
            f0 = 440 * 2 ** ((np.repeat(pitches, 20) - 69) / 12)
            rows = corroborate([token], notes, times, f0, times, f0)
            self.assertEqual(len(rows), 1)
            self.assertEqual('melisma-order-unverified' not in token['reviewReasons'], correct)
            self.assertIn('distant-onset', token['reviewReasons'])
            self.assertTrue(token['needsReview'])
            self.assertEqual(token['noteSpans'], [dict(note=0), dict(note=1)])

    def test_multi_note_evidence_accepts_sustained_order_and_return(self):
        times = np.arange(0, .6, .01)
        token = dict(vowelStart=0, end=.6, sourceNotes=[0, 1, 2])
        notes = [dict(pitch=p) for p in [62, 64, 62]]
        pitches = np.repeat([62., 64., 62.], 20)
        f0 = 440 * 2 ** ((pitches - 69) / 12)
        evidence = inspect_pitch(token, notes, times, f0, f0)
        self.assertEqual(evidence['status'], 'corroborated-target')
        self.assertEqual(evidence['targetPitchOrder'], [2, 4, 2])
        self.assertTrue(evidence['targetOrderSatisfied'])

    def test_multi_note_evidence_rejects_reversed_order(self):
        times = np.arange(0, .4, .01)
        token = dict(vowelStart=0, end=.4, sourceNotes=[0, 1])
        notes = [dict(pitch=62), dict(pitch=64)]
        pitches = np.repeat([64., 62.], 20)
        f0 = 440 * 2 ** ((pitches - 69) / 12)
        evidence = inspect_pitch(token, notes, times, f0, f0)
        self.assertEqual(evidence['status'], 'out-of-order-targets')
        self.assertFalse(evidence['targetOrderSatisfied'])

    def test_multi_note_evidence_requires_the_final_return_and_contiguous_runs(self):
        times = np.arange(0, .6, .01)
        token = dict(vowelStart=0, end=.6, sourceNotes=[0, 1, 2])
        notes = [dict(pitch=p) for p in [62, 64, 62]]
        for pitches in (np.repeat([62., 64.], 30),
                        np.r_[np.repeat([62., 64.], 20), np.tile([62., np.nan], 10)]):
            f0 = 440 * 2 ** ((pitches - 69) / 12)
            evidence = inspect_pitch(token, notes, times, f0, f0)
            self.assertEqual(evidence['status'], 'out-of-order-targets')

    def test_multi_note_semitone_midpoint_cannot_prove_two_different_pitches(self):
        times = np.arange(0, .4, .01)
        token = dict(vowelStart=0, end=.4, sourceNotes=[0, 1])
        notes = [dict(pitch=63), dict(pitch=64)]
        f0 = np.full(len(times), 440 * 2 ** ((63.5 - 69) / 12))
        evidence = inspect_pitch(token, notes, times, f0, f0)
        self.assertEqual(evidence['status'], 'out-of-order-targets')

    def test_pitch_corroboration_does_not_clear_other_diagnostics_or_change_spans(self):
        times = np.arange(0, .4, .01)
        f0 = np.full(len(times), 440 * 2 ** ((63 - 69) / 12))
        spans = [dict(note=0, startUnit=0, endUnit=2)]
        token = dict(text='那', line='line-00', start=0, vowelStart=0, end=.4,
            sourceNotes=[0], noteSpans=spans, pitchSupportFraction=.5,
            reviewReasons=['vowel-pitch-mismatch', 'distant-onset'], needsReview=True)
        rows = corroborate([token], [dict(pitch=87)], times, f0, times, f0)
        self.assertEqual(token['reviewReasons'], ['distant-onset'])
        self.assertTrue(token['needsReview'])
        self.assertEqual(token['noteSpans'], spans)
        self.assertEqual(rows[0]['originalReasons'], ['vowel-pitch-mismatch', 'distant-onset'])

    def test_only_overlapping_lyric_lines_need_context_refinement(self):
        segments = [dict(offset=0), dict(offset=4), dict(offset=8), dict(offset=12)]
        intervals = [[SimpleNamespace(minTime=0, maxTime=5)], [SimpleNamespace(minTime=0, maxTime=3)],
                     [SimpleNamespace(minTime=0, maxTime=5)], [SimpleNamespace(minTime=0, maxTime=3)]]
        self.assertEqual(overlapping_groups(segments, intervals), [[0, 1], [2, 3]])
    def test_mixed_lyrics_preserve_display_and_detect_missing_word(self):
        self.assertEqual(syllables("Oh越过，don't go"), ['Oh', '越', '过', "don't", 'go'])
        intervals = [SimpleNamespace(mark='哦'), SimpleNamespace(mark='越')]
        self.assertEqual([t for t, _ in restore_tokens(['Oh', '越'], intervals)], ['Oh', '越'])
        with self.assertRaises(ValueError):
            restore_tokens(['Oh', '越'], intervals[1:])

    def test_sustained_note_can_split_before_later_notes(self):
        tokens = [dict(start=i, vowelStart=i + .05, end=i + 1) for i in range(3)]
        units = note_units([(0, 2, 60), (2, 3, 60)])
        times = np.arange(0, 3, .04)
        groups = partition_phrase(tokens, units, times, np.full(len(times), 261.625565))
        self.assertEqual([[u['note'] for u in g] for g in groups], [[0] * 4, [0] * 4, [1] * 4])
        self.assertEqual(sum(map(len, groups)), len(units))

    def test_melisma_is_not_flattened_or_split_without_need(self):
        tokens = [dict(start=0, vowelStart=0, end=2), dict(start=2, vowelStart=2, end=3)]
        units = note_units([(0, 1, 60), (1, 2, 62), (2, 3, 64)])
        times = np.arange(0, 3, .04)
        midi = np.where(times < 1, 60, np.where(times < 2, 62, 64))
        f0 = 440 * 2 ** ((midi - 69) / 12)
        groups = partition_phrase(tokens, units, times, f0)
        self.assertEqual([set(u['note'] for u in g) for g in groups], [{0, 1}, {2}])

    def test_insufficient_playable_duration_rejects_incomplete_result(self):
        tokens = [dict(start=0, end=1)] * 3
        with self.assertRaises(ValueError):
            partition_phrase(tokens, note_units([(0, .5, 60)]), np.array([.1]), np.array([261.6]))

    @unittest.skipUnless(os.environ.get('PIANO_LYRICS_REAL_JOB'), 'real sample not configured')
    def test_real_lyric_coverage_ranges_and_phrase_boundary(self):
        job = Path(os.environ['PIANO_LYRICS_REAL_JOB'])
        data = json.loads((job / 'candidate-alignment.json').read_text(encoding='utf-8'))
        manifest = json.loads((job / 'segments.json').read_text(encoding='utf-8'))
        expected = [t for s in manifest for t in syllables(s['text'])]
        self.assertEqual([t['text'] for t in data['tokens']], expected)
        for left, right in zip(data['tokens'], data['tokens'][1:]):
            self.assertLessEqual(left['end'], right['start'] + .001)
        occupied = []
        for token in data['tokens']:
            self.assertTrue(token['noteSpans'])
            occupied.extend((s['note'], u) for s in token['noteSpans'] for u in range(s['startUnit'], s['endUnit']))
        self.assertEqual(occupied, sorted(set(occupied)))
        words = {(t['line'], t['text']): t for t in data['tokens']}
        source_ids = lambda word: [data['notes'][n]['sourceId'] for n in word['sourceNotes']]
        self.assertEqual(source_ids(words['line-31', '裡']), ['n_94_11'])
        self.assertEqual(source_ids(words['line-32', 'Oh']), ['n_95_4', 'n_95_6', 'n_95_8'])
        self.assertEqual(source_ids(words['line-16', '來']), ['n_57_8', 'n_57_10'])
        self.assertEqual(source_ids(words['line-16', '臨']), ['n_58_4', 'n_58_9', 'n_58_11'] if data.get('algorithmVersion',0)>=12
                         else ['n_58_4', 'n_58_8', 'n_58_9', 'n_58_10', 'n_58_11'])
        if data.get('algorithmVersion',0)>=12:
            self.assertEqual({e['sourceId'] for e in data['melodyExtraction']['filteredAccompaniment']}, {'n_58_8','n_58_10','n_110_0'})
            self.assertFalse(any(n['sourceId'] in {'n_58_8','n_58_10','n_110_0'} for n in data['notes']))
        self.assertEqual(data['splitNotes'], 3)
        if data.get('algorithmVersion', 0) >= 3:
            report = json.loads((job / 'pitch-review.json').read_text(encoding='utf-8'))
            examined = 69 if data.get('algorithmVersion', 0) >= 12 else 70 if data.get('algorithmVersion', 0) >= 11 else 64
            self.assertEqual(len(report), examined)
            supported = 48 if data.get('algorithmVersion', 0) >= 12 else 47 if data.get('algorithmVersion', 0) >= 11 else 43 if data.get('algorithmVersion', 0) >= 4 else 31
            self.assertEqual(sum(r['status'].startswith('corroborated-') for r in report), supported)
            self.assertEqual(sum(t['needsReview'] for t in data['tokens']),
                             0 if data.get('algorithmVersion', 0) >= 12 else 2 if data.get('algorithmVersion', 0) >= 11 else 0 if data.get('algorithmVersion', 0) >= 5 else 64 - supported)
            self.assertEqual(len({r['index'] for r in report}), examined)
            self.assertTrue(all(not r['changesAssignment'] for r in report))
            if data.get('algorithmVersion', 0) == 11:
                self.assertEqual({i for i,t in enumerate(data['tokens']) if t['needsReview']}, {167,336})
                self.assertTrue(all('melisma-order-unverified' in data['tokens'][i]['reviewReasons'] for i in (167,336)))
        if data.get('algorithmVersion', 0) >= 5:
            reference_report = json.loads((job / 'reference-review.json').read_text(encoding='utf-8'))
            self.assertTrue(reference_report['match']['accepted'])
            self.assertEqual(reference_report['corroborated'], 21)
            self.assertEqual(sum(bool(t.get('acousticReviewReasons')) for t in data['tokens']), 21)
            self.assertEqual(sum(bool(t.get('referenceScoreEvidence')) for t in data['tokens']), 21)
            self.assertTrue(all(t['referenceScoreEvidence']['sourceSha256'] for t in data['tokens'] if t.get('referenceScoreEvidence')))

    @unittest.skipUnless(os.environ.get('PIANO_LYRICS_SECOND_REAL_JOB'), 'second real sample not configured')
    def test_second_real_song_covers_all_words_and_retains_final_vowel(self):
        job = Path(os.environ['PIANO_LYRICS_SECOND_REAL_JOB'])
        data = json.loads((job / 'candidate-alignment.json').read_text('utf-8'))
        manifest = json.loads((job / 'segments.json').read_text('utf-8'))
        lyrics = json.loads((job / 'selected-lyrics.json').read_text('utf-8'))
        self.assertEqual(lyrics['trackName'], '老男孩')
        self.assertEqual(lyrics['artistName'], '筷子兄弟')
        self.assertEqual([t['text'] for t in data['tokens']], [w for s in manifest for w in syllables(s['text'])])
        self.assertEqual(len(data['tokens']), 462)
        self.assertEqual(data['unmappedTokens'], 0)
        occupied = []
        for token in data['tokens']:
            self.assertTrue(token['noteSpans'])
            occupied.extend((s['note'], u) for s in token['noteSpans'] for u in range(s['startUnit'], s['endUnit']))
        self.assertEqual(occupied, sorted(set(occupied)))
        for left, right in zip(data['tokens'], data['tokens'][1:]):
            self.assertLessEqual(left['end'], right['start'] + .001)
        last = data['tokens'][-1]
        self.assertEqual(last['text'], '的')
        self.assertGreater(last['end'] - last['start'], .08)
        self.assertNotIn('collapsed-acoustic-token', last['reviewReasons'])
        if data.get('algorithmVersion', 0) >= 7:
            # Two touching LRC boundaries used to donate a pickup to the
            # previous line and force the next two words onto a split note.
            for index, notes in {333: [474], 334: [475], 335: [476], 336: [477],
                                 397: [539], 398: [540], 399: [541], 400: [542]}.items():
                self.assertEqual(data['tokens'][index]['sourceNotes'], notes)
            for index in (335, 399, 400):
                self.assertFalse(data['tokens'][index]['needsReview'])
            self.assertEqual(sum(t['needsReview'] for t in data['tokens']),
                             25 if data.get('algorithmVersion', 0) >= 14 else 28 if data.get('algorithmVersion', 0) >= 11 else 18 if data.get('algorithmVersion', 0) >= 9 else
                             19 if data.get('algorithmVersion', 0) >= 8 else 32)
            if data.get('algorithmVersion', 0) >= 11:
                report = json.loads((job / 'pitch-review.json').read_text('utf-8'))
                self.assertEqual(len(report), 129)
                self.assertEqual(sum(r['status'].startswith('corroborated-') for r in report), 87)
                new = {67,83,118,173,214,230,294,308,320,322}
                old = {8,27,42,64,77,79,80,189,196,197,200,202,209,211,215,220,225,304}
                if data.get('algorithmVersion', 0) >= 14:
                    old -= {42, 209, 211}
                self.assertEqual({i for i,t in enumerate(data['tokens']) if t['needsReview']}, old | new)
                self.assertTrue(all('melisma-order-unverified' in data['tokens'][i]['reviewReasons'] for i in new | {200}))
        if data.get('algorithmVersion', 0) >= 8:
            expected = {13, 17, 18, 28, 194, 228, 233, 272, 403, 404, 413, 417, 437}
            if data.get('algorithmVersion', 0) >= 9:
                expected.add(239)
            if data.get('algorithmVersion', 0) >= 14:
                expected |= {42, 209, 211}
                for index, note, owner in [(42, 64, 43), (209, 255, 208), (211, 259, 212)]:
                    evidence = data['tokens'][index]['positionEvidence']
                    self.assertEqual(evidence['occupiedSingleUnitNotes'][str(note)], owner)
                    self.assertIn(owner, evidence['anchorIndices'])
            supported = {i for i, t in enumerate(data['tokens']) if t.get('positionEvidence')}
            self.assertEqual(supported, expected)
            self.assertEqual(data['positionReview']['corroborated'], len(expected))
            self.assertTrue(all(t['acousticReviewReasons'] and not t['positionEvidence']['changesAssignment']
                                for t in data['tokens'] if t.get('positionEvidence')))


if __name__ == '__main__':
    unittest.main()
