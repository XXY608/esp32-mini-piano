const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { parseScore, validateSong } = require("../固件/Board2_1Piano/web/score-parser.js");

const examples = [
  {
    id: "twinkle", title: "小星星", author: "法国传统旋律", bpm: 96, meter: "4/4",
    aliases: ["一闪一闪亮晶晶", "Twinkle Twinkle Little Star", "Ah vous dirai-je maman"],
    melody: [
      "1 1 5 5", "6 6 5 —", "4 4 3 3", "2 2 1 —",
      "5 5 4 4", "3 3 2 —", "5 5 4 4", "3 3 2 —",
      "1 1 5 5", "6 6 5 —", "4 4 3 3", "2 2 1 —"
    ],
    harmony: ["CC","FC","FC","GC","CG","CG","CG","CG","CC","FC","FC","GC"]
  },
  {
    id: "ode-to-joy", title: "欢乐颂", author: "贝多芬", bpm: 100, meter: "4/4",
    aliases: ["Ode to Joy", "Beethoven", "第九交响曲"],
    melody: [
      "3 3 4 5", "5 4 3 2", "1 1 2 3", "3. 2_ 2 —",
      "3 3 4 5", "5 4 3 2", "1 1 2 3", "2. 1_ 1 —",
      "2 2 3 1", "2 3_ 4_ 3 1", "2 3_ 4_ 3 2", "1 2 （5） —",
      "3 3 4 5", "5 4 3 2", "1 1 2 3", "2. 1_ 1 —"
    ],
    harmony: ["CC","CG","CC","CG","CC","CG","CC","GC","GC","GC","CG","CG","CC","CG","CC","GC"]
  },
  {
    id: "frere-jacques", title: "两只老虎", author: "法国传统旋律", bpm: 96, meter: "4/4",
    aliases: ["Frère Jacques", "Frere Jacques", "Are You Sleeping"],
    melody: [
      "1 2 3 1", "1 2 3 1", "3 4 5 —", "3 4 5 —",
      "5_ 6_ 5_ 4_ 3 1", "5_ 6_ 5_ 4_ 3 1", "1 （5） 1 —", "1 （5） 1 —"
    ]
  },
  {
    id: "mary-lamb", title: "玛丽有只小羊羔", author: "传统童谣旋律", bpm: 90, meter: "4/4",
    aliases: ["Mary Had a Little Lamb", "小羊羔", "玛丽的小羊"],
    melody: ["3 2 1 2", "3 3 3 —", "2 2 2 —", "3 5 5 —",
      "3 2 1 2", "3 3 3 3", "2 2 3 2", "1 — — —"]
  },
  {
    id: "jingle-bells", title: "铃儿响叮当（副歌）", author: "詹姆斯·皮尔庞特", bpm: 108, meter: "4/4",
    aliases: ["Jingle Bells", "James Lord Pierpont", "雪橇歌"],
    melody: [
      "3 3 3 —", "3 3 3 —", "3 5 1. 2_", "3 — — —",
      "4 4 4. 4_", "4 3 3 3_ 3_", "3 2 2 3", "2 — 5 —",
      "3 3 3 —", "3 3 3 —", "3 5 1. 2_", "3 — — —",
      "4 4 4. 4_", "4 3 3 3_ 3_", "5 5 4 2", "1 — — —"
    ]
  },
  {
    id: "row-your-boat", title: "划船歌", author: "英语传统童谣旋律", bpm: 96, meter: "6/8",
    aliases: ["Row Row Row Your Boat", "划呀划小船"],
    melody: ["1. 1.", "1 2_ 3.", "3 2_ 3 4_", "5 — —", "【1_】 【1_】 【1_】 5_ 5_ 5_",
      "3_ 3_ 3_ 1_ 1_ 1_", "5 4_ 3 2_", "1 — —"]
  }
];

const ticks = 960;
const pitches = [0, 0, 2, 4, 5, 7, 9, 11];
const chords = { C: [48, 52, 55], F: [41, 48, 53], G: [43, 47, 55] };

function makePolyphony(notes, harmony) {
  const events = [];
  let beat = 0;
  for (const note of notes) {
    if (note.pitch) events.push([beat, beat + note.durationBeats, 60 + pitches[note.pitch] + 12 * note.octave + Number(note.sharp), 96]);
    beat += note.durationBeats;
  }
  Array.from(harmony.join("")).forEach((chord, i) => {
    for (const [voice, midi] of chords[chord].entries()) {
      events.push([2 * i, 2 * i + 2, midi, voice ? 63 : 71]);
    }
  });
  events.sort((a, b) => a[0] - b[0] || a[2] - b[2]);
  const bytes = Buffer.alloc(events.length * 10);
  for (const [i, [from, to, midi, velocity]] of events.entries()) {
    bytes.writeUInt32LE(Math.round(from * ticks), 10 * i);
    bytes.writeUInt32LE(Math.round(to * ticks), 10 * i + 4);
    bytes.writeUInt8(midi, 10 * i + 8);
    bytes.writeUInt8(velocity, 10 * i + 9);
  }
  return bytes.toString("base64");
}

const dir = path.join(__dirname, "../../docs/try/full/catalog");
fs.mkdirSync(dir, { recursive: true });
const index = [];
for (const { id, title, author, aliases, bpm, meter, melody, harmony } of examples) {
  const beats = meter === "6/8" ? 3 : 4;
  for (const measure of melody) {
    assert.equal(parseScore(measure).reduce((total, note) => total + note.durationBeats, 0), beats, title);
  }
  const notes = parseScore(melody.join(" |\n"));
  const song = { id: "online-" + id, title, bpm, timeSignature: meter, notes };
  if (harmony) {
    assert.equal(harmony.length, melody.length);
    song.polyphony = makePolyphony(notes, harmony);
  }
  validateSong(song);
  fs.writeFileSync(path.join(dir, id + ".json"), JSON.stringify(song) + "\n");
  index.push({ id, title, author, aliases, bpm, polyphony: Boolean(song.polyphony) });
}
fs.writeFileSync(path.join(dir, "index.json"), JSON.stringify(index, null, 2) + "\n");
console.log("Generated", index.length, "online scores");
