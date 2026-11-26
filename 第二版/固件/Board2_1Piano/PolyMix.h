#pragma once
#include "AudioSynth.h"
#include "SampleVoice.h"

namespace piano {
class PolyMix {
 public:
  static constexpr size_t blockFrames = 128;

  void begin(const int16_t* stereo, size_t frames) {
    for (size_t i = 0; i < frames; ++i) sum_[i] = stereo[2 * i];
  }

  void addSynth(AudioSynth& synth, int midi, int volume, int instrument, uint32_t trigger, size_t frames) {
    int16_t stereo[blockFrames * 2];
    synth.render(midi, volume, stereo, frames, instrument, false, trigger);
    for (size_t i = 0; i < frames; ++i) sum_[i] += stereo[2 * i];
  }

  void addSample(SampleVoice& voice, const SampleBank& bank, int midi, int volume, int peak,
                 bool click, uint32_t trigger, bool extendedRange, size_t frames) {
    voice.render(bank, midi, volume, frames, peak, click, trigger, extendedRange,
                 [&](size_t index, float value) { sum_[index] += static_cast<int32_t>(value); });
  }

  void finish(int16_t* stereo, size_t frames, int peak) const {
    const int knee = peak * 3 / 4;
    const int headroom = peak - knee;
    for (size_t i = 0; i < frames; ++i) {
      int magnitude = std::abs(sum_[i]);
      if (magnitude > knee) {
        const int excess = magnitude - knee;
        magnitude = knee + static_cast<int>(int64_t(headroom) * excess / (headroom + excess));
      }
      stereo[2 * i] = stereo[2 * i + 1] = static_cast<int16_t>(sum_[i] < 0 ? -magnitude : magnitude);
    }
  }
 private:
  int32_t sum_[blockFrames]{};
};
}
