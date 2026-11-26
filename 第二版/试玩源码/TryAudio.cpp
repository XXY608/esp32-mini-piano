#include <utility>
#include "Board2_1Config.h"
#include "ManualMixer.h"
#include "PerformanceMixer.h"

namespace {
constexpr unsigned frames = 128;
constexpr int melody[] = {1, 2, 3, 4, 5, 6, 7, 6, 5, 4, 3, 2, 1};
piano::Engine engine;
piano::ManualMixer manual(board::kSampleRate, board::kPeakAmplitude);
piano::PerformanceMixer score(board::kSampleRate, board::kPeakAmplitude);
piano::SampleBank bank;
alignas(int16_t) uint8_t bankBytes[piano::SampleBank::kBytes];
int16_t samples[frames * 2];
uint64_t rendered = 0;
uint32_t now() { return static_cast<uint32_t>(rendered * 1000 / board::kSampleRate); }
}

#define EXPORTED(name) extern "C" __attribute__((export_name(#name)))
EXPORTED(bank_buffer) uint8_t* bank_buffer() { return bankBytes; }
EXPORTED(bank_capacity) unsigned bank_capacity() { return sizeof(bankBytes); }
EXPORTED(bind_bank) int bind_bank(int id, unsigned size) {
  if (id < 3 || id > piano::kMaxInstrument || !bank.bind(bankBytes, size, id)) return 0;
  auto settings = engine.view().settings;
  settings.instrument = id;
  engine.settings(settings, now());
  manual.reset();
  score = piano::PerformanceMixer(board::kSampleRate, board::kPeakAmplitude);
  return 1;
}
EXPORTED(set_volume) void set_volume(int value) { engine.setVolume(value); }
EXPORTED(keys) void keys(unsigned mask) { engine.keys(mask & 0x3ff, now()); }
EXPORTED(set_mode) void set_mode(int practice) {
  engine.keys(0, now());
  engine.stop();
  manual.reset();
  if (!practice) return;
  piano::Song song;
  song.id = "try-scale";
  song.title = "音阶练习";
  song.bpm = 120;
  song.bpmProvided = true;
  for (auto degree : melody) {
    piano::Note note(piano::noteMidi(degree, 0, false), .5);
    note.pitch = degree;
    song.notes.push_back(note);
  }
  engine.start(std::move(song), piano::Mode::Simple, now());
}
EXPORTED(progress) unsigned progress() { return engine.view().index; }
EXPORTED(finished) int finished() { return engine.view().finished; }
EXPORTED(render) int16_t* render() {
  engine.tick(now());
  const auto view = engine.view(false);
  if (view.mode == piano::Mode::Manual) {
    manual.render(view, bank, samples, frames);
  } else {
    score.render(piano::melodyAudio(view), bank, view.settings.instrument,
                 view.volume, view.click, samples, frames);
  }
  rendered += frames;
  return samples;
}
