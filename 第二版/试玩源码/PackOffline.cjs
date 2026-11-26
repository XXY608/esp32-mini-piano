const fs = require("node:fs");
const path = require("node:path");
const site = path.resolve(process.argv[2]);
const read = name => fs.readFileSync(path.join(site, name), "utf8");
const b64 = name => fs.readFileSync(path.join(site, name)).toString("base64");
const bundle = { id: 11, wasm: b64("audio.wasm"), bank: b64("banks/11.hps"), worklet: read("audio-worklet.js") };
const html = read("index.html")
  .replace('<link rel="stylesheet" href="style.css">', () => '<style>' + read("style.css") + '</style>')
  .replace('<script src="try.js" defer></script>', '')
  .replace('</body>', () => '<script>window.pianoOffline=' + JSON.stringify(bundle).replace(/</g, '\\u003c') + ';\n' + read("try.js") + '</script>\n</body>');
fs.writeFileSync(process.argv[3], html);
