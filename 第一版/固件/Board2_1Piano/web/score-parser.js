(function (root) {
  'use strict';
  function parseScore(text) {
    const tokens = text.replace(/^\uFEFF/, '').match(/\||\r\n|[\r\n]|[^\s|]+/g) || [];
    const notes = [];
    let barBefore = false, lineBefore = false;
    for (const token of tokens) {
      if (token === '|') { barBefore = true; continue; }
      if (/^[\r\n]+$/.test(token)) { lineBefore = true; continue; }
      if (/^(—|–|-)$/.test(token)) {
        if (!notes.length) throw Error('延长线前缺少音符');
        notes[notes.length - 1].durationBeats += 1;
        if (notes[notes.length - 1].durationBeats > 64) throw Error('单音不能超过 64 拍');
        continue;
      }
      let value = token, octave = 0;
      const pairs = [['【', '】', 1], ['[', ']', 1], ['（', '）', -1], ['(', ')', -1]];
      for (const [left, right, range] of pairs) if (value.startsWith(left)) {
        if (!value.endsWith(right)) throw Error('音区括号不匹配：' + token);
        value = value.slice(1, -1); octave = range; break;
      }
      const match = /^(#?[1-7]|0)(_{0,2})(\.)?$/.exec(value);
      if (!match) throw Error('不支持的音符：' + token);
      const pitch = Number(match[1].replace('#', ''));
      if (pitch === 0 && octave) throw Error('休止符不需要音区括号');
      notes.push({ pitch, octave, sharp: match[1].startsWith('#'), durationBeats: Math.pow(.5, match[2].length) * (match[3] ? 1.5 : 1) });
      if (barBefore) notes[notes.length - 1].barBefore = true;
      if (lineBefore) notes[notes.length - 1].lineBefore = true;
      barBefore = false; lineBefore = false;
      if (notes.length > 1024) throw Error('每首最多 1024 个音符');
    }
    if (!notes.length) throw Error('谱面为空');
    return notes;
  }
  function validateSong(song) {
    if (!song || typeof song !== 'object' || !/^[A-Za-z0-9_-]{1,64}$/.test(song.id || '')) throw Error('歌曲 ID 无效');
    if (song.id === 'demo-scale') throw Error('内置曲目的 ID 不能覆盖');
    if (typeof song.title !== 'string' || !song.title || new TextEncoder().encode(song.title).length > 240) throw Error('曲名不能为空或超过 240 字节');
    if (song.bpm != null && (!Number.isInteger(song.bpm) || song.bpm < 20 || song.bpm > 400)) throw Error('BPM 应为 20～400 的整数');
    if (!Array.isArray(song.notes) || !song.notes.length || song.notes.length > 1024) throw Error('歌曲需要 1～1024 个音符');
    for (const n of song.notes) if (!n || !Number.isInteger(n.pitch) || n.pitch < 0 || n.pitch > 7 || !Number.isInteger(n.octave) || Math.abs(n.octave) > 1 || typeof n.sharp !== 'boolean' || !Number.isFinite(n.durationBeats) || n.durationBeats < 1 / 64 || n.durationBeats > 64) throw Error('音符字段或时值无效');
    for (const n of song.notes) for (const field of ['barBefore','lineBefore']) if (n[field] !== undefined && typeof n[field] !== 'boolean') throw Error('小节/换行标记必须为布尔值');
    for (const n of song.notes) if (n.lyric !== undefined && (typeof n.lyric !== 'string' || new TextEncoder().encode(n.lyric).length > 24 || /[\x00-\x1f\x7f]/.test(n.lyric) || (n.pitch === 0 && n.lyric))) throw Error('逐音歌词应为单行文字，每音最多 24 字节，休止符不填歌词');
    if (song.timeSignature != null && (!/^(?:[1-9]|[12][0-9]|3[0-2])\/(?:1|2|4|8|16|32)$/.test(song.timeSignature) || typeof song.timeSignature !== 'string')) throw Error('拍号应为 4/4、3/4、6/8 等格式');
    if (song.lyricCues != null) {
      if (!Array.isArray(song.lyricCues) || song.lyricCues.length > 256) throw Error('时间轴歌词最多256句');
      const total = song.notes.reduce((sum,n) => sum + n.durationBeats, 0);
      let previous = -1, bytes = 0;
      for (const cue of song.lyricCues) {
        if (!cue || !Number.isFinite(cue.startBeat) || cue.startBeat < 0 || cue.startBeat >= total || cue.startBeat <= previous || typeof cue.text !== 'string' || /[\x00-\x1f\x7f]/.test(cue.text)) throw Error('歌词时间轴无效');
        previous = cue.startBeat;
        const size = new TextEncoder().encode(cue.text).length; bytes += size;
        if (size > 512 || bytes > 32768) throw Error('时间轴歌词每句最多512字节、总计32KB');
      }
    }
    if (new TextEncoder().encode(JSON.stringify(song)).length > 120 * 1024) throw Error('歌曲超过 120 KiB');
    return song;
  }
  function withLyrics(song, text) {
    const words = text.trim() ? text.trim().split(/\s+/u) : [];
    const sounded = song.notes.filter(n => n.pitch !== 0);
    if (words.length && words.length !== sounded.length) throw Error('歌词需要 ' + sounded.length + ' 项，当前 ' + words.length + ' 项；空格分隔，缺词用 _，休止符和延长线不占项');
    if (words.length) delete song.lyricCues;
    for (const n of song.notes) delete n.lyric;
    sounded.forEach((n,i) => { if (words[i] && words[i] !== '_') n.lyric = words[i]; });
    return validateSong(song);
  }
  root.PianoScore = { parseScore, validateSong, withLyrics };
  if (typeof module !== 'undefined') module.exports = root.PianoScore;
})(typeof globalThis !== 'undefined' ? globalThis : this);
