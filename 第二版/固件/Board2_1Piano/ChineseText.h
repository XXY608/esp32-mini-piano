#pragma once
#include <stddef.h>
#include <stdint.h>
#include "ChineseFont.h"

namespace lcd {
inline uint32_t nextCodepoint(const char*& p) {
  const uint8_t lead = static_cast<uint8_t>(*p++);
  if (lead < 0x80) return lead;
  unsigned count = lead >= 0xf0 && lead <= 0xf4 ? 3 : lead >= 0xe0 && lead <= 0xef ? 2 : lead >= 0xc2 && lead <= 0xdf ? 1 : 0;
  if (!count) return '?';
  uint32_t value = lead & ((1u << (6 - count)) - 1);
  const unsigned length = count + 1;
  while (count--) {
    const uint8_t b = static_cast<uint8_t>(*p);
    if (!b || (b & 0xc0) != 0x80) return '?';
    value = (value << 6) | (b & 0x3f); ++p;
  }
  if (value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff) ||
      (length == 2 && value < 0x80) || (length == 3 && value < 0x800) || (length == 4 && value < 0x10000)) return '?';
  return value;
}

inline const Glyph* findGlyph(uint32_t codepoint) {
  size_t lo = 0, hi = sizeof(kGlyphs) / sizeof(kGlyphs[0]);
  while (lo < hi) {
    const size_t mid = lo + (hi - lo) / 2;
    if (kGlyphs[mid].codepoint < codepoint) lo = mid + 1;
    else hi = mid;
  }
  return lo < sizeof(kGlyphs) / sizeof(kGlyphs[0]) && kGlyphs[lo].codepoint == codepoint ? &kGlyphs[lo] : nullptr;
}

inline int textWidth(const char* text) {
  int width = 0;
  while (*text) { const auto* glyph = findGlyph(nextCodepoint(text)); width += glyph ? glyph->width : 8; }
  return width;
}

// Batch each glyph through the installed TFT driver's RGB bitmap method,
// avoiding a separate SPI address transaction for every lit pixel.
template<class Display>
int drawText(Display& display, int x, int y, const char* text, uint16_t color, int maxWidth = 304, uint16_t background = 0) {
  const int start = x;
  uint16_t pixels[16 * 16];
  while (*text) {
    const auto* glyph = findGlyph(nextCodepoint(text));
    if (!glyph) glyph = findGlyph('?');
    if (x - start + glyph->width > maxWidth) break;
    for (int row = 0; row < 16; ++row) for (int col = 0; col < glyph->width; ++col)
      pixels[row * glyph->width + col] = (glyph->bitmap[row * (glyph->width / 8) + col / 8] & (0x80 >> (col & 7))) ? color : background;
    display.drawRGBBitmap(x, y, pixels, glyph->width, 16);
    x += glyph->width;
  }
  return x - start;
}
template<class Display>
int drawDoubleText(Display& display, int x, int y, const char* text, uint16_t color, int maxWidth, uint16_t background) {
  const int start = x; uint16_t pixels[32 * 32];
  while (*text) {
    const auto* glyph = findGlyph(nextCodepoint(text)); if (!glyph) glyph = findGlyph('?');
    const int width = glyph->width * 2;
    if (x - start + width > maxWidth) break;
    for (int row = 0; row < 32; ++row) for (int col = 0; col < width; ++col)
      pixels[row * width + col] = (glyph->bitmap[(row / 2) * (glyph->width / 8) + (col / 2) / 8] & (0x80 >> ((col / 2) & 7))) ? color : background;
    display.drawRGBBitmap(x, y, pixels, width, 32); x += width;
  }
  return x - start;
}
}
