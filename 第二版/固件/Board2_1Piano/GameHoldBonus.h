#pragma once
#include "PianoCore.h"

namespace piano {
// Matches the simulator's release bonus; automatic continuations do not
// replace the note originally accepted by an actual key press.
class GameHoldBonus {
 public:
  void reset() { index_ = -1; pitch_ = 0; beats_ = 0; }
  void accepted(int index, const Note& note) {
    index_ = index; pitch_ = note.pitch; beats_ = note.beats;
  }
  int release(uint16_t keys, const View& view, bool flow) {
    if (index_ < 0 || (pitch_ >= 1 && pitch_ <= 7 && (keys & (1u << (pitch_ - 1))))) return 0;
    const bool earned = !view.paused && !flow &&
      (view.index > static_cast<size_t>(index_) || view.elapsedBeats >= beats_ - 160.0 * view.settings.bpm / 60000.0);
    reset(); return earned ? 20 : 0;
  }
 private:
  int index_ = -1, pitch_ = 0;
  double beats_ = 0;
};
}
