#pragma once
#include <stdint.h>
namespace piano {
// HIGH: hold to open, press to close. Isolated LOW tap: playback pause/resume.
class LocalControls {
 public:
  int scan(uint16_t mask, uint32_t now, bool playback = false) {
    int action = -1;
    constexpr uint16_t high = 1u << 9;
    constexpr uint16_t low = 1u << 7;
    if (!menu_ && playback && mask == low && previous_ == 0) pauseCandidate_ = true;
    if (!playback || menu_ || (mask != 0 && mask != low)) pauseCandidate_ = false;
    if (mask == 0 && previous_ == low && pauseCandidate_) {
      action = 8; pauseCandidate_ = false;
    }
    if (menu_ && armed_ && mask == high && previous_ == 0) {
      close(); holdCandidate_ = false; previous_ = mask; return 7;
    }
    if ((mask & high) && !(previous_ & high)) {
      holdCandidate_ = !menu_ && mask == high && previous_ == 0;
      since_ = now;
    }
    // Once combined with another key, HIGH remains a musical modifier until
    // released. Releasing a long high note cannot start a delayed menu hold.
    if (mask != high) holdCandidate_ = false;
    if (holdCandidate_ && static_cast<uint32_t>(now - since_) >= 1200) {
      menu_ = !menu_; holdCandidate_ = false; armed_ = false;
      action = 7;
    }
    if (mask == 0) armed_ = true;
    if (menu_ && armed_ && !(mask & 0x380)) {
      const auto rising = mask & ~previous_ & 0x7f;
      for (int i = 0; i < 7; ++i) if (rising & (1u << i)) {
        action = i;
        if (i < 2 && mask == (1u << i) && previous_ == 0) {
          repeatKey_ = i; repeatSince_ = now; repeatDelay_ = 1200;
        }
        armed_ = false; break;
      }
    }
    if (!menu_ || repeatKey_ < 0 || mask != (1u << repeatKey_)) repeatKey_ = -1;
    if (repeatKey_ >= 0 && static_cast<uint32_t>(now - repeatSince_) >= repeatDelay_) {
      action = repeatKey_; repeatSince_ = now; repeatDelay_ = 150;
    }
    previous_ = mask; return action;
  }
  bool menu() const { return menu_; }
  void close() { menu_ = false; armed_ = false; repeatKey_ = -1; }
 private:
  uint16_t previous_ = 0;
  uint32_t since_ = 0;
  int repeatKey_ = -1;
  uint32_t repeatSince_ = 0, repeatDelay_ = 1200;
  bool holdCandidate_ = false, pauseCandidate_ = false, menu_ = false, armed_ = true;
};
}
