#pragma once
#include "PianoCore.h"
#include <array>

namespace piano {
// Free performance uses the same shared modifiers for all held degree keys.
inline std::array<int, 7> manualVoices(const View& view) {
  std::array<int, 7> notes{};
  const int octave = (view.high ? 1 : 0) - (view.low ? 1 : 0);
  for (int i = 0; i < 7; ++i) {
    const int midi = (view.keyMask & (1u << i)) ? noteMidi(i + 1, octave, view.sharp) : -1;
    notes[i] = midi >= 0 ? midi + view.settings.transpose : -1;
  }
  return notes;
}
}
