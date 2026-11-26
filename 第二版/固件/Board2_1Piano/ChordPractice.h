#pragma once
#include "PianoCore.h"
#include <algorithm>
#include <cmath>

namespace piano {
class ChordPractice {
 public:
  static bool arrange(Song& song) {
    auto& notes=song.performance;
    if (notes.empty()) {
      double beats=0;
      for (const auto& note:song.notes) {
        const uint32_t start=std::lround(beats*kPerformanceTicksPerBeat);
        beats+=note.beats;
        const uint32_t end=std::lround(beats*kPerformanceTicksPerBeat);
        if (note.midi>=0) notes.push_back({start,std::max(start+1,end),static_cast<uint8_t>(note.midi),100,0});
      }
    }
    size_t write=0;
    std::array<size_t,7> previous{};
    for (size_t read=0;read<notes.size();) {
      std::array<PerformanceNote,7> selected{};
      const uint32_t start=notes[read].start;
      do {
        const auto& note=notes[read++];
        auto& winner=selected[midiDegree(note.midi)-1];
        if (note.velocity>winner.velocity ||
            (note.velocity==winner.velocity && note.midi>winner.midi)) winner=note;
      } while(read<notes.size() && notes[read].start==start);
      for (uint8_t lane=0;lane<7;++lane) {
        auto& winner=selected[lane];
        if (!winner.velocity) continue;
        if (previous[lane]) {
          auto& earlier=notes[previous[lane]-1];
          earlier.end=std::min(earlier.end,start);
        }
        winner.slot=lane;
        notes[write++]=winner;
        previous[lane]=write;
      }
    }
    notes.resize(write);
    return !notes.empty();
  }

  void start(const Song& song,uint32_t now) {
    audio_=PerformanceView{};
    audio_.enabled=true;
    audio_.epoch=++epoch_;
    audio_.count=song.performance.size();
    for (const auto& note:song.performance) audio_.endTick=std::max(audio_.endTick,note.end);
    last_=now;
    down_=fresh_=0;
    held_.fill(0);
    rewind(song);
  }

  void step(const Song& song,const Settings& settings,uint16_t input,uint32_t now) {
    const auto limit=uint64_t(target_?song.performance[next_].start:audio_.endTick)*unitsPerTick;
    units_=std::min(limit,units_+uint64_t(now-last_)*settings.bpm*kPerformanceTicksPerBeat);
    last_=now;
    const uint16_t keys=input&0x7f, rising=keys&~down_;
    fresh_=(fresh_|(rising&target_))&keys;
    for (unsigned lane=0;lane<7;++lane) {
      const unsigned bit=1u<<lane;
      auto& voice=audio_.voices[lane];
      if (!(keys&bit)) { voice.midi=-1; held_[lane]=0; continue; }
      if (!(rising&target_&bit)) continue;
      const auto event=held_[lane]=cue_[lane];
      const auto& note=song.performance[event-1];
      voice.midi=note.midi+settings.transpose;
      voice.velocity=note.velocity;
      voice.trigger=++trigger_;
    }
    down_=keys;
    if (target_ && fresh_==target_) {
      next_=after_;
      fresh_=0;
      group(song);
    } else if (finished() && settings.repeat && !keys) {
      rewind(song);
    }

    while(first_<song.performance.size() && uint64_t(song.performance[first_].end)*unitsPerTick<=units_) ++first_;
  }

  const PerformanceView& audio() const { return audio_; }
  PerformanceView display() const {
    auto frame=audio_;
    frame.positionTicks=units_/static_cast<double>(unitsPerTick);
    frame.started=first_;
    for (unsigned lane=0;lane<7;++lane) frame.voices[lane].trigger=held_[lane];
    return frame;
  }
  unsigned firstCue() const { return target_?next_+1:0; }
  unsigned cueCount() const { return after_-next_; }
  uint16_t targets() const { return target_; }
  bool finished() const { return !target_ && units_==uint64_t(audio_.endTick)*unitsPerTick; }

 private:
  void rewind(const Song& song) {
    next_=first_=0;
    units_=0;
    group(song);
  }
  void group(const Song& song) {
    target_=0;
    cue_.fill(0);
    after_=next_;
    const auto& notes=song.performance;
    while (after_<notes.size() && notes[after_].start==notes[next_].start) {
      const auto lane=notes[after_].slot;
      cue_[lane]=++after_;
      target_|=1u<<lane;
    }
  }

  static constexpr uint64_t unitsPerTick=60000;
  PerformanceView audio_;
  std::array<uint16_t,7> cue_{}, held_{};
  size_t next_=0, after_=0, first_=0;
  uint64_t units_=0;
  uint32_t last_=0, trigger_=0, epoch_=0x40000000u;
  uint16_t down_=0, target_=0, fresh_=0;
};
}
