#pragma once
#include "PianoCore.h"
#include <array>
namespace piano {
struct AudioEvent { uint32_t seq=0, ms=0, trigger=0; int midi=-1, volume=0, instrument=0; bool click=false; };
class AudioEvents {
 public:
  void capture(const View& v,uint32_t now) {
    const auto& last=events_[seq_%64];
    if(seq_ && last.midi==v.midi && last.volume==v.volume && last.instrument==v.settings.instrument && last.trigger==v.trigger && last.click==v.click) return;
    ++seq_; if(!seq_) ++seq_;
    auto& e=events_[seq_%64];e.seq=seq_;e.ms=now;e.trigger=v.trigger;e.midi=v.midi;
    e.volume=v.volume;e.instrument=v.settings.instrument;e.click=v.click;
  }
  unsigned read(uint32_t after,std::array<AudioEvent,64>& out,bool& reset) const {
    reset=!after || after>seq_ || seq_-after>64;
    if(!seq_) return 0;
    unsigned count=0;
    const uint32_t first=reset?seq_:after+1;
    for(uint32_t n=first;n && n<=seq_;++n) out[count++]=events_[n%64];
    return count;
  }
 private:
  uint32_t seq_=0; std::array<AudioEvent,64> events_{};
};
}
