#pragma once
#include "ManualVoices.h"
#include "PolyMix.h"
#include <algorithm>

namespace piano {
// Each held key retains its gain. Only peaks on the combined bus are limited.
class ManualMixer {
 public:
  ManualMixer(unsigned rate, int peak) : synth_{AudioSynth(rate,peak),AudioSynth(rate,peak),AudioSynth(rate,peak),AudioSynth(rate,peak),AudioSynth(rate,peak),AudioSynth(rate,peak),AudioSynth(rate,peak)}, click_(rate,peak), rate_(rate), peak_(peak) {}
  void reset() {
    for (auto& synth : synth_) synth = AudioSynth(rate_, peak_);
    for (auto& voice : sample_) voice.reset();
    click_ = AudioSynth(rate_, peak_);
  }
  void render(const View& v, const SampleBank& bank, int16_t* out, size_t frames) {
    auto notes=manualVoices(v);
    click_.render(-1,v.volume,out,frames,0,v.click,0);
    mix_.begin(out,frames);
    for(int i=0;i<7;++i){
      if(v.settings.instrument>=3){
        mix_.addSample(sample_[i],bank,notes[i],v.volume,peak_,v.click,0,false,frames);
      }else{
        mix_.addSynth(synth_[i],notes[i],v.volume,v.settings.instrument,0,frames);
      }
    }
    mix_.finish(out,frames,peak_);
  }
 private:
  AudioSynth synth_[7],click_;
  SampleVoice sample_[7];PolyMix mix_;unsigned rate_;int peak_;
};
}
