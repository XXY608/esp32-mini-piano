#pragma once
#include "PianoCore.h"
#include <algorithm>
#include <cmath>

namespace piano {
constexpr unsigned kNotesPerRow = 8;
struct ScoreCell {
  int pitch = -1, octave = 0;
  bool sharp = false, barBefore = false, continuation = false;
  double beats = 1;
  std::string lyric;
};
struct ScoreRow { ScoreCell cells[kNotesPerRow]; unsigned count = 0; uint32_t first = 0; };
struct ScorePage {
  ScoreRow rows[2]; int cursor = -1;
  std::string title, timeSignature;
  std::string currentLyric, nextLyric;
  uint32_t lyricFirst = 0;
  bool hasLyrics = false;
  double progress = 0;
};
class NotationLayout {
 public:
  void build(const Song& song) {
    tokens_.clear(); lines_.clear(); noteFirst_.clear(); lyricLines_.clear(); noteBeats_.clear();
    double beat = 0;
    unsigned count = 0; uint32_t rowStart = 0;
    for (size_t i = 0; i < song.notes.size(); ++i) {
      const auto& note = song.notes[i];
      noteBeats_.push_back(beat); beat += note.beats;
      if (note.lineBefore && count) { lines_.push_back({rowStart, count}); rowStart = tokens_.size(); count = 0; }
      noteFirst_.push_back(tokens_.size());
      const unsigned parts = note.beats > 1.5 ? static_cast<unsigned>(std::ceil(note.beats)) : 1;
      for (unsigned part = 0; part < parts; ++part) {
        if (count == kNotesPerRow) { lines_.push_back({rowStart, count}); rowStart = tokens_.size(); count = 0; }
        tokens_.push_back({static_cast<uint16_t>(i), static_cast<uint8_t>(part)}); ++count;
      }
    }
    if (count) lines_.push_back({rowStart, count});
    for (const auto& line : lines_) {
      std::string text;
      uint32_t start = line.first;
      for (unsigned col = 0; col < line.count; ++col) {
        const auto token = tokens_[line.first + col];
        if (token.part == 0 && !song.notes[token.note].lyric.empty()) {
          if (text.empty()) start = line.first + col;
          text += song.notes[token.note].lyric;
        }
      }
      if (!text.empty()) lyricLines_.push_back({start, std::move(text)});
    }
  }
  ScorePage page(const Song& song, const View& view) const {
    ScorePage result; result.hasLyrics=!lyricLines_.empty(); result.title = song.title; result.timeSignature = song.timeSignature;
    if (tokens_.empty() || song.notes.empty()) return result;
    const bool finished = view.index >= song.notes.size();
    const size_t noteIndex = std::min(view.index, song.notes.size() - 1);
    const auto& note = song.notes[noteIndex];
    const unsigned part = note.beats > 1.5 ? static_cast<unsigned>(std::min(std::floor(view.elapsedBeats), std::ceil(note.beats) - 1)) : 0;
    const uint32_t current = finished ? tokens_.size() - 1 : noteFirst_[noteIndex] + part;
    auto line = std::upper_bound(lines_.begin(), lines_.end(), current, [](uint32_t index, const Line& value) { return index < value.first; });
    if (line != lines_.begin()) --line;
    const size_t rowIndex = static_cast<size_t>(line - lines_.begin());
    result.cursor = finished ? -1 : static_cast<int>(current - line->first);
    auto lyric = std::upper_bound(lyricLines_.begin(), lyricLines_.end(), current,
        [](uint32_t first, const LyricLine& value) { return first < value.first; });
    if (lyric != lyricLines_.begin()) {
      const auto& currentLine = *(lyric - 1);
      result.currentLyric = currentLine.text; result.lyricFirst = currentLine.first;
    }
    if (lyric != lyricLines_.end()) result.nextLyric = lyric->text;
    if (!song.lyricCues.empty()) {
      result.hasLyrics = std::any_of(song.lyricCues.begin(), song.lyricCues.end(), [](const LyricCue& cue) { return !cue.text.empty(); });
      result.currentLyric.clear(); result.nextLyric.clear(); result.lyricFirst = 0;
      const double currentBeat = noteBeats_[noteIndex] + (finished ? song.notes[noteIndex].beats : view.elapsedBeats);
      const auto cue = std::upper_bound(song.lyricCues.begin(), song.lyricCues.end(), currentBeat,
          [](double position, const LyricCue& value) { return position < value.startBeat; });
      if (cue != song.lyricCues.begin()) {
        result.currentLyric = (cue - 1)->text;
        result.lyricFirst = static_cast<uint32_t>(cue - song.lyricCues.begin());
      }
      if (cue != song.lyricCues.end()) result.nextLyric = cue->text;
    }
    for (unsigned row = 0; row < 2 && rowIndex + row < lines_.size(); ++row) {
      const auto& source = lines_[rowIndex + row]; auto& target = result.rows[row]; target.first = source.first; target.count = source.count;
      for (unsigned col = 0; col < source.count; ++col) {
        const auto token = tokens_[source.first + col]; const auto& n = song.notes[token.note]; auto& cell = target.cells[col];
        spell(n, cell); cell.continuation = token.part > 0; cell.barBefore = n.barBefore && token.part == 0;
        cell.beats = n.beats > 1.5 ? std::min(1.0, n.beats - token.part) : n.beats;
        if (token.part == 0) cell.lyric = n.lyric;
      }
    }
    result.progress = finished ? 1 : std::min(1.0, (view.index + std::min(1.0, view.elapsedBeats / note.beats)) / song.notes.size());
    return result;
  }
 private:
  struct Token { uint16_t note; uint8_t part; };
  struct Line { uint32_t first; unsigned count; };
  struct LyricLine { uint32_t first; std::string text; };
  static void spell(const Note& note, ScoreCell& cell) {
    if (note.pitch >= 0) { cell.pitch = note.pitch; cell.octave = note.octave; cell.sharp = note.sharp; return; }
    if (note.midi < 0) { cell.pitch = 0; return; }
    for (int sharp = 0; sharp <= 1; ++sharp) for (int octave = -1; octave <= 1; ++octave) for (int pitch = 1; pitch <= 7; ++pitch)
      if (noteMidi(pitch, octave, sharp) == note.midi) { cell.pitch = pitch; cell.octave = octave; cell.sharp = sharp; return; }
  }
  std::vector<Token> tokens_;
  std::vector<Line> lines_;
  std::vector<uint32_t> noteFirst_;
  std::vector<double> noteBeats_;
  std::vector<LyricLine> lyricLines_;
};
}
