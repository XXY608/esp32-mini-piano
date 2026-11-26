#pragma once
#include "PianoCore.h"

namespace piano {
// Stateful input adapter for the game screen. It never changes notation mode.
class GameAssist {
 public:
  struct Result { int accepted=-1; bool automatic=false; };
  void reset() { previous_=modifiers_=0; }
  Result step(Engine& engine,uint16_t input,uint32_t now) {
    Result result;
    const uint16_t degrees=input&0x7f, rising=degrees&~previous_;
    const int pressedDegree=lastPressedDegree(rising);
    bool early=false;
    if(pressedDegree){
      early=engine.prepareEarly(pressedDegree);
      const auto state=engine.view();
      modifiers_=state.index<engine.song().notes.size()?mods(engine.song().notes[state.index]):0;
    }
    previous_=degrees;if(!degrees)modifiers_=0;
    const uint16_t effective=degrees|(degrees?modifiers_:0);
    const auto before=engine.view();engine.keys(effective,now);
    const auto pressed=engine.view();
    if(rising && pressed.noteActive && (!before.noteActive || early) && pressed.index<engine.song().notes.size()){
      const auto& n=engine.song().notes[pressed.index];
      if(n.pitch==pressedDegree) result.accepted=static_cast<int>(pressed.index);
    }
    engine.tick(now);
    if(pressed.mode==Mode::FreeScore && pressed.noteActive){
      const auto after=engine.view();const auto& notes=engine.song().notes;
      if(after.mode==Mode::FreeScore && after.index==pressed.index+1 && after.index<notes.size() &&
         notes[pressed.index].pitch>0 && notes[after.index].pitch==notes[pressed.index].pitch &&
         (effective&(1u<<(notes[after.index].pitch-1))) && pressed.manualKey+1==notes[after.index].pitch){
        modifiers_=mods(notes[after.index]);engine.keys(degrees|modifiers_,now);
        if(engine.continueHeld(now)){result.accepted=static_cast<int>(after.index);result.automatic=true;}
      }
    }
    return result;
  }
 private:
  static uint16_t mods(const Note& n){return (n.octave<0?0x80:n.octave>0?0x200:0)|(n.sharp?0x100:0);}
  uint16_t previous_=0,modifiers_=0;
};
}
