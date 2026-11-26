class PianoOutput extends AudioWorkletProcessor {
  constructor({ processorOptions: { module } }) {
    super();
    this.piano = new WebAssembly.Instance(module, {
      wasi_snapshot_preview1: { fd_close: () => 0, fd_seek: () => 0, fd_write: () => 0 }
    }).exports;
    this.piano._initialize();
    this.offset = 128;
    this.ready = false;
    this.progress = -1;
    this.finished = false;
    this.port.onmessage = ({ data }) => {
      const p = this.piano;
      if (data.type === "bank") {
        const bytes = new Uint8Array(data.bytes);
        const bound = bytes.length === p.bank_capacity() && (() => {
          new Uint8Array(p.memory.buffer, p.bank_buffer(), bytes.length).set(bytes);
          return p.bind_bank(data.id, bytes.length);
        })();
        this.ready = Boolean(bound);
        this.offset = 128;
        this.port.postMessage({ type: "bank", ok: this.ready });
      } else if (data.type === "keys") {
        p.keys(data.mask);
      } else if (data.type === "volume") {
        p.set_volume(data.value);
      } else if (data.type === "mode") {
        p.set_mode(Number(data.practice));
        this.progress = -1;
        this.finished = false;
        this.offset = 128;
      }
    };
  }

  process(_inputs, [channels]) {
    const [left, right] = channels;
    if (!left || !this.ready) return true;
    for (let i = 0; i < left.length; i++) {
      if (this.offset === 128) {
        const p = this.piano;
        const address = p.render();
        this.samples = new Int16Array(p.memory.buffer, address, 256);
        this.offset = 0;
        const progress = p.progress();
        const finished = Boolean(p.finished());
        if (this.progress !== progress || this.finished !== finished) {
          this.progress = progress;
          this.finished = finished;
          this.port.postMessage({ type: "progress", progress, finished });
        }
      }
      left[i] = this.samples[2 * this.offset] / 32768;
      if (right) right[i] = this.samples[2 * this.offset + 1] / 32768;
      this.offset++;
    }
    return true;
  }
}
registerProcessor("firmware-piano", PianoOutput);
