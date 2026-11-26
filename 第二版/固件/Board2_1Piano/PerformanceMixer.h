#pragma once
#include "PolyMix.h"

namespace piano {
inline bool usesLocalScoreAudio(const View& view, const PerformanceView& performance) {
  return performance.enabled || (view.settings.instrument>=3 && (view.mode==Mode::Play || view.mode==Mode::Simple));
}

inline PerformanceView melodyAudio(const View& view) {
  PerformanceView audio;
  audio.epoch=view.songGeneration^0x20000000u;
  audio.paused=view.paused;
  auto& voice=audio.voices[0];
  voice.midi=view.paused?-1:view.midi;
  voice.trigger=view.trigger;
  voice.velocity=view.velocity;
  return audio;
}

class PerformanceMixer {
  struct Voice {
    AudioSynth synth{1, 0};
    SampleVoice sample;
    unsigned release = 0;
    PerformanceVoice note;
    bool keyDown = false, sustained = false;
  };
 public:
  PerformanceMixer(unsigned rate, int peak) : click_(rate, peak), rate_(rate), peak_(peak) {}

  void render(const PerformanceView& view, const SampleBank& bank, int instrument, int volume,
              bool click, int16_t* out, size_t frames) {
    if (epoch_ != view.epoch || instrument_ != instrument) {
      for (auto& voice : voices_) {
        voice.synth = AudioSynth(rate_, peak_);
        voice.sample.reset();
        voice.release = 0;
        voice.note = PerformanceVoice{};
        voice.keyDown = voice.sustained = false;
      }
      epoch_ = view.epoch;
      instrument_ = instrument;
    }
    click_.render(-1, volume, out, frames, 0, click);
    mix_.begin(out, frames);
    assignVoices(view);
    for (auto& voice : voices_) {
      const auto& note = voice.note;
      const bool sounding = voice.keyDown || voice.sustained;
      if (sounding) voice.release = rate_ / 5;
      else if (voice.release) voice.release -= std::min(voice.release, static_cast<unsigned>(frames));
      else continue;
      const int midi = sounding ? note.midi : -1;
      const int level = sounding ? std::min(100, volume * note.velocity / 100) : volume;
      if (instrument >= 3) {
        mix_.addSample(voice.sample, bank, midi, level, peak_, click, note.trigger, true, frames);
      } else {
        mix_.addSynth(voice.synth, midi, level, instrument, note.trigger, frames);
      }
    }
    mix_.finish(out, frames, peak_);
  }

 private:
  void assignVoices(const PerformanceView& view) {
    std::array<bool, kPerformanceVoices> assigned{};
    for (auto& voice : voices_) {
      bool wasHeld = voice.keyDown || voice.sustained;
      voice.keyDown = false;
      for (size_t i = 0; i < view.voices.size(); ++i) {
        const auto& note = view.voices[i];
        if (note.midi >= 0 && note.trigger == voice.note.trigger) {
          voice.note = note;
          assigned[i] = voice.keyDown = true;
          break;
        }
      }
      voice.sustained = !voice.keyDown && wasHeld && view.pedal && !view.paused;
    }
    for (size_t i = 0; i < view.voices.size(); ++i) {
      const auto& note = view.voices[i];
      if (assigned[i] || note.midi < 0) continue;
      Voice* destination = nullptr;
      for (auto& voice : voices_) {
        if (voice.keyDown) continue;
        if (voice.sustained && voice.note.midi == note.midi) { destination = &voice; break; }
        if (!destination || (destination->sustained && !voice.sustained) ||
            (destination->sustained == voice.sustained && voice.note.trigger < destination->note.trigger)) destination = &voice;
      }
      if (destination) {
        destination->note = note;
        destination->keyDown = true;
        destination->sustained = false;
      }
    }
  }
  std::array<Voice, kPerformanceVoices> voices_;
  AudioSynth click_;
  PolyMix mix_;
  unsigned rate_;
  int peak_, instrument_ = -1;
  uint32_t epoch_ = 0;
};
}
