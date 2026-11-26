"""Match song identity before comparing arrangement durations."""
import re
import unicodedata


def song_name(value):
    value = unicodedata.normalize('NFKC', value)
    value = re.sub(r'[（(【\[].*?[）)】\]]', '', value)
    return ''.join(c.lower() for c in value if c.isalnum())


def select_lyrics(candidates, title, duration, score, known_sources=()):
    metadata = score.get('title', {})
    composer = metadata.get('composer', '') if isinstance(metadata, dict) else ''
    artist = re.search(r'艺术家/歌手[：:]\s*([^\n]+)', composer)
    artist = song_name(artist[1]) if artist else ''
    wanted = song_name(title)
    if artist == wanted:
        artist = ''  # Some real CCMZ metadata repeats the song as its singer.
    aliases = {artist} if artist else set()
    for source in known_sources:
        names = {song_name(a) for a in source.get('artist', '').split('/')}
        if song_name(source['title']) == wanted and (not artist or artist in names):
            aliases.update(names)
    matches = []
    for item in candidates:
        lrc = item.get('syncedLyrics') or ''
        if not lrc or song_name(item.get('trackName', '')) != wanted:
            continue
        # Reject metadata that truncates its own timed lyrics (a real LRCLIB
        # entry declares 205 s while still containing lyrics after 270 s).
        times = [int(m) * 60 + float(s) for m, s in re.findall(r'\[(\d+):(\d+(?:\.\d+)?)\]', lrc)]
        if not times or max(times) > item.get('duration', 0) + 1:
            continue
        matches.append(item)
    if aliases:
        same_artist = [x for x in matches if any(a in song_name(x.get('artistName', '')) for a in aliases if a)]
        if not same_artist:
            raise ValueError('没有找到与源谱曲名和歌手对应的同步歌词，未更改曲谱')
        matches = same_artist
    if not matches:
        raise ValueError('没有找到与源谱同曲的有效同步歌词，未更改曲谱')
    return min(matches, key=lambda x: abs(x.get('duration', 0) - duration))
