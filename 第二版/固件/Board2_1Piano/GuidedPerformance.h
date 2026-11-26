#pragma once
#include "PianoCore.h"
#include <algorithm>
#include <cmath>

namespace piano {
// The existing single-note score supplies the keys to press. The original
// performance supplies the voices, velocity, timing and pedal between presses.
class GuidedPerformance {
  struct Cue { uint32_t tick; uint16_t guide, event; };
  static constexpr uint64_t unitsPerTick=60000;

 public:
  void start(const Song& song,uint32_t now) {
    cues_.clear();
    if (!song.performance.empty()) {
      double beats=0;
      for (size_t i=0;i<song.notes.size();++i) {
        const auto& note=song.notes[i];
        if (note.midi>=0 && !addCue(song,static_cast<uint32_t>(std::lround(beats*kPerformanceTicksPerBeat)),i)) {
          cues_.clear();
          break;
        }
        beats+=note.beats;
      }
    }
    end_=0;
    if (!cues_.empty()) for (const auto& note:song.performance) end_=std::max(end_,note.end);
    rewind(now);
  }

  void rewind(uint32_t now) {
    view_=PerformanceView{};
    view_.epoch=++epoch_;
    view_.enabled=!cues_.empty();
    view_.endTick=end_;
    units_=cues_.empty()?0:uint64_t(cues_.front().tick)*unitsPerTick;
    view_.positionTicks=units_/static_cast<double>(unitsPerTick);
    next_=event_=pedal_=0;
    active_.fill(0);
    running_=pedalDown_=false;
    last_=now;
  }

  void step(const Song& song,const View& guide,int accepted,uint32_t now,bool suspended) {
    if (!view_.enabled) return;
    const uint32_t elapsed=now-last_;
    last_=now;
    view_.voices.fill(PerformanceVoice{});
    view_.paused=suspended || guide.paused;
    view_.pedal=false;
    if (view_.paused) return;

    if (next_<cues_.size() && accepted==cues_[next_].guide) {
      units_=uint64_t(cues_[next_++].tick)*unitsPerTick;
      running_=true;
    } else if (running_) {
      const auto limit=uint64_t(next_<cues_.size()?cues_[next_].tick:end_)*unitsPerTick;
      units_=std::min(limit,units_+uint64_t(elapsed)*guide.settings.bpm*kPerformanceTicksPerBeat);
      running_=units_<limit;
    }
    view_.positionTicks=units_/static_cast<double>(unitsPerTick);
    if (!running_) return;

    const uint32_t tick=units_/unitsPerTick;
    while (pedal_<song.pedal.size() && song.pedal[pedal_]/2<=tick) pedalDown_=song.pedal[pedal_++]&1;
    while (event_<song.performance.size() && song.performance[event_].start<=tick) {
      const auto& note=song.performance[event_++];
      if (note.end>tick) active_[note.slot]=event_;
    }
    view_.started=event_;
    view_.pedal=pedalDown_;
    for (size_t slot=0;slot<active_.size();++slot) {
      if (!active_[slot]) continue;
      const auto& note=song.performance[active_[slot]-1];
      if (note.end<=tick) {
        active_[slot]=0;
        continue;
      }
      auto& voice=view_.voices[slot];
      voice.midi=note.midi+guide.settings.transpose;
      voice.trigger=active_[slot];
      voice.velocity=note.velocity;
    }
  }

  const PerformanceView& view() const { return view_; }
  unsigned nextEvent() const { return next_<cues_.size()?cues_[next_].event:0; }
  int nextDegree(const Song& song) const { return next_<cues_.size()?song.notes[cues_[next_].guide].pitch:0; }

 private:
  bool addCue(const Song& song,uint32_t tick,size_t guide) {
    const auto* match=findPerformanceCue(song,tick,song.notes[guide].midi,cues_.empty()?0:cues_.back().tick+1);
    if (!match) return false;
    cues_.push_back({match->start,static_cast<uint16_t>(guide),static_cast<uint16_t>(match-song.performance.data()+1)});
    return true;
  }

  std::vector<Cue> cues_;
  std::array<uint16_t,kPerformanceVoices> active_{};
  PerformanceView view_;
  size_t next_=0,event_=0,pedal_=0;
  uint32_t end_=0,last_=0,epoch_=0x80000000u;
  uint64_t units_=0;
  bool running_=false,pedalDown_=false;
};
}
