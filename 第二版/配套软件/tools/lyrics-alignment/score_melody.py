"""Recognize accompaniment from explicit notation, not MIDI onset proximity."""


def arpeggio_accompaniment(score):
    result = {}
    for part_index, part in enumerate(score.get('parts', [])):
        for measure in part.get('measures', []):
            for note in measure.get('notes', []):
                elements = note.get('elems', [])
                rolled = len(elements) > 1 and any(a.get('type') == 'arpeggiate' for a in note.get('arts', []))
                pitches = []
                for element in elements:
                    step, octave, alter = element.get('step'), element.get('octave'), element.get('alter', 0)
                    if not (isinstance(step, int) and 1 <= step <= 7 and
                            isinstance(octave, int) and isinstance(alter, int)):
                        pitches = []
                        break
                    pitches.append(12 * (octave + 1) + [0, 2, 4, 5, 7, 9, 11][step - 1] + alter)
                complete = len(pitches) == len(elements) and bool(pitches)
                for index, element in enumerate(elements):
                    if isinstance(element.get('id'), str):
                        result[part_index, element['id']] = bool(rolled and complete and pitches[index] < max(pitches))
    return result


def is_arpeggio_accompaniment(event, notation):
    part = event.get('part', 0)
    for source_id in [event.get('id')] + event.get('elem_ids', []):
        key = part, source_id
        if key in notation:
            return notation[key]
    return False
