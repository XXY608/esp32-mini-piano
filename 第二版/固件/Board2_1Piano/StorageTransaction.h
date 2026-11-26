#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <initializer_list>

namespace piano {
template<class File, class Text>
bool readSongText(File& file, size_t expected, Text& text) {
  text = "";
  // Arduino ESP32 2.0.17 loses the high bits of the old length when a
  // growing String reallocates past 64 KiB. Reserve before appending.
  if (!text.reserve(expected)) return false;
  uint8_t chunk[513];
  for (size_t offset = 0; offset < expected;) {
    const size_t count = std::min(expected - offset, sizeof(chunk) - 1);
    if (file.read(chunk, count) != count) return false;
    chunk[count] = 0;
    if (!text.concat(chunk, count)) return false;
    offset += count;
  }
  return text.length() == expected;
}

template<class File, class Text>
bool matchesSongFile(File& file, const Text& body) {
  if (file.size() != body.length()) return false;
  uint8_t chunk[512];
  for (size_t offset = 0; offset < body.length();) {
    const size_t count = std::min(static_cast<size_t>(body.length()) - offset, sizeof(chunk));
    if (file.read(chunk, count) != count || std::memcmp(chunk, body.c_str() + offset, count)) return false;
    offset += count;
  }
  return true;
}

// The same transaction is tested with an in-memory fault-injecting store and
// used through an Arduino FS adapter. It does not claim flash crash atomicity.
template<class Store, class Text>
bool commitSong(Store& fs, const Text& target, const Text& body, Text& error) {
  const Text temp = target + ".tmp", backup = target + ".bak";
  if (fs.exists(backup) && !fs.exists(target) && !fs.rename(backup, target)) {
    error = "cannot recover previous song"; return false;
  }
  if (!fs.write(temp, body)) { fs.remove(temp); error = "temporary song write failed"; return false; }
  if (!fs.matches(temp, body)) {
    fs.remove(temp); error = "song readback failed; previous version retained"; return false;
  }
  if (fs.exists(backup) && !fs.remove(backup)) { error = "cannot clear previous backup"; return false; }
  const bool previous = fs.exists(target);
  if (previous && !fs.rename(target, backup)) { error = "cannot preserve previous song"; return false; }
  if (!fs.rename(temp, target)) {
    const bool restored = !previous || fs.rename(backup, target);
    error = restored ? "song commit failed; previous song restored" : "song commit and rollback failed; backup retained for recovery";
    return false;
  }
  if (previous) fs.remove(backup);
  return true;
}

template<class Store, class Text>
bool removeSong(Store& fs, const Text& target) {
  // Remove owned recovery records first. Otherwise a successful DELETE could
  // resurrect the song at the next boot from a leftover .bak file.
  for (const Text& path : {Text(target + ".bak"), Text(target + ".tmp"), target})
    if (fs.exists(path) && !fs.remove(path)) return false;
  return true;
}
}
