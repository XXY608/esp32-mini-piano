#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string>
#include <vector>

namespace piano {
constexpr size_t kMaxNotes = 1024;
struct Note {
  int midi; double beats; // midi=-1 is rest
  int pitch = -1, octave = 0;
  bool sharp = false, barBefore = false, lineBefore = false;
  std::string lyric;
  Note(int value = -1, double duration = 1) : midi(value), beats(duration) {}
};
struct LyricCue { double startBeat; std::string text; };
struct Song {
  std::string id, title, timeSignature;
  int bpm = 120;
  bool bpmProvided = false;
  std::vector<Note> notes;
  std::vector<LyricCue> lyricCues;
};
bool safeId(const std::string& value);
int noteMidi(int degree, int octave, bool sharp);
double frequency(int midi);
bool parseSong(const char* data, size_t size, Song& output, std::string& error);
bool readSongId(const char* data, size_t size, std::string& id);
bool readVolume(const char* data, size_t size, int& volume);
struct Settings {
  int instrument = 0, bpm = 120, transpose = 0;
  bool metronome = false, repeat = false, showLyrics = false;
};
bool readSettings(const char* data, size_t size, Settings& value);
std::string encodeSong(const Song& song);

enum class Mode { Manual, Play, Teach, FreeScore, Simple, Rhythm };
struct View {
  Mode mode = Mode::Manual;
  int midi = -1, expected = -1, volume = 80, manualKey = -1;
  size_t index = 0, count = 0;
  bool low = false, sharp = false, high = false, finished = false;
  bool paused = false, click = false;
  uint16_t keyMask = 0;
  uint32_t trigger = 0;
  uint32_t songGeneration = 0;
  double elapsedBeats = 0;
  bool noteActive = false;
  Settings settings;
};

// All methods are called under the device's control mutex. No I/O in this class.
class Engine {
 public:
  bool start(Song song, Mode mode, uint32_t now);
  void stop();
  void setVolume(int value);
  void settings(const Settings& value, uint32_t now);
  bool pause(bool value, uint32_t now);
  void keys(uint16_t pressedMask, uint32_t now);
  void tick(uint32_t now);
  View view() const;
  const Song& song() const { return song_; }
 private:
  void next(uint32_t now);
  void skipInputRests();
  uint32_t duration() const;
  Song song_;
  Mode mode_ = Mode::Manual;
  size_t index_ = 0;
  size_t inputIndex_ = 0; // Simple practice accepts ahead of the playback cursor.
  uint32_t noteStart_ = 0;
  int volume_ = 80, manualKey_ = -1, manualMidi_ = -1;
  uint16_t mask_ = 0;
  bool finished_ = false, teachSounding_ = false, teachArmed_ = true;
  bool paused_ = false;
  uint32_t pauseStart_ = 0, clockStart_ = 0, trigger_ = 0, lastTick_ = 0;
  uint32_t songGeneration_ = 0;
  Settings settings_;
  std::vector<int> heldOrder_;
};

}
