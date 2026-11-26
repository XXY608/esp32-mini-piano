#pragma once
#include <cstdint>
#include <algorithm>

namespace piano {
// Phone polls renew this temporary route. A lost page cannot leave the piano muted.
class AudioOutput {
 public:
  static constexpr uint32_t kLeaseMs = 2000;
  bool ready(uint32_t now) const { return owner_ && uint32_t(now - renewed_) < kLeaseMs; }
  bool muted(uint32_t now) const { return phone_ && ready(now); }
  bool selectPhone(bool phone,uint32_t now) {
    if(phone && !ready(now)) return false;
    phone_ = phone; return true;
  }
  bool acquire(uint32_t session, uint32_t now) {
    if (!session || (ready(now) && owner_ != session)) return false;
    owner_ = session; renewed_ = now; phone_ = true; return true;
  }
  bool renew(uint32_t session, uint32_t now) {
    if (!session || owner_ != session || !ready(now)) return false;
    renewed_ = now; return true;
  }
  bool release(uint32_t session) {
    if (!session || owner_ != session) return false;
    owner_ = 0; phone_ = false; return true;
  }
  void silence(int16_t* samples, unsigned count, uint32_t now) const {
    if (muted(now)) std::fill(samples, samples + count, int16_t(0));
  }
 private:
  uint32_t owner_ = 0, renewed_ = 0;
  bool phone_ = false;
};
}
