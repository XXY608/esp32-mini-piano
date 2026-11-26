#include "PianoCore.h"
#include "cJSON.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <memory>

namespace piano {
namespace {
using Json = std::unique_ptr<cJSON, decltype(&cJSON_Delete)>;
Json document(const char* data, size_t size) {
  if (!data || !size || size > 120 * 1024 || std::memchr(data, 0, size)) return Json(nullptr, cJSON_Delete);
  // cJSON decodes U+0000 to a C string terminator. Reject it explicitly rather
  // than silently accepting a truncated id/title. The payload remains bounded.
  const std::string input(data, size);
  if (input.find("\\u0000") != std::string::npos) return Json(nullptr, cJSON_Delete);
  unsigned depth = 0; bool inString = false, escaped = false;
  for (char c : input) {
    if (inString) {
      if (escaped) escaped = false;
      else if (c == '\\') escaped = true;
      else if (c == '"') inString = false;
    } else if (c == '"') inString = true;
    else if (c == '{' || c == '[') { if (++depth > 8) return Json(nullptr, cJSON_Delete); }
    else if (c == '}' || c == ']') { if (!depth) return Json(nullptr, cJSON_Delete); --depth; }
  }
  if (inString || depth) return Json(nullptr, cJSON_Delete);
  const char* end = nullptr;
  Json result(cJSON_ParseWithLengthOpts(input.c_str(), input.size() + 1, &end, true), cJSON_Delete);
  if (!result || !cJSON_IsObject(result.get())) return Json(nullptr, cJSON_Delete);
  return result;
}
bool unique(const cJSON* object) {
  for (const cJSON* a = object->child; a; a = a->next)
    for (const cJSON* b = a->next; b; b = b->next)
      if (a->string && b->string && std::strcmp(a->string, b->string) == 0) return false;
  return true;
}
const cJSON* field(const cJSON* object, const char* key) { return cJSON_GetObjectItemCaseSensitive(object, key); }
bool integer(const cJSON* value, int lo, int hi, int& result) {
  if (!cJSON_IsNumber(value) || !std::isfinite(value->valuedouble) || std::floor(value->valuedouble) != value->valuedouble || value->valuedouble < lo || value->valuedouble > hi) return false;
  result = static_cast<int>(value->valuedouble); return true;
}
bool stringValue(const cJSON* value, std::string& out, size_t limit) {
  if (!cJSON_IsString(value) || !value->valuestring) return false;
  out = value->valuestring; return out.size() <= limit;
}
constexpr char base64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
constexpr size_t performanceRecordSize = 10, maxPerformanceNotes = 4096;
constexpr uint32_t maxPerformanceTick = 65536 * kPerformanceTicksPerBeat;

bool decodePerformance(const cJSON* value, std::vector<PerformanceNote>& notes) {
  if (!cJSON_IsString(value) || !value->valuestring) return false;
  const char* source = value->valuestring;
  const size_t length = std::strlen(source);
  if (!length || length % 4 || length > ((maxPerformanceNotes * performanceRecordSize + 2) / 3) * 4) return false;
  const size_t padding = (source[length - 1] == '=') + (source[length - 2] == '=');
  const size_t bytes = length / 4 * 3 - padding;
  if (!bytes || bytes % performanceRecordSize || bytes / performanceRecordSize > maxPerformanceNotes) return false;
  notes.reserve(bytes / performanceRecordSize);
  std::array<uint32_t, kPerformanceVoices> activeUntil{};
  uint8_t record[performanceRecordSize];
  size_t used = 0;
  unsigned bits = 0, buffer = 0;
  for (size_t i = 0; i < length - padding; ++i) {
    const char* digit = std::strchr(base64, source[i]);
    if (!digit) return false;
    buffer = (buffer << 6) | static_cast<unsigned>(digit - base64);
    bits += 6;
    if (bits < 8) continue;
    bits -= 8;
    record[used++] = static_cast<uint8_t>(buffer >> bits);
    if (used != performanceRecordSize) continue;
    auto word = [&](int first) {
      uint32_t result = 0;
      for (int j = 0; j < 4; ++j) result |= static_cast<uint32_t>(record[first + j]) << (j * 8);
      return result;
    };
    PerformanceNote note{word(0), word(4), record[8], record[9], 0};
    if ((!notes.empty() && note.start < notes.back().start) || note.start >= note.end || note.end > maxPerformanceTick ||
        note.midi < 21 || note.midi > 108 || !note.velocity || note.velocity > 127) return false;
    while (note.slot < activeUntil.size() && activeUntil[note.slot] > note.start) ++note.slot;
    if (note.slot == activeUntil.size()) return false;
    activeUntil[note.slot] = note.end;
    notes.push_back(note);
    used = 0;
  }
  return notes.size() == bytes / performanceRecordSize && !(buffer & ((1u << bits) - 1));
}

std::string encodePerformance(const std::vector<PerformanceNote>& notes) {
  std::string output;
  output.reserve(((notes.size() * performanceRecordSize + 2) / 3) * 4);
  unsigned bits = 0, buffer = 0;
  auto append = [&](uint8_t byte) {
    buffer = (buffer << 8) | byte;
    bits += 8;
    while (bits >= 6) { bits -= 6; output += base64[(buffer >> bits) & 63u]; }
  };
  for (const auto& note : notes) {
    for (uint32_t tick : {note.start, note.end})
      for (int j = 0; j < 4; ++j) append(static_cast<uint8_t>(tick >> (8 * j)));
    append(note.midi);
    append(note.velocity);
  }
  if (bits) output += base64[(buffer << (6 - bits)) & 63u];
  while (output.size() % 4) output += '=';
  return output;
}
}

bool safeId(const std::string& id) {
  if (id.empty() || id.size() > 64) return false;
  for (char c : id) if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
  return true;
}
int noteMidi(int degree, int octave, bool sharp) {
  static const int semitones[] = {0, 2, 4, 5, 7, 9, 11};
  return degree >= 1 && degree <= 7 ? 60 + semitones[degree - 1] + 12 * octave + (sharp ? 1 : 0) : -1;
}
double frequency(int midi) { return midi < 0 ? 0 : 440.0 * std::pow(2.0, (midi - 69) / 12.0); }
bool parseSong(const char* data, size_t size, Song& output, std::string& error) {
  auto json = document(data, size);
  auto fail = [&](const char* message) { error = message; return false; };
  if (!json || !unique(json.get())) return fail("invalid JSON object or duplicate fields");
  Song candidate;
  if (!stringValue(field(json.get(), "id"), candidate.id, 64) || !safeId(candidate.id)) return fail("invalid song id");
  if (!stringValue(field(json.get(), "title"), candidate.title, 240) || candidate.title.empty()) return fail("title required (up to 240 UTF-8 bytes)");
  const auto* bpm = field(json.get(), "bpm");
  if (bpm && !cJSON_IsNull(bpm) && !integer(bpm, 20, 400, candidate.bpm)) return fail("bpm must be null or an integer from 20 to 400");
  candidate.bpmProvided = bpm && !cJSON_IsNull(bpm);
  const auto* meter = field(json.get(), "timeSignature");
  if (meter && !cJSON_IsNull(meter)) {
    if (!stringValue(meter, candidate.timeSignature, 5)) return fail("invalid timeSignature");
    unsigned numerator = 0, denominator = 0; char extra = 0;
    if (std::sscanf(candidate.timeSignature.c_str(), "%u/%u%c", &numerator, &denominator, &extra) != 2 || numerator < 1 || numerator > 32 ||
        !(denominator == 1 || denominator == 2 || denominator == 4 || denominator == 8 || denominator == 16 || denominator == 32)) return fail("invalid timeSignature");
  }
  const auto* notes = field(json.get(), "notes");
  if (!cJSON_IsArray(notes)) return fail("notes array required");
  const int count = cJSON_GetArraySize(notes);
  if (count < 1 || count > static_cast<int>(kMaxNotes)) return fail("song must have 1..1024 notes");
  candidate.notes.reserve(count);
  for (const cJSON* n = notes->child; n; n = n->next) {
    if (!cJSON_IsObject(n) || !unique(n)) return fail("invalid or duplicate note fields");
    int degree, octave;
    if (!integer(field(n, "pitch"), 0, 7, degree) || !integer(field(n, "octave"), -1, 1, octave)) return fail("pitch must be 0..7 and octave -1..1");
    const auto* sharp = field(n, "sharp");
    if (!cJSON_IsBool(sharp)) return fail("sharp must be boolean");
    const auto* beats = field(n, "durationBeats");
    if (!cJSON_IsNumber(beats) || !std::isfinite(beats->valuedouble) || beats->valuedouble < 1.0 / 64 || beats->valuedouble > 64) return fail("durationBeats must be 1/64..64");
    candidate.notes.push_back({noteMidi(degree, octave, cJSON_IsTrue(sharp)), beats->valuedouble});
    auto& note = candidate.notes.back(); note.pitch = degree; note.octave = octave; note.sharp = cJSON_IsTrue(sharp);
    const auto* bar = field(n, "barBefore"); const auto* line = field(n, "lineBefore");
    const auto* phrase = field(n, "phraseBefore");
    if ((bar && !cJSON_IsBool(bar)) || (line && !cJSON_IsBool(line)) || (phrase && !cJSON_IsBool(phrase))) return fail("barBefore/lineBefore/phraseBefore must be boolean");
    note.barBefore = bar && cJSON_IsTrue(bar); note.lineBefore = line && cJSON_IsTrue(line); note.phraseBefore = phrase && cJSON_IsTrue(phrase);
    const auto* lyric = field(n, "lyric");
    if (lyric) {
      if (!stringValue(lyric, note.lyric, 24)) return fail("lyric must be a string up to 24 UTF-8 bytes");
      for (unsigned char c : note.lyric) if (c < 32 || c == 127) return fail("lyric must be single-line text");
      if (degree == 0 && !note.lyric.empty()) return fail("rests cannot carry lyrics");
    }
  }
  const auto* cues = field(json.get(), "lyricCues");
  const auto* performance = field(json.get(), "polyphony");
  if (performance && !decodePerformance(performance, candidate.performance))
    return fail("invalid polyphonic timeline or more than 12 simultaneous notes");
  if (const auto* pedal = field(json.get(), "pedal")) {
    if (candidate.performance.empty() || !cJSON_IsArray(pedal) || cJSON_GetArraySize(pedal) > 512)
      return fail("invalid sustain pedal timeline");
    for (auto* item = pedal->child; item; item = item->next) {
      int step;
      if (!integer(item, 0, maxPerformanceTick * 2 + 1, step) ||
          (!candidate.pedal.empty() && static_cast<uint32_t>(step / 2) < candidate.pedal.back() / 2))
        return fail("invalid sustain pedal event");
      candidate.pedal.push_back(static_cast<uint32_t>(step));
    }
  }
  if (cues && !cJSON_IsNull(cues)) {
    if (!cJSON_IsArray(cues) || cJSON_GetArraySize(cues) > 256) return fail("lyricCues must have up to 256 entries");
    double total = 0, previous = -1; size_t bytes = 0;
    for (const auto& note : candidate.notes) total += note.beats;
    for (const cJSON* item = cues->child; item; item = item->next) {
      if (!cJSON_IsObject(item) || !unique(item)) return fail("invalid lyric cue fields");
      const auto* beat = field(item, "startBeat");
      LyricCue cue;
      if (!cJSON_IsNumber(beat) || !std::isfinite(beat->valuedouble) || beat->valuedouble < 0 ||
          beat->valuedouble >= total || beat->valuedouble <= previous) return fail("lyric cue beats must increase within the song");
      cue.startBeat = beat->valuedouble; previous = cue.startBeat;
      if (!stringValue(field(item, "text"), cue.text, 512)) return fail("lyric cue text exceeds 512 UTF-8 bytes");
      for (unsigned char c : cue.text) if (c < 32 || c == 127) return fail("lyric cue must be single-line text");
      bytes += cue.text.size(); if (bytes > 32768) return fail("lyric cue text exceeds 32KB");
      candidate.lyricCues.push_back(std::move(cue));
    }
  }
  output = std::move(candidate); error.clear(); return true;
}
bool readSongId(const char* data, size_t size, std::string& id) {
  auto json = document(data, size);
  if (!json || !unique(json.get())) return false;
  const auto* value = field(json.get(), "id");
  if (!value) value = field(json.get(), "songId");
  return stringValue(value, id, 64) && safeId(id);
}
bool readVolume(const char* data, size_t size, int& volume) {
  auto json = document(data, size);
  return json && unique(json.get()) && integer(field(json.get(), "volumePercent"), 0, 100, volume);
}
bool readSettings(const char* data, size_t size, Settings& value) {
  auto json = document(data, size);
  if (!json || !unique(json.get())) return false;
  Settings candidate = value;
  for (const auto* item = json->child; item; item = item->next) {
    if (!item->string) return false;
    const std::string name = item->string;
    if (name == "instrument") { if (!integer(item, 0, kMaxInstrument, candidate.instrument)) return false; }
    else if (name == "bpm") { if (!integer(item, 20, 400, candidate.bpm)) return false; }
    else if (name == "transpose") { if (!integer(item, -12, 12, candidate.transpose)) return false; }
    else if (name == "metronome" || name == "repeat" || name == "showLyrics") {
      if (!cJSON_IsBool(item)) return false;
      (name == "metronome" ? candidate.metronome : name == "repeat" ? candidate.repeat : candidate.showLyrics) = cJSON_IsTrue(item);
    } else return false;
  }
  value = candidate; return true;
}
std::string encodeSong(const Song& song) {
  Json root(cJSON_CreateObject(), cJSON_Delete);
  if (!root) return "";
  cJSON_AddStringToObject(root.get(), "id", song.id.c_str());
  cJSON_AddStringToObject(root.get(), "title", song.title.c_str());
  cJSON_AddNumberToObject(root.get(), "bpm", song.bpm);
  if (!song.timeSignature.empty()) cJSON_AddStringToObject(root.get(), "timeSignature", song.timeSignature.c_str());
  if (!song.performance.empty()) {
    const std::string timeline = encodePerformance(song.performance);
    if (!cJSON_AddStringToObject(root.get(), "polyphony", timeline.c_str())) return "";
  }
  if (!song.pedal.empty()) {
    auto* pedal = cJSON_AddArrayToObject(root.get(), "pedal"); if (!pedal) return "";
    for (uint32_t step : song.pedal) {
      auto* value = cJSON_CreateNumber(step); if (!value) return "";
      cJSON_AddItemToArray(pedal, value);
    }
  }
  auto* notes = cJSON_AddArrayToObject(root.get(), "notes");
  if (!notes) return "";
  for (const auto& note : song.notes) {
    auto* item = cJSON_CreateObject(); if (!item) return "";
    cJSON_AddItemToArray(notes, item);
    int pitch = 0, octave = 0; bool sharp = false, found = note.midi < 0;
    if (note.pitch >= 0 && note.pitch <= 7 && noteMidi(note.pitch, note.octave, note.sharp) == note.midi) {
      pitch = note.pitch; octave = note.octave; sharp = note.sharp; found = true;
    }
    for (int s = 0; s <= 1 && !found; ++s) for (int o = -1; o <= 1 && !found; ++o)
      for (int p = 1; p <= 7 && !found; ++p) if (noteMidi(p, o, s) == note.midi) { pitch = p; octave = o; sharp = s; found = true; }
    if (!found) return "";
    cJSON_AddNumberToObject(item, "pitch", pitch); cJSON_AddNumberToObject(item, "octave", octave);
    cJSON_AddBoolToObject(item, "sharp", sharp); cJSON_AddNumberToObject(item, "durationBeats", note.beats);
    if (note.barBefore) cJSON_AddBoolToObject(item, "barBefore", true);
    if (note.lineBefore) cJSON_AddBoolToObject(item, "lineBefore", true);
    if (note.phraseBefore) cJSON_AddBoolToObject(item, "phraseBefore", true);
    if (!note.lyric.empty()) cJSON_AddStringToObject(item, "lyric", note.lyric.c_str());
  }
  if (!song.lyricCues.empty()) {
    auto* cues = cJSON_AddArrayToObject(root.get(), "lyricCues"); if (!cues) return "";
    for (const auto& cue : song.lyricCues) {
      auto* item = cJSON_CreateObject(); if (!item) return "";
      cJSON_AddItemToArray(cues, item);
      cJSON_AddNumberToObject(item, "startBeat", cue.startBeat);
      cJSON_AddStringToObject(item, "text", cue.text.c_str());
    }
  }
  char* text = cJSON_PrintUnformatted(root.get()); if (!text) return "";
  std::string out(text); cJSON_free(text); return out;
}

const PerformanceNote* findPerformanceCue(const Song& song, uint32_t tick, int midi, uint32_t minimumStart, uint32_t tolerance) {
  const auto& source=song.performance;
  const uint32_t first=std::max(minimumStart,tick>tolerance?tick-tolerance:0);
  const auto lower=std::lower_bound(source.begin(),source.end(),first,
    [](const PerformanceNote& note,uint32_t time){return note.start<time;});
  const PerformanceNote* best=nullptr;
  int bestTime=tolerance+1,bestPitch=128;
  for (auto it=lower;it!=source.end() && it->start<=tick+tolerance;++it) {
    if (it->midi%12!=midi%12) continue;
    const int time=std::abs(static_cast<int>(it->start)-static_cast<int>(tick));
    const int pitch=std::abs(it->midi-midi);
    if (time<bestTime || (time==bestTime && pitch<bestPitch)) {best=&*it;bestTime=time;bestPitch=pitch;}
  }
  return best;
}

bool Engine::start(Song song, Mode mode, uint32_t now, PlaybackArrangement arrangement) {
  if (song.notes.empty() || song.notes.size() > kMaxNotes || song.bpm < 20 || song.bpm > 400 || mode == Mode::Manual) return false;
  for(auto& n:song.notes) if(n.pitch<0 && n.midi>=0) {
    for(int sharp=0;sharp<=1 && n.pitch<0;++sharp) for(int degree=1;degree<=7 && n.pitch<0;++degree)
      if((noteMidi(degree,0,sharp)%12)==n.midi%12) { n.pitch=degree; n.sharp=sharp!=0; }
  }
  song_ = std::move(song); mode_ = mode; index_ = 0; noteStart_ = now;
  inputIndex_ = 0; skipInputRests();
  finished_ = false; teachSounding_ = false; teachArmed_ = (mask_ & 0x7f) == 0;
  paused_ = false; clockStart_ = now; lastTick_ = now; settings_.bpm = song_.bpm; ++trigger_;
  performanceActive_.fill(0);
  performanceUnits_ = 0;
  displayBeat_ = 0;
  nextPerformanceNote_ = performanceEnd_ = 0;
  nextPedal_ = 0; pedalDown_ = false;
  performanceClock_ = now;
  ++performanceEpoch_;
  melodyVelocity_.clear();
  if (mode_ == Mode::Play && arrangement==PlaybackArrangement::Original && !song_.performance.empty()) {
    for (const auto& note : song_.performance) performanceEnd_ = std::max(performanceEnd_, note.end);
    tickPerformance(now);
  } else if (mode_==Mode::Simple || mode_==Mode::Play) {
    melodyVelocity_.assign(song_.notes.size(),72);
    double beat=0;
    uint32_t nextAttack=0;
    // A written melody can sit slightly ahead of or behind the rendered attack.
    const uint32_t tolerance=std::min<uint32_t>(kPerformanceTicksPerBeat/12,
      song_.bpm*kPerformanceTicksPerBeat*45/60000);
    for(size_t i=0;i<song_.notes.size();++i) {
      const auto& note=song_.notes[i];
      if(note.midi>=0) {
        const auto* match=findPerformanceCue(song_,std::lround(beat*kPerformanceTicksPerBeat),note.midi,nextAttack,tolerance);
        if(match) { melodyVelocity_[i]=match->velocity; nextAttack=match->start+1; }
      }
      beat+=note.beats;
    }
  }
  ++songGeneration_;
  return true;
}
void Engine::stop() { mode_ = Mode::Manual; teachSounding_ = false; finished_ = false; paused_ = false; ++trigger_; }
void Engine::setVolume(int value) { volume_ = std::max(0, std::min(100, value)); }
bool Engine::pause(bool value, uint32_t now) {
  if (mode_ == Mode::Manual) return false;
  if (paused_ == value) return true;
  if (performanceEnd_ && mode_ == Mode::Play) {
    if (value) tickPerformance(now);
    if (mode_ == Mode::Manual) return false;
    performanceClock_ = now;
  }
  if (value) pauseStart_ = now;
  else { noteStart_ += now - pauseStart_; clockStart_ += now - pauseStart_; }
  paused_ = value; ++trigger_; return true;
}
void Engine::settings(const Settings& value, uint32_t now) {
  // Preserve fractional position of the current note when changing tempo.
  const uint32_t effectiveNow = paused_ ? pauseStart_ : now;
  if (mode_ == Mode::Play && performanceEnd_ && !paused_ && value.bpm != settings_.bpm) tickPerformance(now);
  if (mode_ != Mode::Manual && value.bpm != settings_.bpm)
    noteStart_ = effectiveNow - static_cast<uint32_t>(static_cast<double>(effectiveNow - noteStart_) * settings_.bpm / value.bpm);
  if (value.bpm != settings_.bpm || value.metronome != settings_.metronome) clockStart_ = effectiveNow;
  settings_ = value;
}
uint32_t Engine::duration() const {
  return static_cast<uint32_t>(std::max(1.0, std::round(song_.notes[index_].beats * 60000.0 / settings_.bpm)));
}
void Engine::keys(uint16_t mask, uint32_t now) {
  const uint16_t changed = mask ^ mask_;
  const uint16_t rising = changed & mask & 0x7f;
  for (int i = 0; i < 7; ++i) {
    if (!(mask & (1u << i))) heldOrder_.erase(std::remove(heldOrder_.begin(), heldOrder_.end(), i), heldOrder_.end());
    if (rising & (1u << i)) heldOrder_.push_back(i);
  }
  mask_ = mask;
  manualKey_ = heldOrder_.empty() ? -1 : heldOrder_.back();
  const int octave = ((mask & (1u << 9)) ? 1 : 0) - ((mask & (1u << 7)) ? 1 : 0);
  manualMidi_ = noteMidi(manualKey_ + 1, octave, mask & (1u << 8));
  if (mode_ != Mode::Simple && changed && (rising || ((changed & 0x380) && mode_ != Mode::Rhythm))) ++trigger_;
  if (mode_ == Mode::Play && rising) stop(); // deliberate manual takeover
  if (mode_ == Mode::Manual && rising) finished_ = false;
  if (mode_ == Mode::Simple && !paused_ && inputIndex_ < song_.notes.size() &&
      song_.notes[inputIndex_].pitch > 0 && (rising & (1u << (song_.notes[inputIndex_].pitch-1)))) {
    ++inputIndex_; skipInputRests();
    if (!teachSounding_ && song_.notes[index_].midi >= 0) {
      teachSounding_ = true; noteStart_ = now; lastTick_ = now; ++trigger_;
    }
  }
  if ((mode_ == Mode::Teach || mode_ == Mode::Rhythm || mode_ == Mode::FreeScore) && !paused_ && index_ < song_.notes.size()) {
    if (!(mask & 0x7f)) teachArmed_ = true;
    if (!teachSounding_ && teachArmed_ && (rising || ((changed & 0x380) && mode_ != Mode::Rhythm)) && (mode_ == Mode::Rhythm ? manualKey_ + 1 == song_.notes[index_].pitch : manualMidi_ == song_.notes[index_].midi) && manualMidi_ >= 0) {
      teachSounding_ = true; teachArmed_ = false; noteStart_ = now;
      ++trigger_;
    }
  }
}
// The simulator retriggers each contiguous note while its key remains held.
bool Engine::continueHeld(uint32_t now) {
  if (mode_ != Mode::FreeScore || paused_ || teachSounding_ || index_ >= song_.notes.size() || manualMidi_ != song_.notes[index_].midi) return false;
  teachSounding_ = true; teachArmed_ = false; noteStart_ = now; ++trigger_; return true;
}
void Engine::retriggerHeld() { if (manualMidi_ >= 0) ++trigger_; }
// In the Windows game simulator a correct next-key press can interrupt the
// current note. Rests and wrong notes never advance the score.
bool Engine::prepareEarly(int degree) {
  if (mode_ != Mode::FreeScore || paused_ || index_ >= song_.notes.size()) return false;
  if (teachSounding_) {
    if (index_ + 1 >= song_.notes.size() || song_.notes[index_ + 1].pitch != degree || song_.notes[index_ + 1].midi < 0) return false;
    ++index_; teachSounding_ = false; teachArmed_ = true; return true;
  }
  if (!teachArmed_ && song_.notes[index_].pitch == degree && song_.notes[index_].midi >= 0) { teachArmed_ = true; return true; }
  return false;
}
void Engine::skipInputRests() {
  while (inputIndex_ < song_.notes.size() && song_.notes[inputIndex_].midi < 0) ++inputIndex_;
}
void Engine::next(uint32_t now) {
  ++index_; teachSounding_ = false;
  teachArmed_ = (mask_ & 0x7f) == 0;
  noteStart_ = now;
  if (mode_ != Mode::FreeScore) ++trigger_;
  if (index_ >= song_.notes.size()) {
    if (settings_.repeat) { index_ = 0; inputIndex_ = 0; skipInputRests(); }
    else { mode_ = Mode::Manual; finished_ = true; }
  }
  if (mode_ == Mode::Simple && index_ < inputIndex_ && index_ < song_.notes.size() && song_.notes[index_].midi >= 0) teachSounding_ = true;
}
void Engine::tick(uint32_t now) {
  lastTick_ = now;
  if (mode_ == Mode::Play && performanceEnd_ && !paused_) { tickPerformance(now); return; }
  if (paused_ || mode_ == Mode::Manual || index_ >= song_.notes.size()) return;
  if (mode_ == Mode::Play) {
    // Keep absolute musical time, including rollover; never stretch every note
    // by the control-loop polling interval. Bounded by the song's note count.
    size_t budget = 2048;
    while (budget-- && mode_ == Mode::Play && static_cast<uint32_t>(now - noteStart_) >= duration()) {
      const uint32_t due = noteStart_ + duration(); next(due);
    }
  } else if (mode_ == Mode::Simple) {
    size_t budget = 2048;
    while (budget-- && mode_ == Mode::Simple && (song_.notes[index_].midi < 0 || teachSounding_) && static_cast<uint32_t>(now - noteStart_) >= duration()) {
      const uint32_t due = noteStart_ + duration(); next(due);
    }
  } else if (song_.notes[index_].midi < 0 || teachSounding_) {
    if (static_cast<uint32_t>(now - noteStart_) >= duration()) next(now);
  }
}

void Engine::tickPerformance(uint32_t now) {
  constexpr uint64_t unitsPerTick = 60000;
  const uint64_t end = performanceEnd_ * unitsPerTick;
  performanceUnits_ += uint64_t(now - performanceClock_) * settings_.bpm * kPerformanceTicksPerBeat;
  performanceClock_ = now;
  if (performanceUnits_ >= end) {
    performanceActive_.fill(0);
    if (!settings_.repeat) {
      index_ = song_.notes.size(); mode_ = Mode::Manual; finished_ = true; ++trigger_;
      return;
    }
    performanceUnits_ %= end;
    nextPerformanceNote_ = index_ = 0;
    nextPedal_ = 0; pedalDown_ = false;
    displayBeat_ = 0;
    ++performanceEpoch_;
  }
  const uint32_t tick = static_cast<uint32_t>(performanceUnits_ / unitsPerTick);
  while (nextPedal_ < song_.pedal.size() && song_.pedal[nextPedal_] / 2 <= tick)
    pedalDown_ = (song_.pedal[nextPedal_++] & 1u) != 0;
  for (auto& active : performanceActive_)
    if (active && song_.performance[active - 1].end <= tick) active = 0;
  while (nextPerformanceNote_ < song_.performance.size() && song_.performance[nextPerformanceNote_].start <= tick) {
    const auto& note = song_.performance[nextPerformanceNote_++];
    if (note.end > tick) performanceActive_[note.slot] = static_cast<uint16_t>(nextPerformanceNote_);
  }
  const double beat = performanceUnits_ / (60000.0 * kPerformanceTicksPerBeat);
  while (index_ + 1 < song_.notes.size() && displayBeat_ + song_.notes[index_].beats <= beat) {
    displayBeat_ += song_.notes[index_++].beats;
    ++trigger_;
  }
}

PerformanceView Engine::performanceView() const {
  PerformanceView result;
  result.enabled = mode_ == Mode::Play && performanceEnd_ != 0;
  result.epoch = performanceEpoch_;
  result.pedal = pedalDown_;
  result.paused = paused_;
  result.endTick = performanceEnd_;
  result.positionTicks = std::min<double>(performanceEnd_, performanceUnits_ / 60000.0);
  result.count = song_.performance.size();
  result.started = finished_ && performanceEnd_ ? result.count : nextPerformanceNote_;
  if (!result.enabled || paused_) return result;
  for (size_t slot = 0; slot < performanceActive_.size(); ++slot) {
    const auto active = performanceActive_[slot];
    if (!active) continue;
    const auto& note = song_.performance[active - 1];
    auto& voice = result.voices[slot];
    voice.midi = note.midi + settings_.transpose;
    voice.trigger = active;
    voice.velocity = note.velocity;
  }
  return result;
}
View Engine::view(bool inputCursor) const {
  View v; v.mode = mode_; v.index = index_; v.count = song_.notes.size(); v.volume = volume_;
  v.manualKey = manualKey_; v.finished = finished_;
  v.keyMask = mask_; v.paused = paused_; v.settings = settings_; v.trigger = trigger_;
  v.songGeneration = songGeneration_;
  v.originalPlayback = mode_==Mode::Play && performanceEnd_!=0;
  const auto beatPhase = (static_cast<uint64_t>(lastTick_ - clockStart_) * settings_.bpm) % 60000;
  v.click = settings_.metronome && !paused_ && beatPhase < static_cast<unsigned>(22 * settings_.bpm);
  v.low = mask_ & (1u << 7); v.sharp = mask_ & (1u << 8); v.high = mask_ & (1u << 9);
  if (mode_ == Mode::Manual) { v.midi = finished_ ? -1 : manualMidi_; v.noteActive = v.midi >= 0; }
  else if (index_ < song_.notes.size()) {
    if (index_ < melodyVelocity_.size()) v.velocity=melodyVelocity_[index_];
    v.expected = song_.notes[index_].midi;
    v.noteActive = !paused_ && (mode_ == Mode::Play || teachSounding_ || v.expected < 0);
    if (mode_ == Mode::Play || teachSounding_ || v.expected < 0) {
      const uint32_t clock = paused_ ? pauseStart_ : lastTick_;
      const double elapsed = mode_ == Mode::Play && performanceEnd_ ? performanceUnits_ / (60000.0 * kPerformanceTicksPerBeat) - displayBeat_
        : static_cast<uint32_t>(clock - noteStart_) * settings_.bpm / 60000.0;
      v.elapsedBeats = std::min(song_.notes[index_].beats, elapsed);
    }
    v.midi = mode_ == Mode::FreeScore ? manualMidi_ : mode_ == Mode::Play || teachSounding_ ? v.expected : -1;
    if (paused_) v.midi = -1;
    // Separate repeated attacks, but let different consecutive pitches use their full time.
    if (((mode_==Mode::Play && !performanceEnd_) || mode_==Mode::Simple) && !paused_ && v.midi>=0) {
      const bool connected=index_+1<song_.notes.size() && song_.notes[index_+1].midi>=0 &&
        song_.notes[index_+1].midi!=v.midi;
      if (!connected && duration()>30 && lastTick_-noteStart_>=duration()-12) v.midi=-1;
    }
  }
  if (mode_ == Mode::Simple && inputCursor) {
    v.index = inputIndex_;
    v.expected = inputIndex_ < song_.notes.size() ? song_.notes[inputIndex_].midi : -1;
    if (inputIndex_ != index_) { v.noteActive = false; v.elapsedBeats = 0; }
  }
  if (v.midi >= 0) v.midi += settings_.transpose;
  if (v.expected >= 0) v.expected += settings_.transpose;
  return v;
}

}
