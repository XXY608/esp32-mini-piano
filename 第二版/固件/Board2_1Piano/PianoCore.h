#pragma once
#include <stddef.h>
#include <stdint.h>
#include <array>
#include <string>
#include <vector>

namespace piano {
// Engine::keys adds simultaneous new presses in ascending key order.
inline int lastPressedDegree(uint16_t rising) {
  for (int degree = 7; degree >= 1; --degree)
    if (rising & (1u << (degree - 1))) return degree;
  return 0;
}
inline int midiDegree(int midi) {
  constexpr int degrees[]={1,1,2,2,3,4,4,5,5,6,6,7};
  return degrees[midi%12];
}
constexpr size_t kMaxNotes = 1024;
constexpr size_t kPerformanceVoices = 12;
constexpr uint32_t kPerformanceTicksPerBeat = 960;
constexpr int kMaxInstrument = 11;
struct PerformanceNote {
  uint32_t start, end;
  uint8_t midi, velocity, slot;
};
struct PerformanceVoice {
  int midi = -1;
  uint32_t trigger = 0;
  uint8_t velocity = 0;
};
struct PerformanceView {
  std::array<PerformanceVoice, kPerformanceVoices> voices{};
  uint32_t epoch = 0;
  bool enabled = false;
  bool pedal = false, paused = false;
  double positionTicks = 0;
  uint32_t endTick = 0;
  size_t started = 0, count = 0;
};
struct Note {
  int midi; double beats; // midi=-1 is rest
  int pitch = -1, octave = 0;
  bool sharp = false, barBefore = false, lineBefore = false, phraseBefore = false;
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
  std::vector<PerformanceNote> performance;
  std::vector<uint32_t> pedal;
};
bool safeId(const std::string& value);
int noteMidi(int degree, int octave, bool sharp);
double frequency(int midi);
const PerformanceNote* findPerformanceCue(const Song& song, uint32_t tick, int midi, uint32_t minimumStart=0, uint32_t tolerance=3);
bool parseSong(const char* data, size_t size, Song& output, std::string& error);
bool readSongId(const char* data, size_t size, std::string& id);
bool readVolume(const char* data, size_t size, int& volume);
struct Settings {
  int instrument = 3, bpm = 120, transpose = 0;
  bool metronome = false, repeat = false, showLyrics = false;
};
bool readSettings(const char* data, size_t size, Settings& value);
std::string encodeSong(const Song& song);

enum class Mode { Manual, Play, Teach, FreeScore, Simple, Rhythm };
enum class PlaybackArrangement { Melody, Original };
inline bool hasPlaybackArrangement(const Song& song, PlaybackArrangement arrangement) {
  return arrangement==PlaybackArrangement::Melody || !song.performance.empty();
}
struct View {
  Mode mode = Mode::Manual;
  int midi = -1, expected = -1, volume = 80, manualKey = -1;
  size_t index = 0, count = 0;
  bool low = false, sharp = false, high = false, finished = false;
  bool paused = false, click = false, originalPlayback = false;
  uint16_t keyMask = 0;
  uint32_t trigger = 0;
  uint32_t songGeneration = 0;
  double elapsedBeats = 0;
  bool noteActive = false;
  uint8_t velocity = 100;
  Settings settings;
};
inline bool isPlaybackArrangement(const View& view, PlaybackArrangement arrangement) {
  return view.mode==Mode::Play && view.originalPlayback==(arrangement==PlaybackArrangement::Original);
}

// All methods are called under the device's control mutex. No I/O in this class.
class Engine {
 public:
  bool start(Song song, Mode mode, uint32_t now, PlaybackArrangement arrangement=PlaybackArrangement::Original);
  void stop();
  void setVolume(int value);
  void settings(const Settings& value, uint32_t now);
  bool pause(bool value, uint32_t now);
  void keys(uint16_t pressedMask, uint32_t now);
  void tick(uint32_t now);
  bool continueHeld(uint32_t now);
  void retriggerHeld();
  bool prepareEarly(int degree);
  View view(bool inputCursor=true) const;
  int simpleSoundingNote() const { return mode_==Mode::Simple && teachSounding_ ? static_cast<int>(index_) : -1; }
  PerformanceView performanceView() const;
  const Song& song() const { return song_; }
 private:
  void next(uint32_t now);
  void skipInputRests();
  uint32_t duration() const;
  void tickPerformance(uint32_t now);
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
  std::vector<uint8_t> melodyVelocity_;
  std::vector<int> heldOrder_;
  std::array<uint16_t, kPerformanceVoices> performanceActive_{};
  size_t nextPerformanceNote_ = 0;
  size_t nextPedal_ = 0;
  bool pedalDown_ = false;
  uint32_t performanceClock_ = 0, performanceEnd_ = 0, performanceEpoch_ = 0;
  uint64_t performanceUnits_ = 0;
  double displayBeat_ = 0;
};

}
