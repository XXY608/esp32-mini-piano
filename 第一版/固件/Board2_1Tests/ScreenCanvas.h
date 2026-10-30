#pragma once
#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <string>
#include <vector>

// GFX drawing surface for running the production screen renderer on a PC.
struct ScreenCanvas {
  std::vector<uint16_t> pixels = std::vector<uint16_t>(320 * 240, 0);
  unsigned writes = 0;
  void pixel(int x, int y, uint16_t color) {
    if (x >= 0 && x < 320 && y >= 0 && y < 240) { pixels[y * 320 + x] = color; ++writes; }
  }
  void fillRect(int x, int y, int width, int height, uint16_t color) {
    for (int row = 0; row < height; ++row) for (int col = 0; col < width; ++col) pixel(x + col, y + row, color);
  }
  void drawFastHLine(int x, int y, int width, uint16_t color) { fillRect(x, y, width, 1, color); }
  void drawFastVLine(int x, int y, int height, uint16_t color) { fillRect(x, y, 1, height, color); }
  static bool rounded(int x, int y, int w, int h, int radius) {
    if (x < 0 || y < 0 || x >= w || y >= h) return false;
    const int dx = x < radius ? radius - x : x >= w - radius ? x - (w - radius - 1) : 0;
    const int dy = y < radius ? radius - y : y >= h - radius ? y - (h - radius - 1) : 0;
    return dx * dx + dy * dy <= radius * radius;
  }
  void fillRoundRect(int x, int y, int w, int h, int r, uint16_t color) {
    r = std::min(r, std::min(w, h) / 2);
    for (int yy = 0; yy < h; ++yy) for (int xx = 0; xx < w; ++xx) if (rounded(xx, yy, w, h, r)) pixel(x + xx, y + yy, color);
  }
  void drawRoundRect(int x, int y, int w, int h, int r, uint16_t color) {
    for (int yy = 0; yy < h; ++yy) for (int xx = 0; xx < w; ++xx)
      if (rounded(xx, yy, w, h, r) && !rounded(xx - 1, yy - 1, w - 2, h - 2, r - 1)) pixel(x + xx, y + yy, color);
  }
  void drawRGBBitmap(int x, int y, uint16_t* bitmap, int w, int h) {
    if (x < 0 || y < 0 || x + w > 320 || y + h > 240) std::abort();
    for (int yy = 0; yy < h; ++yy) for (int xx = 0; xx < w; ++xx) pixel(x + xx, y + yy, bitmap[yy * w + xx]);
  }
  bool save(const std::string& path) const {
    std::ofstream out(path, std::ios::binary); out << "P6\n320 240\n255\n";
    for (auto value : pixels) {
      const unsigned char rgb[] = {static_cast<unsigned char>((value >> 11) * 255 / 31), static_cast<unsigned char>(((value >> 5) & 63) * 255 / 63), static_cast<unsigned char>((value & 31) * 255 / 31)};
      out.write(reinterpret_cast<const char*>(rgb), 3);
    }
    return out.good();
  }
};
