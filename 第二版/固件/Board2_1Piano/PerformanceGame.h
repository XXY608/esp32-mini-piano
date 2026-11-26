#pragma once
#include "GameScreen.h"
#include "PianoCore.h"

namespace lcd {
inline bool showsPerformance(const piano::PerformanceView& performance, const piano::View& view) {
  return performance.enabled || (view.finished && performance.endTick != 0);
}

class PerformanceLayout {
 public:
  struct Placement { uint8_t column=0, columns=0; };

  void update(const piano::Song& song, const piano::View& view) {
    const auto& notes=song.performance;
    const int transpose=view.settings.transpose;
    if (notes.data()==source_ && notes.size()==placements_.size() &&
        view.songGeneration==session_ && transpose==transpose_) return;
    source_=notes.data(); session_=view.songGeneration; transpose_=transpose;
    placements_.assign(notes.size(),Placement{});
    struct Lane {
      uint32_t ends[piano::kPerformanceVoices]{};
      uint32_t groupEnd=0;
      size_t last=0;
      uint8_t columns=0;
    } lanes[7];
    for (size_t i=0;i<notes.size();++i) {
      const auto& note=notes[i];
      auto& lane=lanes[midiDegree(note.midi+transpose)-1];
      if (note.start>=lane.groupEnd && lane.columns) {
        placements_[lane.last].columns=lane.columns;
        lane.columns=0;
      }
      // Previous groups have ended, so their column-end times are already free.
      auto& column=placements_[i].column;
      while (size_t(column)+1<piano::kPerformanceVoices && lane.ends[column]>note.start) ++column;
      lane.ends[column]=note.end;
      lane.last=i;
      lane.groupEnd=std::max(lane.groupEnd,note.end);
      lane.columns=std::max(lane.columns,static_cast<uint8_t>(column+1));
    }
    for (const auto& lane:lanes) if(lane.columns) placements_[lane.last].columns=lane.columns;
    // Each completed overlapping group marks its last note. Propagate its
    // width backwards so scrolling cannot reassign a note to another column.
    uint8_t widths[7]{};
    for (size_t i=notes.size();i>0;) {
      --i;
      auto& count=widths[midiDegree(notes[i].midi+transpose)-1];
      auto& columns=placements_[i].columns;
      if (columns) count=columns;
      else columns=count;
    }
  }

  const Placement& operator[](size_t index) const { return placements_[index]; }

 private:
  const piano::PerformanceNote* source_=nullptr;
  uint32_t session_=0;
  int transpose_=0;
  std::vector<Placement> placements_;
};

inline void performanceGame(GameFrame& frame, PerformanceLayout& layout, const piano::Song& song, const piano::View& view,
                            const piano::PerformanceView& performance, unsigned cue=0,
                            unsigned cueCount=1, bool keySlots=false) {
  using Style = GameStyle;
  frame.polyphonic = true;
  frame.keySlots = keySlots;
  frame.keys = 0;
  frame.count = 0;
  frame.progress = performance.endTick ? performance.positionTicks / performance.endTick : 0;
  // During playback only currently active notes can extend back from the cursor.
  size_t first = performance.paused ? 0 : performance.started;
  for (size_t slot=0;slot<performance.voices.size();++slot) {
    const auto& voice=performance.voices[slot];
    if (voice.midi < 0) continue;
    if (!keySlots && voice.trigger) first = std::min(first, static_cast<size_t>(voice.trigger - 1));
    const int lane = keySlots?static_cast<int>(slot):midiDegree(voice.midi)-1;
    if (frame.keys & (1u << lane)) continue;
    frame.keys |= 1u << lane;
    frame.inputStyle[lane] = Style::colorIndex(midiOctave(voice.midi), midiSharp(voice.midi));
  }
  if (view.finished) { frame.progress = 1; return; }
  if (!keySlots) layout.update(song,view);

  const double pixelsPerTick = Style::pixelsPerMs * 60000.0 /
    (std::max(20, view.settings.bpm) * piano::kPerformanceTicksPerBeat);
  const double position = performance.positionTicks;
  const double visibleEnd = position + (Style::line - Style::boardTop) / pixelsPerTick;
  for (size_t index = first; index < song.performance.size() && frame.count < GameFrame::MaxTiles; ++index) {
    const auto& note = song.performance[index];
    if (note.start >= visibleEnd) break;
    if (note.end <= position) continue;
    const int midi = keySlots?note.midi:note.midi+view.settings.transpose;
    const int lane = keySlots?note.slot:midiDegree(midi)-1;
    auto& tile = frame.tiles[frame.count++];
    tile = GameTile{};
    tile.degree = lane + 1;
    tile.octave = midiOctave(midi);
    tile.sharp = midiSharp(midi);
    tile.cue = cue && index+1>=cue && index+1-cue<cueCount;
    tile.bottom = Style::line - (note.start - position) * pixelsPerTick;
    tile.top = Style::line - (note.end - position) * pixelsPerTick;
    if (keySlots) tile.top=std::min(tile.top,tile.bottom-12.f);
    else { tile.column=layout[index].column; tile.columns=layout[index].columns; }
    const auto& voice = performance.voices[note.slot];
    tile.hit = tile.holding = voice.midi >= 0 && voice.trigger == index + 1;
    tile.fill = tile.hit ? (position-note.start)/(note.end-note.start) : 0;
  }
}
}
