#pragma once
#include "PianoCore.h"
namespace piano {
inline bool builtinSong(const std::string& id, Song& song) {
  if (id != "demo-scale") return false;
  song = Song{}; song.id = id; song.title = "音阶练习（内置）"; song.bpm = 100; song.bpmProvided = true;
  for (int i = 1; i <= 7; ++i) song.notes.push_back({noteMidi(i, 0, false), .5});
  song.notes.push_back({72, 1}); song.notes.push_back({-1, .5});
  for (int i = 7; i >= 1; --i) song.notes.push_back({noteMidi(i, 0, false), .5});
  song.notes.back().beats = 2; return true;
}
}
