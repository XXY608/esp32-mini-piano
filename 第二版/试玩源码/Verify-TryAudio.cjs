const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");

const site = path.resolve(__dirname, "../../docs/try");
const wasm = new WebAssembly.Module(fs.readFileSync(path.join(site, "audio.wasm")));
const assets = path.join(site, "banks");
const manifest = JSON.parse(fs.readFileSync(path.join(assets, "manifest.json"), "utf8").replace(/^\uFEFF/, ""));
const fixture = path.join(os.tmpdir(), "piano-reference-" + crypto.randomUUID() + ".pcm");
const keyEvents = new Map([
  [0, 1], [36, 0x15], [72, 1], [96, 0x201], [133, 0x181], [167, 0], [205, 2],
  [1205, 0], [1260, 1], [1270, 0], [1275, 2], [1280, 0], [1285, 4], [1290, 0]
]);

try {
  assert.equal(manifest.files.length, 9);
  for (const { instrument: id, file, sha256, bytes } of manifest.files) {
    const bankFile = path.join(assets, file);
    const bank = fs.readFileSync(bankFile);
    assert.equal(bank.length, bytes);
    assert.equal(crypto.createHash("sha256").update(bank).digest("hex"), sha256);
    execFileSync(process.argv[2], [String(id), bankFile, fixture]);
    const native = fs.readFileSync(fixture);
    const p = new WebAssembly.Instance(wasm, {
      wasi_snapshot_preview1: { fd_close: () => 0, fd_seek: () => 0, fd_write: () => 0 }
    }).exports;
    p._initialize();
    assert.equal(bank.length, p.bank_capacity());
    new Uint8Array(p.memory.buffer, p.bank_buffer(), bank.length).set(bank);
    assert.equal(p.bind_bank(id, bank.length), 1);
    p.set_volume(60);
    assert.equal(native.length, 1660 * 512);
    let difference = 0;
    let nonzero = 0;
    for (let block = 0; block < 1660; block++) {
      if (block === 1260) p.set_mode(1);
      if (keyEvents.has(block)) p.keys(keyEvents.get(block));
      const address = p.render();
      const samples = new Int16Array(p.memory.buffer, address, 256);
      for (let i = 0; i < samples.length; i++) {
        difference = Math.max(difference, Math.abs(samples[i] - native.readInt16LE((block * 256 + i) * 2)));
        if (samples[i]) nonzero++;
      }
    }
    assert.equal(p.progress(), 3);
    assert(nonzero > 10000, `bank ${id} unexpectedly silent`);
    assert(difference <= 2, `bank ${id}: PCM difference ${difference}`);
    console.log(`bank ${id}: ${1660 * 128} frames, max delta ${difference}/32768`);
  }
} finally {
  fs.rmSync(fixture, { force: true });
}
