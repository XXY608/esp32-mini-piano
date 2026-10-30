#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cstddef>

namespace piano {
// HPS1: LE uint32 rate, first key, root step, root count, frames/root,
// instrument id, reserved; followed by mono signed 16-bit PCM.
struct SampleBank {
  const int16_t* pcm = nullptr;
  int instrument = -1;
  static constexpr unsigned kRate = 22050, kFrames = 88200, kRoots = 31;
  static constexpr size_t kBytes = 32 + size_t(kFrames) * kRoots * 2;
  static uint32_t word(const uint8_t* p) { return uint32_t(p[0]) | uint32_t(p[1])<<8 | uint32_t(p[2])<<16 | uint32_t(p[3])<<24; }
  static bool validHeader(const uint8_t* data, int id) {
    return data && !std::memcmp(data,"HPS1",4) && word(data+4)==kRate && word(data+8)==36 &&
        word(data+12)==2 && word(data+16)==kRoots && word(data+20)==kFrames && word(data+24)==unsigned(id) && word(data+28)==0;
  }
  bool bind(const uint8_t* data, size_t size, int id) {
    pcm = nullptr; instrument = -1;
    if (!data || size != kBytes || !validHeader(data,id)) return false;
    pcm = reinterpret_cast<const int16_t*>(data+32); instrument=id; return true;
  }
};
class SampleVoice {
 public:
  void reset() { position_=0; gain_=0; last_=0; transition_=0; midi_=-1; trigger_=0; bank_=nullptr; }
  void mix(const SampleBank& bank, int midi, int volume, int16_t* stereo, size_t frames,
           int peak, bool click, uint32_t trigger) {
    if (bank.pcm != bank_) { reset(); bank_=bank.pcm; }
    if (!bank.pcm) return;
    if (midi>=36 && midi<=96 && (midi!=midi_ || trigger!=trigger_)) {
      root_=(midi-36)/2; step_=std::pow(2.0,(midi-(36+root_*2))/12.0);
      position_=0; transition_=110; previous_=last_;
    }
    midi_=midi; trigger_=trigger;
    const bool on=midi>=36 && midi<=96;
    const bool sustained=bank.instrument==5 || bank.instrument==6 || bank.instrument==7 || bank.instrument==9;
    const float target=on?std::max(0,std::min(100,volume))/100.0f:0;
    const int16_t* samples=bank.pcm+size_t(root_)*SampleBank::kFrames;
    for(size_t i=0;i<frames;i++) {
      gain_+=(target-gain_)*(on?.01f:.0015f);
      float value=0;
      if(position_<SampleBank::kFrames-1) {
        const auto index=static_cast<unsigned>(position_); const float fraction=position_-index;
        value=samples[index]+(samples[index+1]-samples[index])*fraction;
        constexpr unsigned fade=1024, loop=22050;
        if(position_>=SampleBank::kFrames-fade-1) {
          const float blend=(position_-(SampleBank::kFrames-fade-1))/fade;
          if(sustained) {
            const unsigned next=loop+index-(SampleBank::kFrames-fade-1);
            value=value*(1-blend)+samples[next]*blend;
          } else value*=1-blend;
        }
        position_+=step_;
        if(sustained && position_>=SampleBank::kFrames-1) position_=loop+fade+(position_-(SampleBank::kFrames-1));
      }
      value*=gain_*peak/32768.0f*(click?.8f:1);
      if(transition_) { const float mix=transition_/110.0f; value=value*(1-mix)+previous_*mix; --transition_; }
      if(!volume) value=0;
      last_=value;
      for(int channel=0;channel<2;channel++) {
        const float sum=stereo[2*i+channel]+value;
        stereo[2*i+channel]=static_cast<int16_t>(std::max(float(-peak),std::min(float(peak),sum)));
      }
    }
  }
 private:
  const int16_t* bank_=nullptr;
  double position_=0,step_=1;
  float gain_=0,last_=0,previous_=0;
  unsigned root_=0,transition_=0;
  int midi_=-1;
  uint32_t trigger_=0;
};
}
