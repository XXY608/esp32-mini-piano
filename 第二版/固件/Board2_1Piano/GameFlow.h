#pragma once
#include "PianoCore.h"
#include <algorithm>
#include <cmath>

namespace piano {
// Free-paced game cursor; the normal engine remains responsible for sound.
class GameFlow {
 public:
  struct Result { int accepted=-1; bool automatic=false; };
  void reset(const Engine& engine,uint32_t now){index_=0;previous_=modifiers_=0;run_=false;started_=now;skipRests(engine.song());}
  Result step(Engine& engine,uint16_t input,uint32_t now){
    Result result;const auto& notes=engine.song().notes;const uint16_t degrees=input&0x7f,rising=degrees&~previous_;
    bool retrigger=false;
    if(rising && index_<notes.size()){
      const auto& n=notes[index_];modifiers_=mods(n);
      if(n.pitch==lastPressedDegree(rising)){
        const size_t played=index_;runDegree_=n.pitch;due_=now+duration(n,engine.view().settings.bpm);
        result.accepted=static_cast<int>(index_++);skipRests(engine.song());run_=index_==played+1;started_=now;
      }
    }
    if(run_ && (degrees&(1u<<(runDegree_-1))) && !(rising&~(1u<<(runDegree_-1)))){
      if(index_<notes.size() && notes[index_].pitch==runDegree_ && static_cast<int32_t>(now-due_)>=0){
        const auto& n=notes[index_];modifiers_=mods(n);result.accepted=static_cast<int>(index_++);result.automatic=true;retrigger=true;
        due_+=duration(n,engine.view().settings.bpm);started_=now;
        if(index_<notes.size() && notes[index_].midi<0){skipRests(engine.song());run_=false;}
      }
    }else if(run_)run_=false;
    previous_=degrees;if(!degrees)modifiers_=0;
    engine.keys(degrees|(degrees?modifiers_:0),now);if(retrigger)engine.retriggerHeld();engine.tick(now);
    return result;
  }
  View view(const Engine& engine,uint32_t now) const {
    auto v=engine.view();v.mode=Mode::FreeScore;v.index=index_;v.count=engine.song().notes.size();v.finished=index_>=v.count;
    v.expected=v.finished?-1:engine.song().notes[index_].midi;
    v.elapsedBeats=-std::max(0.0,1.0-static_cast<double>(static_cast<uint32_t>(now-started_))/180.0);return v;
  }
 private:
  static uint16_t mods(const Note& n){return (n.octave<0?0x80:n.octave>0?0x200:0)|(n.sharp?0x100:0);}
  static uint32_t duration(const Note& n,int bpm){return static_cast<uint32_t>(std::max(1.0,std::round(n.beats*60000.0/bpm)));}
  void skipRests(const Song& song){while(index_<song.notes.size()&&song.notes[index_].midi<0)++index_;}
  size_t index_=0;uint16_t previous_=0,modifiers_=0;bool run_=false;int runDegree_=0;uint32_t started_=0,due_=0;
};
}
