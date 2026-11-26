"use strict";

const byId = id => document.getElementById(id);
const melody = [1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3, 2, 1];
const keyboard = ["Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "KeyJ", "KeyK", "KeyL"];
const buttons = [];
const notes = [];
const inputs = new Map();
const offline = window.pianoOffline;
const localBanks = new Map();
let context, output, modulePromise, outputPromise, bankReply;
let selected = 0, mask = 0, progress = 0;
let busy = false, active = false, practicing = true, finished = false;

if (offline) byId("local-bank-label").hidden = false;

for (const degree of melody) {
  const note = document.createElement("span");
  note.textContent = degree;
  notes.push(note);
  byId("score").append(note);
}

function draw() {
  const target = practicing && !finished ? melody[progress] : 0;
  byId("progress").textContent = practicing ? progress + " / " + melody.length : " ";
  buttons.forEach((button, i) => {
    button.classList.toggle("target", i + 1 === target);
    button.classList.toggle("held", Boolean(mask & (1 << i)));
    button.setAttribute("aria-pressed", String(Boolean(mask & (1 << i))));
  });
  notes.forEach((note, i) => {
    note.classList.toggle("target", practicing && i === progress && !finished);
    note.classList.toggle("played", practicing && i < progress);
  });
}

function setInput(source, index) {
  if (index < 0) inputs.delete(source);
  else if (active && !busy && (index < 7 || !practicing)) inputs.set(source, index);
  else return;
  const next = [...inputs.values()].reduce((value, key) => value | (1 << key), 0);
  if (mask !== next) {
    mask = next;
    output?.port.postMessage({ type: "keys", mask });
    draw();
  }
}

function releaseAll() {
  inputs.clear();
  setInput("", -1);
}

for (const [i, label] of ["1", "2", "3", "4", "5", "6", "7", "降八度", "升半音", "升八度"].entries()) {
  const key = document.createElement("button");
  key.className = "key";
  key.type = "button";
  key.textContent = label;
  key.setAttribute("aria-label", i < 7 ? "琴键 " + label : label);
  key.setAttribute("aria-keyshortcuts", i < 7 ? label : ["J", "K", "L"][i - 7]);
  key.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    key.setPointerCapture(event.pointerId);
    setInput("pointer-" + event.pointerId, i);
    event.preventDefault();
  });
  for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) {
    key.addEventListener(type, event => setInput("pointer-" + event.pointerId, -1));
  }
  let taps = 0;
  key.addEventListener("click", event => {
    if (event.detail) return;
    const id = "tap-" + i + "-" + ++taps;
    setInput(id, i);
    setTimeout(() => setInput(id, -1), 160);
  });
  byId(i < 7 ? "keys" : "modifiers").append(key);
  buttons.push(key);
}

document.addEventListener("keydown", event => {
  const i = keyboard.indexOf(event.code);
  if (i < 0 || !active || event.altKey || event.ctrlKey || event.metaKey ||
      event.target.closest("input,select,textarea,[contenteditable]")) return;
  event.preventDefault();
  if (!event.repeat) setInput(event.code, i);
});
document.addEventListener("keyup", event => setInput(event.code, -1));
window.addEventListener("blur", releaseAll);
document.addEventListener("visibilitychange", () => { if (document.hidden) releaseAll(); });

function setBusy(value) {
  busy = value;
  for (const id of ["practice", "free", "instrument", "local-bank"]) byId(id).disabled = value;
}

function fromBase64(encoded) {
  return Uint8Array.from(atob(encoded), char => char.charCodeAt(0));
}

async function loadBytes(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error("资源读取失败，请重试。");
  return response.arrayBuffer();
}

async function prepareOutput() {
  if (!context) {
    if (!window.AudioContext || !window.AudioWorkletNode || !window.isSecureContext) {
      throw new Error("请用支持网页音频的浏览器打开 HTTPS 试玩页。");
    }
    context = new AudioContext({ sampleRate: 22050, latencyHint: "interactive" });
    if (context.sampleRate !== 22050) {
      await context.close();
      context = null;
      throw new Error("此浏览器不支持 22.05 kHz 音频。请换用 Chrome、Edge 或 Firefox 试试。");
    }
  }
  const resume = context.resume();
  if (!outputPromise) outputPromise = (async () => {
    modulePromise ??= WebAssembly.compile(await (offline ? fromBase64(offline.wasm) : loadBytes("audio.wasm")));
    const url = offline
      ? "data:text/javascript;charset=utf-8," + encodeURIComponent(offline.worklet)
      : "audio-worklet.js";
    const [module] = await Promise.all([modulePromise, context.audioWorklet.addModule(url)]);
    output = new AudioWorkletNode(context, "firmware-piano", {
      numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2], processorOptions: { module }
    });
    output.port.onmessage = ({ data }) => {
      if (data.type === "bank") {
        bankReply?.(data.ok);
      } else if (practicing && data.type === "progress") {
        progress = data.progress;
        finished = data.finished;
        if (finished) {
          byId("status").textContent = "音阶弹完了！可以再来一遍，或切到自由弹奏。";
          byId("practice").textContent = "再弹一遍";
        } else if (active) {
          byId("status").textContent = progress < melody.length ? "下一个键：" + melody[progress] : "最后一个音正在播放。";
        }
        draw();
      }
    };
    output.addEventListener("processorerror", () => {
      releaseAll();
      active = false;
      bankReply?.(false);
      output.disconnect();
      output = null;
      outputPromise = null;
      selected = 0;
      byId("status").textContent = "音频处理已停止，点击开始重试。";
    });
    output.connect(context.destination);
  })().catch(error => { outputPromise = null; modulePromise = null; throw error; });
  await Promise.all([resume, outputPromise]);
}

async function bindInstrument(id) {
  if (selected === id) return;
  let bytes;
  if (localBanks.has(id)) bytes = await localBanks.get(id).arrayBuffer();
  else if (!offline) bytes = await loadBytes("banks/" + id + ".hps");
  else if (id === offline.id) bytes = fromBase64(offline.bank).buffer;
  else throw new Error("离线版请先选择 SD 卡上对应的 .hps 文件。");
  const header = new DataView(bytes);
  if (bytes.byteLength !== 5468432 || header.getUint32(0) !== 0x48505331 ||
      header.getUint32(4, true) !== 22050 || header.getUint32(8, true) !== 36 ||
      header.getUint32(12, true) !== 2 || header.getUint32(16, true) !== 31 ||
      header.getUint32(20, true) !== 88200 || header.getUint32(24, true) !== id ||
      header.getUint32(28, true) !== 0) throw new Error("音色文件格式与所选音色不符。");
  const success = await new Promise(resolve => {
    const timer = setTimeout(() => settle(false), 5000);
    const settle = ok => { clearTimeout(timer); bankReply = null; resolve(ok); };
    bankReply = settle;
    output.port.postMessage({ type: "bank", id, bytes }, [bytes]);
  });
  if (!success) {
    selected = 0;
    throw new Error("音色载入失败，请重试。");
  }
  selected = id;
}

function sendVolume() {
  const value = Number(byId("volume").value);
  byId("volume-value").value = value + "%";
  output?.port.postMessage({ type: "volume", value });
}

async function start(practice) {
  if (busy) return;
  releaseAll();
  setBusy(true);
  byId("status").textContent = "正在载入琴端音色…";
  try {
    await prepareOutput();
    await bindInstrument(Number(byId("instrument").value));
    sendVolume();
    practicing = practice;
    progress = 0;
    finished = false;
    output.port.postMessage({ type: "mode", practice });
    active = true;
    byId("mode-label").textContent = practice ? "跟弹练习" : "自由弹奏";
    byId("practice").textContent = practice ? "重新跟弹" : "开始跟弹";
    byId("status").textContent = practice ? "先弹亮起的 1。" : "可以用键盘、鼠标或触屏弹奏。";
    draw();
  } catch (error) {
    active = false;
    byId("status").textContent = error.message || "音频无法启动。";
  } finally {
    setBusy(false);
  }
}

byId("practice").onclick = () => start(true);
byId("free").onclick = () => start(false);
byId("volume").oninput = sendVolume;
byId("instrument").onchange = async () => {
  if (!active || busy) return;
  releaseAll();
  setBusy(true);
  byId("status").textContent = "正在切换音色…";
  try {
    await bindInstrument(Number(byId("instrument").value));
    byId("status").textContent = "已切换到 " + byId("instrument").selectedOptions[0].textContent + "。";
  } catch (error) {
    if (selected) byId("instrument").value = selected;
    byId("status").textContent = error.message;
  } finally {
    setBusy(false);
  }
};
byId("local-bank").onchange = async event => {
  const file = event.target.files[0];
  if (!file) return;
  const head = new DataView(await file.slice(0, 32).arrayBuffer());
  const id = head.byteLength >= 32 && head.getUint32(0) === 0x48505331 ? head.getUint32(24, true) : 0;
  if (id < 3 || id > 11 || file.size !== 5468432) {
    byId("status").textContent = "请选择第 3–11 号琴端 HPS 采样。";
    return;
  }
  localBanks.set(id, file);
  selected = 0; // Reload even if the new file has the same bank ID.
  byId("instrument").value = id;
  await start(practicing);
};
draw();
