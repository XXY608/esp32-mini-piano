#pragma once
#include "PianoCore.h"
#include <cstdint>

namespace lcd {
using piano::midiDegree;

inline bool midiSharp(int midi) {
  return (0x54a & (1 << (midi % 12))) != 0;
}

inline int midiOctave(int midi) { return midi / 12 - 5; }

inline void numberedPitchLabel(int degree, bool sharp, char (&label)[3]) {
  if (sharp) label[0] = '#';
  label[sharp ? 1 : 0] = static_cast<char>('0' + degree);
  label[sharp ? 2 : 1] = '\0';
}

template<class Display>
void drawOctaveDots(Display& d, int center, int labelY, int octave, uint16_t ink, int labelHeight=16) {
  const int count = octave < 0 ? -octave : octave;
  const int y = octave < 0 ? labelY + labelHeight : labelY - 2;
  const int left = center - (count * 3 - 1) / 2;
  for (int i = 0; i < count; ++i) d.fillRect(left + i * 3, y, 2, 2, ink);
}
}
