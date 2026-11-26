#pragma once
#include "GameScreen.h"
#include "PianoCore.h"

namespace lcd {
// The score cursor can advance before a held key stops sounding. Match the
// actual voice to the accepted score event independently of that cursor.
inline bool isGuidedNoteSounding(const piano::Song& song, const piano::View& view, int accepted) {
  if (view.paused || view.midi < 0 || view.manualKey < 0 || accepted < 0 ||
      static_cast<size_t>(accepted) >= song.notes.size()) return false;
  const auto& note = song.notes[accepted];
  return note.pitch == view.manualKey + 1 && note.midi + view.settings.transpose == view.midi;
}

class GuidedGame {
 public:
  void update(GameFrame& frame, const piano::Song& song, const piano::View& view,
              int accepted, uint32_t heldMs, uint32_t now, const piano::View* playback=nullptr) {
    using Style = GameStyle;
    frame.guided = true;
    frame.keys = 0;
    frame.guideMask = 0;
    frame.count = 0;
    const bool simple = view.mode==piano::Mode::Simple;
    const bool matched = simple
      ? !view.paused && view.midi>=0 && accepted>=0 && static_cast<size_t>(accepted)<song.notes.size()
      : isGuidedNoteSounding(song, view, accepted);
    if (simple) {
      if (view.index<song.notes.size()) {
        const int next=song.notes[view.index].pitch;
        if (next>=1 && next<=7) frame.guideMask=1u<<(next-1);
      }
      // The queued note can sound after its key is released or a later key is pressed.
      if (matched) {
        const auto& note=song.notes[accepted];
        if (note.pitch>=1 && note.pitch<=7) {
          frame.keys=1u<<(note.pitch-1);
          frame.inputStyle[note.pitch-1]=Style::colorIndex(note.octave,note.sharp);
        }
      }
    } else if (!view.paused && view.midi >= 0 && view.manualKey >= 0 && view.manualKey < 7) {
      frame.keys = 1u << view.manualKey;
      frame.inputStyle[view.manualKey] = Style::colorIndex((view.high ? 1 : 0) - (view.low ? 1 : 0), view.sharp);
    }

    const auto& clock=playback?*playback:view;
    double total = 0, target = 0;
    for (size_t i = 0; i < song.notes.size(); ++i) {
      if (i < clock.index) target += song.notes[i].beats;
      total += song.notes[i].beats;
    }
    target = std::min(total, target + std::max(0.0, clock.elapsedBeats));
    const double msPerBeat = 60000.0 / std::max(20, view.settings.bpm);
    const double pixelsPerBeat = Style::pixelsPerMs * msPerBeat;
    if (playback || !valid_ || session_ != view.songGeneration || view.index < index_) {
      position_ = target;
      valid_ = true;
    } else if (!view.paused) {
      // Input can move the cue ahead, but the waterfall still follows the shared display speed.
      const double step = static_cast<uint32_t>(now - last_) / msPerBeat;
      position_ += std::max(0.0, std::min(target - position_, step));
    }
    session_ = view.songGeneration;
    index_ = view.index;
    last_ = now;
    frame.progress = total > 0 ? position_ / total : 0;

    double beat = 0;
    for (size_t i = 0; i < song.notes.size() && frame.count < GameFrame::MaxTiles; ++i) {
      const auto& note = song.notes[i];
      const double bottom = Style::line - (beat - position_) * pixelsPerBeat;
      if (bottom <= Style::boardTop) break;
      const double top = bottom - std::max(20.0, note.beats * pixelsPerBeat);
      beat += note.beats;
      if (top >= Style::line || note.pitch < 1 || note.pitch > 7) continue;

      auto& tile = frame.tiles[frame.count++];
      tile = GameTile{};
      tile.degree = note.pitch;
      tile.octave = note.octave;
      tile.sharp = note.sharp;
      tile.bottom = bottom;
      tile.top = top;
      tile.cue = simple && i==view.index;
      tile.hit = tile.holding = matched && i == static_cast<size_t>(accepted);
      tile.fill = tile.hit ? (simple?1.f:std::min(1.0, heldMs / (note.beats * msPerBeat))) : 0;
    }
  }
 private:
  bool valid_ = false;
  uint32_t session_ = 0, last_ = 0;
  size_t index_ = 0;
  double position_ = 0;
};
}
