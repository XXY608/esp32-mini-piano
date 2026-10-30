#pragma once
#include <initializer_list>

namespace piano {
// The same transaction is tested with an in-memory fault-injecting store and
// used through an Arduino FS adapter. It does not claim flash crash atomicity.
template<class Store, class Text>
bool commitSong(Store& fs, const Text& target, const Text& body, Text& error) {
  const Text temp = target + ".tmp", backup = target + ".bak";
  if (fs.exists(backup) && !fs.exists(target) && !fs.rename(backup, target)) {
    error = "cannot recover previous song"; return false;
  }
  if (!fs.write(temp, body)) { fs.remove(temp); error = "temporary song write failed"; return false; }
  Text check;
  if (!fs.read(temp, check) || check != body) {
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
