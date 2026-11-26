"use strict";

const el = id => document.getElementById(id);
const width = 320, height = 240;
const canvas = el("screen").getContext("2d");
const rgba = canvas.createImageData(width, height);
const settingsKey = "mini-piano-full-settings-v1";
const songsKey = "mini-piano-full-songs-v1";
const layout = [
  ["KeyZ", "Z", "1"], ["KeyX", "X", "2"], ["KeyC", "C", "3"], ["KeyV", "V", "4"],
  ["KeyB", "B", "5"], ["KeyN", "N", "6"], ["KeyM", "M", "7"],
  ["Comma", ",", "降八度"], ["Period", ".", "升半音"], ["Slash", "/", "升八度 / 菜单"]
];
const keyboard = new Map(layout.map(([code], index) => [code, index]));
const menuNames = ["", "菜单首页", "开始演奏或播放音乐", "音色与音量", "节奏与调性",
  "手机连接", "设备状态", "选择歌曲", "逐键检查", "演奏模式", "删除歌曲", "演奏界面"];
const homeNames = ["选择歌曲", "演奏模式", "音色与音量", "演奏界面", "手机连接", "设备状态"];
const launchNames = ["开始演奏", "播放音乐", "播放模式", "显示歌词"];
const gameNames = ["", "单键伴奏", "自主节奏", "复杂 多音"];
const modeNames = ["自由演奏", "自动播放", "跟弹练习", "自由演奏", "简单 单音", "节奏练习"];
const buttons = [];
const sources = new Map();
const pendingImports = new Map();
let context, output, active = false, mask = 0, frameAt = 0, revision = -1;
let frameBuffer = new ArrayBuffer(width * height * 2);
let bankInFlight = false, pendingBank = 0, startup = null;
let storedSongs = getStored(songsKey, []);
if (!Array.isArray(storedSongs)) storedSongs = [];

function getStored(key, fallback) {
  try { return JSON.parse(localStorage.getItem(key)) ?? fallback; }
  catch { return fallback; }
}

function save(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); return true; }
  catch { return false; }
}

function updateKey(source, index) {
  if (index < 0) sources.delete(source);
  else if (active) sources.set(source, index);
  const next = [...sources.values()].reduce((bits, key) => bits | (1 << key), 0);
  if (mask === next) return;
  mask = next;
  output?.port.postMessage({ type: "keys", mask });
  updateKeyStyles();
}

function updateKeyStyles() {
  buttons.forEach((button, i) => {
    const held = Boolean(mask & (1 << i));
    button.classList.toggle("held", held);
    button.setAttribute("aria-pressed", String(held));
  });
}

function releaseAll() {
  sources.clear();
  mask = 0;
  output?.port.postMessage({ type: "keys", mask, reset: true });
  updateKeyStyles();
}

for (const [i, [, shortcut, label]] of layout.entries()) {
  const key = document.createElement("button");
  const hint = document.createElement("span");
  key.className = "key";
  key.type = "button";
  key.disabled = true;
  key.textContent = label;
  key.setAttribute("aria-label", i < 7 ? "琴键 " + label : label);
  key.setAttribute("aria-keyshortcuts", shortcut);
  hint.className = "key-hint";
  hint.textContent = shortcut;
  hint.setAttribute("aria-hidden", "true");
  key.append(hint);
  const pointers = new Map();
  let counter = 0;
  key.addEventListener("pointerdown", event => {
    if (event.button !== 0 || !active) return;
    key.setPointerCapture(event.pointerId);
    const source = "pointer-" + i + "-" + ++counter;
    pointers.set(event.pointerId, { source, since: performance.now() });
    updateKey(source, i);
    event.preventDefault();
  });
  for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) {
    key.addEventListener(type, event => {
      const press = pointers.get(event.pointerId);
      if (!press) return;
      pointers.delete(event.pointerId);
      const delay = type === "pointerup" && i < 7 ? 65 - (performance.now() - press.since) : 0;
      if (delay > 0) setTimeout(() => updateKey(press.source, -1), delay);
      else updateKey(press.source, -1);
    });
  }
  key.addEventListener("click", event => {
    if (event.detail || !active) return;
    const source = "activation-" + i + "-" + ++counter;
    updateKey(source, i);
    setTimeout(() => updateKey(source, -1), i === 9 ? 1300 : 90);
  });
  el(i < 7 ? "keys" : "modifiers").append(key);
  buttons.push(key);
}

document.addEventListener("keydown", event => {
  const key = keyboard.get(event.code);
  if (key === undefined || !active || event.altKey || event.ctrlKey || event.metaKey ||
      event.target.closest("input, select, textarea, [contenteditable]")) return;
  event.preventDefault();
  if (!event.repeat) updateKey(event.code, key);
});
document.addEventListener("keyup", event => updateKey(event.code, -1));
window.addEventListener("blur", releaseAll);
document.addEventListener("visibilitychange", () => { if (document.hidden) releaseAll(); });

function paint(buffer, state) {
  const colors = new Uint16Array(buffer);
  const pixels = rgba.data;
  for (let i = 0, offset = 0; i < colors.length; i++, offset += 4) {
    const value = colors[i];
    const r = value >> 11, g = (value >> 5) & 63, b = value & 31;
    pixels[offset] = (r << 3) | (r >> 2);
    pixels[offset + 1] = (g << 2) | (g >> 4);
    pixels[offset + 2] = (b << 3) | (b >> 2);
    pixels[offset + 3] = 255;
  }
  canvas.putImageData(rgba, 0, 0);
  const [menu, home, row, mode, game, , paused, , , volume, instrument, bpm, transpose, flags, saved] = state;
  const description = menu ? menuNames[menu] + (menu === 1 ? " · " + homeNames[home] :
    menu === 2 ? " · " + launchNames[row] : "") :
    (gameNames[game] || modeNames[mode]) + (paused ? " · 已暂停" : "");
  const status = description + " · 音量 " + volume + "%" + (bankInFlight ? " · 正在载入音色…" : "");
  if (el("screen-state").textContent !== status) el("screen-state").textContent = status;
  if (saved !== revision) {
    if (revision !== -1 || saved !== 0) save(settingsKey, { volume, instrument, bpm, transpose, flags });
    revision = saved;
  }
}

function drawFrame(now) {
  if (!active) return;
  if (frameBuffer && now - frameAt >= 42) {
    frameAt = now;
    output.port.postMessage({ type: "frame", buffer: frameBuffer }, [frameBuffer]);
    frameBuffer = null;
  }
  requestAnimationFrame(drawFrame);
}

async function bytes(url) {
  const response = await fetch(url, { signal: AbortSignal.timeout(180000) });
  if (!response.ok) throw new Error("资源载入失败，请重试。");
  return response.arrayBuffer();
}

async function loadBank(id) {
  const bank = await bytes("../banks/" + id + ".hps");
  const header = new DataView(bank);
  const words = [22050, 36, 2, 31, 88200, id, 0];
  if (bank.byteLength !== 5468432 || header.getUint32(0) !== 0x48505331 ||
      !words.every((value, i) => header.getUint32(4 + 4 * i, true) === value)) {
    throw new Error("琴端采样格式不匹配。");
  }
  return bank;
}

function queueBank(instrument) {
  pendingBank = instrument;
  if (bankInFlight) return;
  bankInFlight = true;
  pendingBank = 0;
  loadBank(instrument).then(bank => {
    output.port.postMessage({ type: "bank", instrument, bytes: bank }, [bank]);
  }).catch(() => {
    output?.port.postMessage({ type: "bank-failed" });
    finishBank();
  });
}

function finishBank() {
  bankInFlight = false;
  if (pendingBank) queueBank(pendingBank);
}

function pushSong(song) {
  const data = new TextEncoder().encode(JSON.stringify(song));
  if (data.length > 120 * 1024) throw new Error("曲谱超过固件的 120 KiB 限制。");
  output.port.postMessage({ type: "import", id: song.id, bytes: data.buffer }, [data.buffer]);
}

function hasSong(id) {
  return storedSongs.some(song => song?.id === id);
}

function importSong(song) {
  PianoScore.validateSong(song);
  if (hasSong(song.id) || pendingImports.has(song.id)) throw new Error("这首曲谱已在曲库中或正在导入。");
  return new Promise((resolve, reject) => {
    pushSong(song);
    pendingImports.set(song.id, { song, resolve, reject });
  });
}

function importNotice(song, persisted) {
  return "已导入「" + song.title + "」。长按 / 后在「选择歌曲」中打开。" +
    (persisted ? "" : "浏览器无法保存，刷新后需重新导入。");
}

function receive({ data }) {
  if (data.type === "frame") {
    frameBuffer = data.buffer;
    paint(frameBuffer, data.state);
    if (data.bank) queueBank(data.bank);
    if (data.removed) {
      storedSongs = storedSongs.filter(song => song.id !== data.removed);
      save(songsKey, storedSongs);
      document.dispatchEvent(new Event("piano-library-change"));
    }
  } else if (data.type === "bank") {
    if (!active && data.ok) {
      active = true;
      el("boot").hidden = true;
      el("import").disabled = false;
      buttons.forEach(button => { button.disabled = false; });

      for (const song of storedSongs.slice(0, 62)) {
        try { pushSong(song); } catch { /* Skip invalid browser data. */ }
      }
      requestAnimationFrame(drawFrame);
      startup?.resolve();
      startup = null;
    } else if (!active) fail("音色无法启动，请重试。");
    finishBank();
  } else if (data.type === "import") {
    const pending = pendingImports.get(data.id);
    if (!pending) return;
    pendingImports.delete(data.id);
    if (data.ok) {
      storedSongs.push(pending.song);
      pending.resolve(save(songsKey, storedSongs));
      document.dispatchEvent(new Event("piano-library-change"));
    } else {
      pending.reject(new Error(data.error === "song library full" ?
        "曲库已满，请先删除曲目。" : "固件无法读取这份曲谱，请检查格式。"));
    }
  }
}

function fail(message) {
  const error = new Error(message);
  startup?.reject(error);
  startup = null;
  for (const { reject } of pendingImports.values()) reject(error);
  pendingImports.clear();
  releaseAll();
  active = false;
  buttons.forEach(button => { button.disabled = true; });
  el("import").disabled = true;
  el("screen-state").textContent = "未开机";
  el("power").disabled = false;
  el("boot").hidden = false;
  el("boot-status").textContent = message;
  output?.disconnect();
  output = null;
  context?.close().catch(() => {});
  context = null;
  frameBuffer = new ArrayBuffer(width * height * 2);
  bankInFlight = false;
  pendingBank = 0;
  revision = -1;
}

function powerOn() {
  if (active) return Promise.resolve();
  if (startup) return startup.promise;
  startup = {};
  startup.promise = new Promise((resolve, reject) => Object.assign(startup, { resolve, reject }));
  const promise = startup.promise;
  boot();
  return promise;
}

async function boot() {
  el("power").disabled = true;
  el("boot-status").textContent = "正在载入琴端音色…";
  try {
    if (!window.isSecureContext || !window.AudioWorkletNode || !window.AudioContext) {
      throw new Error("请在支持网页音频的浏览器中打开 HTTPS 页面。");
    }
    context = new AudioContext({ sampleRate: 22050, latencyHint: "interactive" });
    if (context.sampleRate !== 22050) throw new Error("此浏览器不支持 22.05 kHz 音频。");
    const saved = getStored(settingsKey, {});
    const settings = {
      bpm: Number.isInteger(saved.bpm) ? saved.bpm : 120,
      transpose: Number.isInteger(saved.transpose) ? saved.transpose : 0,
      volume: Number.isInteger(saved.volume) ? saved.volume : 60,
      flags: Number.isInteger(saved.flags) ? saved.flags : 16
    };
    const instrument = Number.isInteger(saved.instrument) && saved.instrument >= 3 && saved.instrument <= 11 ? saved.instrument : 11;
    const [module, bank] = await Promise.all([
      bytes("piano.wasm?v=1").then(WebAssembly.compile), loadBank(instrument),
      context.audioWorklet.addModule("worklet.js?v=1"), context.resume()
    ]);
    output = new AudioWorkletNode(context, "full-piano", {
      processorOptions: { module, settings }, numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2]
    });
    output.port.onmessage = receive;
    output.onprocessorerror = () => fail("音频中断，请重新开机。");
    output.connect(context.destination);
    output.port.postMessage({ type: "bank", instrument, bytes: bank }, [bank]);
  } catch (error) { fail(error.message || "声音无法启动，请重试。"); }
}

el("power").onclick = () => powerOn().catch(() => {});

function scoreFileParts(name) {
  const stem = name.replace(/\.(?:txt|json|ccmz)$/i, "");
  const bpmTag = /[_\s]*(?:源谱\s*)?BPM\s*[:：]\s*(\d+)(?:\s+\(\d+\))?$/i.exec(stem);
  return {
    title: (bpmTag ? stem.slice(0, bpmTag.index) : stem).trim() || "自定义曲谱",
    bpm: bpmTag ? Number(bpmTag[1]) : null
  };
}

function scoreFileSizeLimit(name) {
  return /\.ccmz$/i.test(name) ? 8 * 1024 * 1024 : 120 * 1024;
}

async function convertCcmz(file, title, id) {
  const bytes = await file.arrayBuffer();
  const worker = new Worker("ccmz-worker.js", { type: "module" });
  try {
    return await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("CCMZ 转换超时，请重试。")), 60000);
      const finish = (callback, value) => {
        clearTimeout(timeout);
        callback(value);
      };
      worker.onmessage = ({ data }) => {
        if (data?.song) finish(resolve, data.song);
        else finish(reject, new Error(data?.error || "CCMZ 转换失败。"));
      };
      worker.onerror = () => finish(reject, new Error("CCMZ 转换器无法启动，请刷新后重试。"));
      worker.onmessageerror = () => finish(reject, new Error("无法读取 CCMZ 转换结果。"));
      worker.postMessage({ bytes, title, id }, [bytes]);
    });
  } finally {
    worker.terminate();
  }
}

async function readScoreFile(file, id = "web-" + crypto.randomUUID()) {
  if (/\.ccmz$/i.test(file.name)) {
    if (file.size > scoreFileSizeLimit(file.name)) throw new Error("CCMZ 文件超过 8 MiB。");
    return convertCcmz(file, scoreFileParts(file.name).title, id);
  }
  if (/\.pdf$/i.test(file.name)) throw new Error("PDF 是谱面文件，请选用 CCMZ、TXT 简谱或琴端 JSON。");
  if (!/\.(?:txt|json)$/i.test(file.name)) throw new Error("请选择 CCMZ、TXT 或琴端 JSON 曲谱。");
  if (file.size > scoreFileSizeLimit(file.name)) throw new Error("曲谱超过 120 KiB。");
  const text = await file.text();
  if (/\.json$/i.test(file.name)) {
    const song = JSON.parse(text.replace(/^\uFEFF/, ""));
    if (!song || typeof song !== "object" || Array.isArray(song)) throw new Error("请选择琴端歌曲 JSON。");
    return { ...song, id };
  }
  const { title, bpm: fileBpm } = scoreFileParts(file.name);
  const bpm = fileBpm ?? Number(el("bpm").value);
  if (!Number.isInteger(bpm) || bpm < 20 || bpm > 400) throw new Error("BPM 必须在 20 至 400 之间。");
  return { id, bpm, title, notes: PianoScore.parseScore(text) };
}

el("import-form").onsubmit = async event => {
  event.preventDefault();
  const file = el("file").files[0];
  if (!file || !active) return;
  el("import").disabled = true;
  el("file-status").textContent = /\.ccmz$/i.test(file.name) ? "正在转换 CCMZ 曲谱…" : "正在导入曲谱…";
  try {
    const song = await readScoreFile(file);
    el("file-status").textContent = importNotice(song, await importSong(song));
  } catch (error) {
    el("file-status").textContent = error.message || "曲谱导入失败。";
  } finally {
    el("import").disabled = !active;
  }
};
