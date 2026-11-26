class FullPianoOutput extends AudioWorkletProcessor {
  constructor({ processorOptions: { module, settings } }) {
    super();
    this.piano = new WebAssembly.Instance(module, {
      wasi_snapshot_preview1: { fd_close: () => 0, fd_seek: () => 0, fd_write: () => 0 }
    }).exports;
    this.piano._initialize();
    this.piano.boot();
    if (settings) this.piano.restore(settings.bpm, settings.transpose, settings.volume, settings.flags);
    this.offset = 128;
    this.pendingFrame = null;
    this.ready = false;
    this.keys = [];
    this.port.onmessage = ({ data }) => this.receive(data);
  }

  string(address) {
    const bytes = new Uint8Array(this.piano.memory.buffer);
    return String.fromCharCode(...bytes.subarray(address, bytes.indexOf(0, address)));
  }

  receive(data) {
    const p = this.piano;
    if (data.type === "bank") {
      const bytes = new Uint8Array(data.bytes);
      const ok = bytes.length === p.bank_capacity() && (() => {
        new Uint8Array(p.memory.buffer, p.bank_buffer(), bytes.length).set(bytes);
        return Boolean(p.bind_bank(data.instrument, bytes.length));
      })();
      if (!ok) p.bank_failed();
      if (ok) {
        this.ready = true;
        this.offset = 128;
      }
      this.port.postMessage({ type: "bank", ok, instrument: data.instrument });
    } else if (data.type === "bank-failed") {
      p.bank_failed();
    } else if (data.type === "keys") {
      if (data.reset) this.keys.length = 0;
      this.keys.push(data.mask);
    } else if (data.type === "frame") {
      this.pendingFrame = data.buffer;
    } else if (data.type === "import") {
      const bytes = new Uint8Array(data.bytes);
      let ok = false;
      if (bytes.length <= 120 * 1024) {
        new Uint8Array(p.memory.buffer, p.import_buffer(), bytes.length).set(bytes);
        ok = Boolean(p.import_song(bytes.length));
      }
      this.port.postMessage({ type: "import", id: data.id, ok, error: ok ? "" : this.string(p.import_error()) });
    }
  }

  sendFrame() {
    const p = this.piano;
    const buffer = this.pendingFrame;
    this.pendingFrame = null;
    const pixels = p.frame();
    new Uint16Array(buffer).set(new Uint16Array(p.memory.buffer, pixels, 320 * 240));
    const state = Array.from(new Int32Array(p.memory.buffer, p.status(), 24));
    const removed = this.string(p.deletion());
    if (removed) p.clear_deletion();
    this.port.postMessage({ type: "frame", buffer, state, removed, bank: p.take_bank_request() }, [buffer]);
  }

  process(_inputs, [channels]) {
    const [left, right] = channels;
    if (!left) return true;
    if (this.ready) {
      for (let i = 0; i < left.length; i++) {
        if (this.offset === 128) {
          const p = this.piano;
          if (this.keys.length) p.keys(this.keys.shift());
          const address = p.render();
          this.samples = new Int16Array(p.memory.buffer, address, 256);
          this.offset = 0;
        }
        left[i] = this.samples[2 * this.offset] / 32768;
        if (right) right[i] = this.samples[2 * this.offset + 1] / 32768;
        this.offset++;
      }
    }
    if (this.pendingFrame) this.sendFrame();
    return true;
  }
}
registerProcessor("full-piano", FullPianoOutput);
