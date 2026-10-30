#pragma once
#include "PianoCore.h"
#include <cmath>

namespace piano {
class AudioSynth {
 public:
  AudioSynth(unsigned sampleRate, int peak) : sampleRate_(sampleRate), peak_(peak) {}
  void render(int midi, int volume, int16_t* stereo, size_t frames, int instrument = 0, bool click = false, uint32_t trigger = 0) {
    if (trigger != trigger_ || midi != midi_) { age_ = 0; trigger_ = trigger; midi_ = midi; }
    if (click && !wasClick_) clickAge_ = 0;
    wasClick_ = click;
    float noteGain = 1;
    if (midi >= 0) {
      const double hz = frequency(midi);
      step_ = static_cast<uint32_t>(hz * 4294967296.0 / sampleRate_);
      // First listening correction for the physical speaker in pure-tone mode:
      // C3 and below keep full output; above C3 reduce 6dB per octave.
      // Do not boost bass beyond the existing peak or alter the sine's spectrum.
      if (instrument == 0 && midi > 48) noteGain = static_cast<float>(130.81278265 / hz);
    }
    volume = volume < 0 ? 0 : volume > 100 ? 100 : volume;
    const float target = midi >= 0 ? peak_ * volume / 100.0f * noteGain : 0;
    for (size_t i = 0; i < frames; ++i) {
      envelope_ += (target - envelope_) * 0.02f;
      const float angle = (phase_ / 4294967296.0f) * 6.28318530718f;
      float wave = std::sin(angle);
      if (instrument == 1) {
        wave = (wave + .30f * std::sin(2 * angle) + .12f * std::sin(3 * angle)) / 1.42f;
        wave *= .25f + .75f * std::exp(-static_cast<float>(age_) / (sampleRate_ * .65f));
      } else if (instrument == 2) wave = (wave + .3f * std::sin(2 * angle) + .2f * std::sin(4 * angle)) / 1.5f;
      float value = wave * envelope_ * (click || clickAge_ < sampleRate_ / 40 ? .8f : 1.0f);
      if (clickAge_ < sampleRate_ / 40) {
        const float clickEnv = 1.0f - static_cast<float>(clickAge_) / (sampleRate_ / 40);
        value += std::sin(clickAge_ * 6.28318530718f * 1800 / sampleRate_) * clickEnv * peak_ * .2f * volume / 100;
        ++clickAge_;
      }
      const int16_t sample = static_cast<int16_t>(value);
      if (age_ < sampleRate_ * 60) ++age_;
      phase_ += step_;
      stereo[i * 2] = stereo[i * 2 + 1] = sample;
    }
  }
 private:
  unsigned sampleRate_;
  int peak_;
  uint32_t phase_ = 0, step_ = 0;
  float envelope_ = 0;
  int midi_ = -1;
  uint32_t trigger_ = 0, age_ = 0, clickAge_ = 0xffffffff;
  bool wasClick_ = false;
};
}
