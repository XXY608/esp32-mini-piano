(function (root) {
  'use strict';
  function parseScore(text) {
    const notes = [];
    let octave = 0, closing = '', sharp = false, barBefore = false, lineBefore = false, phraseBefore = false;
    let modifierAllowed = false, reductions = 0, dotted = false, extended = false;
    for (let i = 0; i < text.length; i++) {
      const c = text[i];
      if (/\s/u.test(c)) { if (/[\r\n\u2028\u2029]/u.test(c)) lineBefore = true; continue; }
      if (c === '\uFEFF') continue;
      if (c === '|' || c === '｜') { barBefore = true; modifierAllowed = false; continue; }
      if (c === '_' || c === '.' || c === '-' || c === '—' || c === '–') {
        if (!modifierAllowed || !notes.length || sharp) throw Error('时值符号必须跟在音符或休止符后，不能跨小节');
        const previous = notes[notes.length - 1];
        if (c === '_') {
          if (dotted || extended || reductions >= 2) throw Error('减时符最多两条，且须写在附点和延长符之前');
          reductions++; previous.durationBeats /= 2;
        } else if (c === '.') {
          if (dotted || extended) throw Error('仅支持一个附点，须写在延长符之前');
          dotted = true; previous.durationBeats *= 1.5;
        } else {
          extended = true; previous.durationBeats++;
          if (previous.durationBeats > 64) throw Error('单音不能超过 64 拍');
        }
        continue;
      }
      const pairs = { '【':['】',1], '[':[']',1], '（':['）',-1], '(':[')',-1] };
      if (pairs[c]) {
        if (closing) throw Error('不支持嵌套音区括号');
        if (sharp) throw Error('请把 # 写在括号内音符之前');
        [closing, octave] = pairs[c]; continue;
      }
      if ('】]）)'.includes(c)) {
        if (c !== closing) throw Error('音区括号不匹配：' + c);
        if (sharp) throw Error('# 后面缺少音符');
        closing = ''; octave = 0; continue;
      }
      if (c === '^') {
        if (phraseBefore || sharp) throw Error('分句标记位置无效');
        phraseBefore = true; continue;
      }
      if (c === '#') {
        if (sharp) throw Error('不支持连续 #');
        sharp = true; continue;
      }
      if (c >= '0' && c <= '7') {
        const pitch = Number(c);
        if (pitch === 0 && (sharp || octave)) throw Error('休止符不能升半音或使用音区括号');
        const note = { pitch, octave, sharp, durationBeats: 1 };
        if (barBefore) note.barBefore = true;
        if (lineBefore) note.lineBefore = true;
        if (phraseBefore) note.phraseBefore = true;
        notes.push(note);
        if (notes.length > 1024) throw Error('每首最多 1024 个音符');
        sharp = false; barBefore = false; lineBefore = false; phraseBefore = false;
        modifierAllowed = true; reductions = 0; dotted = false; extended = false;
        continue;
      }
      throw Error('不支持的字符：' + c);
    }
    if (sharp) throw Error('# 后面缺少音符');
    if (phraseBefore) throw Error('分句标记后缺少音符');
    if (closing) throw Error('音区括号不匹配，缺少 ' + closing);
    if (!notes.length) throw Error('谱面为空');
    return notes;
  }
  function validateSong(song) {
    if (!song || typeof song !== 'object' || !/^[A-Za-z0-9_-]{1,64}$/.test(song.id || '')) throw Error('歌曲 ID 无效');
    if (song.id === 'demo-scale') throw Error('内置曲目的 ID 不能覆盖');
    if (typeof song.title !== 'string' || !song.title || new TextEncoder().encode(song.title).length > 240) throw Error('曲名不能为空或超过 240 字节');
    if (song.bpm != null && (!Number.isInteger(song.bpm) || song.bpm < 20 || song.bpm > 400)) throw Error('BPM 应为 20～400 的整数');
    if (!Array.isArray(song.notes) || !song.notes.length || song.notes.length > 1024) throw Error('歌曲需要 1～1024 个音符');
    if (song.polyphony !== undefined && (!performanceCount(song.polyphony))) throw Error('多音时间轴格式无效');
    if (song.pedal !== undefined && (!song.polyphony || !Array.isArray(song.pedal) || song.pedal.length > 512 ||
        song.pedal.some((event, i) => !Number.isInteger(event) || event < 0 || event > 125829121 ||
          (i > 0 && (event >>> 1) < (song.pedal[i - 1] >>> 1))))) throw Error('延音踏板时间轴格式无效');
    for (const n of song.notes) if (!n || !Number.isInteger(n.pitch) || n.pitch < 0 || n.pitch > 7 || !Number.isInteger(n.octave) || Math.abs(n.octave) > 1 || typeof n.sharp !== 'boolean' || !Number.isFinite(n.durationBeats) || n.durationBeats < 1 / 64 || n.durationBeats > 64) throw Error('音符字段或时值无效');
    for (const n of song.notes) for (const field of ['barBefore','lineBefore','phraseBefore']) if (n[field] !== undefined && typeof n[field] !== 'boolean') throw Error('小节/换行标记必须为布尔值');
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
  function performanceCount(value) {
    if (typeof value !== 'string' || !value.length || value.length > 54616 || value.length % 4 || !/^[A-Za-z0-9+/]+={0,2}$/.test(value)) return 0;
    const padding = value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0;
    const bytes = value.length / 4 * 3 - padding;
    return bytes % 10 || bytes > 40960 ? 0 : bytes / 10;
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
  root.PianoScore = { parseScore, validateSong, withLyrics, performanceCount };
  if (typeof module !== 'undefined') module.exports = root.PianoScore;
})(typeof globalThis !== 'undefined' ? globalThis : this);
