#pragma once
#include "NotationLayout.h"
#include "ChineseText.h"
#include "DisplayText.h"
#include <cstdio>

namespace lcd {
// Shared by the physical TFT and the native preview; no framebuffer on ESP32.
class ScoreScreen {
 public:
  static constexpr uint16_t kBackground = 0xffdf, kMint = 0x0475, kWhite = 0x18e4;
  static constexpr uint16_t kMuted = 0x6b6e, kRule = 0xdedb, kShadow = 0xc73e;
  void reset() { valid_ = false; }
  template<class Display>
  void draw(Display& d, const piano::View& v, const piano::ScorePage& page, uint32_t now) {
    const bool free = v.mode == piano::Mode::Manual && !v.finished;
    const bool lyrics = v.settings.showLyrics && !free;
    const int cursor = free ? v.manualKey : page.cursor;
    const bool active = free ? v.midi >= 0 : v.mode != piano::Mode::Manual && v.noteActive;
    const uint32_t row = free ? 0 : page.rows[0].first;
    const bool full = !valid_ || generation_ != v.songGeneration || free_ != free || title_ != page.title || lyrics_ != lyrics;
    const bool pageChanged = full || row_ != row;
    const bool lyricChanged = lyricFirst_ != page.lyricFirst || currentLyric_ != page.currentLyric;
    if (full) { scrolling_ = false; outgoingLyric_.clear(); outgoingNext_.clear(); }
    else if (lyrics && lyricChanged) {
      outgoingLyric_ = currentLyric_; outgoingNext_ = nextLyric_;
      scrollAt_ = now; scrolling_ = !currentLyric_.empty();
    }
    const uint32_t scrollElapsed = static_cast<uint32_t>(now - scrollAt_);
    if (scrolling_ && scrollElapsed >= 260) scrolling_ = false;
    const int lyricOffset = scrolling_ ? 18 - static_cast<int>(scrollElapsed * 18 / 260) : 0;
    const bool strike = cursor != cursor_ || v.index != index_ || (active && !active_) || (free && trigger_ != v.trigger);
    if (strike) pressedAt_ = now;
    const bool pressed = active && static_cast<uint32_t>(now - pressedAt_) < 90;
    if (full) d.fillRect(0, 0, 320, 240, kBackground);
    if (full || mode_ != v.mode || paused_ != v.paused || finished_ != v.finished) header(d, v);
    if (full || transpose_ != v.settings.transpose) songHeader(d, v, page, free);
    const bool topChanged = pageChanged || strike || active_ != active || pressed_ != pressed || paused_ != v.paused;
    if (topChanged) {
      piano::ScoreRow manual;
      if (free) { manual.count = 7; for (int i = 0; i < 7; ++i) manual.cells[i].pitch = i + 1; }
      drawRow(d, free ? manual : page.rows[0], 60, cursor, active, pressed, false, lyrics);
    }
    if (pageChanged) {
      drawRow(d, page.rows[1], 123, -1, false, false, true, lyrics, free);
      if (free || !page.rows[1].count) text(d, 8, 149, free ? kScoreEmpty : v.finished ? kFinished : "", kMuted);
    } else if (finished_ != v.finished && !page.rows[1].count) {
      d.fillRect(0, 123, 320, 63, kBackground);
      if (v.finished) text(d, 8, 149, kFinished, kMint);
    }
    if (lyrics && (topChanged || pageChanged || finished_ != v.finished || lyricChanged || nextLyric_ != page.nextLyric || lyricOffset_ != lyricOffset)) {
      drawLyricBand(d, 103, page.hasLyrics ? page.currentLyric : "此曲无歌词，请上传含词曲谱", outgoingLyric_, lyricOffset, kMint);
      drawLyricBand(d, 166, page.nextLyric, outgoingNext_, lyricOffset, kMuted);
    }
    const unsigned progress = free ? 0 : static_cast<unsigned>(page.progress * 304);
    const size_t position = free ? 0 : std::min(v.index + 1, v.count);
    if (full || bpm_ != v.settings.bpm || volume_ != v.volume || position_ != position || count_ != v.count) {
      d.fillRect(0, 187, 320, 23, kBackground); d.drawFastHLine(8, 187, 304, kRule);
      char value[40]; std::snprintf(value, sizeof(value), "BPM %d", v.settings.bpm); text(d, 8, 192, value, kWhite, 96);
      std::snprintf(value, sizeof(value), "%s%d%%", kVolume, v.volume); text(d, 112, 192, value, kWhite, 112);
      std::snprintf(value, sizeof(value), "%u/%u", static_cast<unsigned>(position), static_cast<unsigned>(free ? 0 : v.count));
      text(d, 312 - textWidth(value), 192, value, kWhite);
    }
    if (full || progress_ != progress) {
      d.fillRoundRect(8, 212, 304, 5, 2, kRule);
      if (progress) d.fillRoundRect(8, 212, progress, 5, 2, kMint);
    }
    if (full || mask_ != (v.keyMask & 0x380)) {
      d.fillRect(0, 220, 320, 20, kBackground);
      text(d, 18, 222, "<", v.low ? kMint : kMuted, 8); text(d, 34, 222, kLowNote, v.low ? kMint : kMuted, 48);
      text(d, 128, 222, "#", v.sharp ? kMint : kMuted, 8); text(d, 144, 222, "升号", v.sharp ? kMint : kMuted, 48);
      text(d, 242, 222, kHighNote, v.high ? kMint : kMuted, 48); text(d, 282, 222, ">", v.high ? kMint : kMuted, 8);
      d.drawFastVLine(106, 223, 14, kRule); d.drawFastVLine(212, 223, 14, kRule);
    }
    valid_ = true; free_ = free; generation_ = v.songGeneration; row_ = row; title_ = page.title;
    lyrics_ = lyrics;
    lyricFirst_ = page.lyricFirst; currentLyric_ = page.currentLyric; nextLyric_ = page.nextLyric; lyricOffset_ = lyricOffset;
    cursor_ = cursor; index_ = v.index; active_ = active; pressed_ = pressed; trigger_ = v.trigger;
    mode_ = v.mode; paused_ = v.paused; finished_ = v.finished;
    bpm_ = v.settings.bpm; volume_ = v.volume; transpose_ = v.settings.transpose;
    position_ = position; count_ = v.count; progress_ = progress; mask_ = v.keyMask & 0x380;
  }
 private:
  // Only these two 18px lyric bands scroll. Clip glyph bitmaps so animation
  // cannot erase the notation or require a full-screen framebuffer on ESP32.
  template<class Display> struct LyricBand {
    Display& display; int top;
    void drawRGBBitmap(int x, int y, uint16_t* pixels, int width, int height) {
      const int first = std::max(0, top - y), end = std::min(height, top + 18 - y);
      if (end > first) display.drawRGBBitmap(x, y + first, pixels + first * width, width, end - first);
    }
  };
  template<class Display> static void drawLyricBand(Display& d, int y, const std::string& value,
      const std::string& outgoing, int offset, uint16_t color) {
    d.fillRect(0, y, 320, 18, kBackground);
    LyricBand<Display> band{d, y};
    auto line = [&](const std::string& text, int top, uint16_t ink) {
      const int width = std::min(304, textWidth(text.c_str()));
      drawText(band, (320 - width) / 2, top, text.c_str(), ink, 304, kBackground);
    };
    if (offset) line(outgoing, y + 1 + offset - 18, kMuted);
    line(value, y + 1 + offset, color);
  }
  template<class Display> static void text(Display& d, int x, int y, const char* value, uint16_t color, int width = 304) {
    drawText(d, x, y, value, color, width, kBackground);
  }
  template<class Display> static void header(Display& d, const piano::View& v) {
    d.fillRect(0, 0, 320, 28, kBackground);
    for (int i = 0; i < 4; ++i) d.fillRoundRect(8 + i * 5, 6, 4, 16, 1, kWhite);
    for (int i = 0; i < 3; ++i) d.fillRect(11 + i * 5, 6, 3, 9, kBackground);
    text(d, 36, 5, kTitle, kWhite, 160);
    const char* state = v.paused ? kPaused : v.finished ? kFinished : kModeNames[static_cast<int>(v.mode)];
    text(d, 312 - textWidth(state), 5, state, kMint, 80);
    d.drawFastHLine(8, 27, 304, kRule);
  }
  template<class Display> static void songHeader(Display& d, const piano::View& v, const piano::ScorePage& page, bool free) {
    d.fillRect(0, 29, 320, 30, kBackground);
    const char* title = free ? kModeNames[0] : page.title.c_str();
    const bool clipped = textWidth(title) > 168;
    const int used = drawText(d, 8, 35, title, kWhite, clipped ? 144 : 168, kBackground);
    if (clipped) text(d, 8 + used, 35, "...", kMuted, 24);
    static const char* keys[] = {"C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"};
    char key[12]; std::snprintf(key, sizeof(key), "1=%s", keys[(12 + v.settings.transpose) % 12]);
    text(d, 184, 35, key, kMuted, 56);
    text(d, 248, 35, page.timeSignature.empty() || free ? kMeterUnknown : page.timeSignature.c_str(), kMuted, 64);
    d.drawFastHLine(8, 57, 304, kRule);
  }
  static unsigned beams(double beats) { return beats < .375 ? 2 : beats < .875 ? 1 : 0; }
  template<class Display> static void number(Display& d, int x, int y, int pitch, uint16_t ink, uint16_t paper, int height) {
    // Enlarge and thicken the visible digit bitmap; omit its blank top margin.
    const auto* glyph = findGlyph('0' + std::max(0, pitch));
    uint16_t pixels[20 * 30];
    for (int row = 0; row < height; ++row) for (int col = 0; col < 20; ++col) {
      const auto bits = glyph->bitmap[4 + row * 10 / height];
      const int source = col / 3 + 1;
      const bool on = (bits & (0x80 >> source)) || (col > 0 && (bits & (0x80 >> ((col - 1) / 3 + 1))));
      pixels[row * 20 + col] = on ? ink : paper;
    }
    d.drawRGBBitmap(x, y, pixels, 20, height);
  }
  template<class Display> static void drawRow(Display& d, const piano::ScoreRow& row, int y, int cursor, bool active, bool pressed, bool upcoming, bool lyrics, bool empty = false) {
    d.fillRect(0, y, 320, 63, kBackground);
    if (empty || !row.count) return;
    const int spacing = 304 / row.count;
    const int top = lyrics ? 4 : 10, height = lyrics ? 24 : 30, beamY = lyrics ? 32 : 46;
    for (unsigned i = 0; i < row.count; ++i) {
      const auto& n = row.cells[i]; const int center = 8 + spacing * i + spacing / 2;
      const bool selected = static_cast<int>(i) == cursor;
      const int offset = selected && pressed ? 2 : 0;
      const uint16_t ink = selected && active ? kBackground : upcoming ? kMuted : kWhite;
      const uint16_t paper = selected && active ? kMint : kBackground;
      if (selected) {
        if (active) { d.fillRoundRect(center - 17, y + top + 1, 34, height + 6, 5, kShadow); d.fillRoundRect(center - 17, y + top - 3 + offset, 34, height + 6, 5, kMint); }
        else d.drawRoundRect(center - 17, y + top - 3, 34, height + 8, 5, kMint);
      }
      if (n.continuation) d.fillRect(center - 10, y + top + height / 2 + offset, 20, 3, ink);
      else {
        number(d, center - 10, y + top + offset, n.pitch, ink, paper, height);
        if (n.sharp) drawText(d, center - 18, y + top + 3 + offset, "#", ink, 8, paper);
        if (n.octave > 0) d.fillRect(center - 1, y + top - 4, 3, 2, upcoming ? kMuted : kWhite);
        const unsigned count = beams(n.beats);
        for (unsigned level = 0; level < count; ++level) {
          const bool joined = i + 1 < row.count && !row.cells[i + 1].barBefore && !row.cells[i + 1].continuation && beams(row.cells[i + 1].beats) > level;
          d.drawFastHLine(center - 8, y + beamY + level * 4, joined ? spacing + 16 : 16, upcoming ? kMuted : kWhite);
        }
        if (n.octave < 0) d.fillRect(center - 1, y + beamY + 1 + count * 4, 3, 2, upcoming ? kMuted : kWhite);
        if (std::abs(n.beats - 1.5) < .01 || std::abs(n.beats - .75) < .01 || std::abs(n.beats - .375) < .01)
          d.fillRect(center + 10, y + top + height - 5 + offset, 2, 2, ink);
      }
      if (i && n.barBefore) d.drawFastVLine(8 + spacing * i, y + 6, 34, kRule);
    }
  }
  bool valid_ = false, free_ = true, lyrics_ = false, active_ = false, pressed_ = false, paused_ = false, finished_ = false;
  uint32_t generation_ = 0, row_ = 0, pressedAt_ = 0, trigger_ = 0;
  unsigned progress_ = 0; uint16_t mask_ = 0;
  int cursor_ = -1, bpm_ = 0, volume_ = 0, transpose_ = 0;
  size_t index_ = 0, position_ = 0, count_ = 0;
  piano::Mode mode_ = piano::Mode::Manual;
  std::string title_;
  bool scrolling_ = false;
  uint32_t lyricFirst_ = 0, scrollAt_ = 0;
  int lyricOffset_ = 0;
  std::string currentLyric_, nextLyric_, outgoingLyric_, outgoingNext_;
};
}
