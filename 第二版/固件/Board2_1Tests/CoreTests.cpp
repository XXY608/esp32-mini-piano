#include "PianoCore.h"
#include "AudioSynth.h"
#include "SampleVoice.h"
#include "SampleUpload.h"
#include <fstream>
#include <iterator>
#include "Board2_1Config.h"
#include "StorageTransaction.h"
#include "LocalControls.h"
#include "BuiltinSongs.h"
#include "ChineseText.h"
#include "DisplayText.h"
#include "ScoreScreen.h"
#include "GameScreen.h"
#include "PerformanceGame.h"
#include "GuidedGame.h"
#include "GuidedPerformance.h"
#include "ChordPractice.h"
#include "GameAssist.h"
#include "GameFlow.h"
#include "GameHoldBonus.h"
#include "ManualVoices.h"
#include "ManualMixer.h"
#include "PerformanceMixer.h"
#include "MenuScreen.h"
#include "ScreenCanvas.h"
#include "FrameSurface.h"
#include "AudioEvents.h"
#include "AudioOutput.h"
#include "cJSON.h"
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <string>
#include <vector>
#include <map>
#include <set>

static int checks = 0, groups = 0;
#define CHECK(x) do { ++checks; if (!(x)) { std::fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); std::exit(1); } } while (0)
static std::string payload(const std::string& notes, const std::string& bpm="120") {
  return "{\"id\":\"song-1\",\"title\":\"\\u77e5\\u6211\",\"bpm\":"+bpm+",\"notes\":["+notes+"]}";
}
static std::string note(int pitch=1, double beats=1, int octave=0, bool sharp=false) {
  return "{\"pitch\":"+std::to_string(pitch)+",\"octave\":"+std::to_string(octave)+",\"sharp\":"+(sharp?"true":"false")+",\"durationBeats\":"+std::to_string(beats)+"}";
}
static piano::Song song(std::vector<piano::Note> notes) { piano::Song s; s.id="song-1"; s.bpm=120; s.notes=std::move(notes); return s; }
static void group(const char* name, const std::function<void()>& body) { body(); ++groups; std::printf("PASS %s\n",name); }
struct SongFileReader {
  const std::string& bytes;
  size_t offset=0, limit=std::string::npos;
  size_t size() const { return bytes.size(); }
  size_t read(uint8_t* buffer, size_t count) {
    count=std::min(count,std::min(size(),limit)-offset);
    std::memcpy(buffer,bytes.data()+offset,count);
    offset+=count;
    return count;
  }
};
struct ReservedSongText {
  std::string bytes;
  size_t reserved=0;
  ReservedSongText& operator=(const char* text) { bytes=text; return *this; }
  bool reserve(size_t size) { reserved=size; bytes.reserve(size); return true; }
  bool concat(const uint8_t* data, size_t count) {
    CHECK(bytes.size()+count<=reserved && data[count]==0);
    bytes.append(reinterpret_cast<const char*>(data),count);
    return true;
  }
  size_t length() const { return bytes.length(); }
};
struct FakeStore {
  std::map<std::string,std::string> files;
  std::set<std::string> failures;
  bool exists(const std::string& p) { return files.count(p)!=0; }
  bool write(const std::string& p,const std::string& value) { if(failures.count("write:"+p))return false; files[p]=value;return true; }
  bool matches(const std::string& p,const std::string& value) {
    if(!exists(p)) return false;
    auto& contents=files.at(p);
    if(failures.count("corrupt:"+p) && contents.size()>65536) contents[65536]^=1;
    SongFileReader reader{contents};
    if(failures.count("read:"+p)) reader.limit=0;
    return piano::matchesSongFile(reader,value);
  }
  bool remove(const std::string& p) { if(failures.count("remove:"+p))return false;files.erase(p);return true; }
  bool rename(const std::string& a,const std::string& b) { if(failures.count("rename:"+a+":"+b)||!exists(a)||exists(b))return false;files[b]=files[a];files.erase(a);return true; }
};
struct FontCanvas {
  std::vector<uint16_t> pixels = std::vector<uint16_t>(320 * 240, 0);
  int glyphs = 0;
  void drawRGBBitmap(int x, int y, uint16_t* bitmap, int width, int height) {
    CHECK(x >= 0 && y >= 0 && x + width <= 320 && y + height <= 240);
    ++glyphs;
    for (int row = 0; row < height; ++row) for (int col = 0; col < width; ++col)
      pixels[(y + row) * 320 + x + col] = bitmap[row * width + col];
  }
};
struct SpiCanvas : ScreenCanvas {
  unsigned windows=0, transactions=0;
  void startWrite() { CHECK(!writing_); writing_=true; ++transactions; }
  void endWrite() { CHECK(writing_ && remaining_==0); writing_=false; }
  void setAddrWindow(int x,int y,int w,int h) {
    CHECK(writing_ && remaining_==0);
    CHECK(x>=0 && y>=0 && w>0 && h>0 && x+w<=320 && y+h<=240);
    x_=x; y_=y; width_=w; col_=0; remaining_=w*h; ++windows;
  }
  void writePixels(const uint16_t* pixels, int count) {
    CHECK(writing_ && count>0 && count<=remaining_);
    remaining_-=count;
    for(int i=0;i<count;++i) {
      pixel(x_+col_,y_,pixels[i]);
      if(++col_==width_) { col_=0; ++y_; }
    }
  }
 private:
  bool writing_=false;
  int x_=0,y_=0,width_=0,col_=0,remaining_=0;
};
int main(int argc, char** argv) {
  const std::string preview = argc > 1 ? argv[1] : "";
  group("polyphonic payload, independent note clocks, pause, tempo, loop and PCM", [] {
    auto original = song({{72,.5},{74,.5},{76,1}});
    original.title = "复音测试";
    original.performance = {{0,2880,48,75,0},{0,480,72,96,1},{480,960,74,104,1},
                            {960,1920,76,110,1},{960,1920,79,92,2}};
    auto json = piano::encodeSong(original);
    if (const char* fixture = std::getenv("PIANO_POLYPHONY_FIXTURE")) {
      std::ifstream input(fixture, std::ios::binary); CHECK(input.good());
      json.assign(std::istreambuf_iterator<char>(input), {});
    }
    piano::Song decoded; std::string error;
    CHECK(piano::parseSong(json.data(), json.size(), decoded, error));
    CHECK(decoded.performance.size() == 5 && decoded.performance[0].end == 2880 && decoded.performance[0].velocity == 75);
    const auto stored = piano::encodeSong(decoded);
    CHECK(piano::parseSong(stored.data(), stored.size(), decoded, error));
    auto overLimit = original;
    overLimit.performance.assign(13, original.performance.front());
    const auto invalid = piano::encodeSong(overLimit);
    CHECK(!piano::parseSong(invalid.data(), invalid.size(), decoded, error));
    CHECK(decoded.performance.size() == 5);

    piano::Engine engine;
    const uint32_t start = UINT32_MAX - 100;
    CHECK(engine.start(decoded, piano::Mode::Play, start));
    auto state = engine.performanceView();
    CHECK(state.enabled && state.voices[0].midi == 48 && state.voices[1].midi == 72);
    const auto lowTrigger = state.voices[0].trigger;
    piano::PerformanceMixer mixer(22050, board::kPeakAmplitude);
    int16_t block[256];
    double bass = 0, treble = 0;
    for (int b = 0; b < 32; ++b) {
      mixer.render(state, {}, 0, 80, false, block, 128);
      for (int i = 0; i < 128; ++i) {
        const double seconds = (b * 128 + i) / 22050.0;
        const auto sample = block[2 * i];
        CHECK(std::abs(int(sample)) <= board::kPeakAmplitude);
        bass += sample * std::sin(6.28318530718 * piano::frequency(48) * seconds);
        treble += sample * std::sin(6.28318530718 * piano::frequency(72) * seconds);
      }
    }
    CHECK(std::abs(bass) > 100000 && std::abs(treble) > 100000);
    engine.tick(start + 250); state = engine.performanceView();
    CHECK(state.voices[0].midi == 48 && state.voices[0].trigger == lowTrigger && state.voices[1].midi == 74);
    CHECK(engine.pause(true, start + 300));
    CHECK(engine.performanceView().voices[0].midi < 0);
    engine.tick(start + 900);
    CHECK(engine.pause(false, start + 900));
    auto settings = engine.view().settings; settings.bpm = 60;
    engine.settings(settings, start + 900);
    engine.tick(start + 1300); state = engine.performanceView();
    CHECK(state.voices[0].midi == 48 && state.voices[1].midi == 76 && state.voices[2].midi == 79);
    engine.tick(start + 2300); state = engine.performanceView();
    CHECK(state.voices[0].midi == 48 && state.voices[1].midi < 0 && state.voices[2].midi < 0);
    engine.tick(start + 3300); CHECK(!engine.performanceView().enabled && engine.view().finished);
    CHECK(engine.start(decoded, piano::Mode::Play, 0));
    const auto epoch = engine.performanceView().epoch;
    settings.bpm = 120; settings.repeat = true; engine.settings(settings, 0); engine.tick(1500);
    state = engine.performanceView(); CHECK(state.epoch != epoch && state.voices[0].midi == 48 && state.voices[1].midi == 72);
    engine.stop(); CHECK(!engine.performanceView().enabled);
    CHECK(engine.start(decoded, piano::Mode::Teach, 0) && !engine.performanceView().enabled);
  });
  group("timed lyric JSON roundtrip, validation and note clock display", [] {
    auto data=payload(note(1,4)+","+note(0,1)+","+note(2,4));data.pop_back();
    data+=",\"lyricCues\":[{\"startBeat\":1.5,\"text\":\"第一句歌词\"},{\"startBeat\":4.25,\"text\":\"\"},{\"startBeat\":5.25,\"text\":\"第二句歌词\"}]}";
    piano::Song s;std::string error;CHECK(piano::parseSong(data.data(),data.size(),s,error));
    const auto encoded=piano::encodeSong(s);piano::Song restored;CHECK(piano::parseSong(encoded.data(),encoded.size(),restored,error));
    CHECK(restored.lyricCues.size()==3 && restored.lyricCues[0].text=="第一句歌词");
    piano::NotationLayout layout;layout.build(restored);piano::View v;v.count=3;
    v.elapsedBeats=1.49;auto page=layout.page(restored,v);CHECK(page.hasLyrics && page.currentLyric.empty() && page.nextLyric=="第一句歌词");
    v.elapsedBeats=1.5;page=layout.page(restored,v);CHECK(page.currentLyric=="第一句歌词");
    v.index=1;v.elapsedBeats=.25;page=layout.page(restored,v);CHECK(page.currentLyric.empty() && page.nextLyric=="第二句歌词");
    v.index=2;v.elapsedBeats=.25;page=layout.page(restored,v);CHECK(page.currentLyric=="第二句歌词");
    v.settings.bpm=60;v.paused=true;CHECK(layout.page(restored,v).currentLyric==page.currentLyric);
    piano::Engine engine;CHECK(engine.start(restored,piano::Mode::Play,0));engine.tick(900);
    const auto before=layout.page(restored,engine.view()).currentLyric;CHECK(engine.pause(true,900));engine.tick(9000);
    CHECK(layout.page(restored,engine.view()).currentLyric==before);CHECK(engine.pause(false,9000));engine.tick(9100);
    for (const auto& bad:std::vector<std::string>{"[{\"startBeat\":-1,\"text\":\"x\"}]","[{\"startBeat\":9,\"text\":\"x\"}]",
        "[{\"startBeat\":1,\"text\":\"x\"},{\"startBeat\":1,\"text\":\"y\"}]","[{\"startBeat\":1,\"text\":\"a\\nb\"}]",
        "[{\"startBeat\":1,\"text\":\""+std::string(513,'x')+"\"}]"}) {
      auto invalid=payload(note(1,9));invalid.pop_back();invalid+=",\"lyricCues\":"+bad+"}";
      CHECK(!piano::parseSong(invalid.data(),invalid.size(),s,error));
    }
  });
  if (const char* fixture=std::getenv("PIANO_MOBILE_LYRICS_FIXTURE")) group("real mobile serialized payload to firmware storage and TFT", [&] {
    std::ifstream input(fixture,std::ios::binary);CHECK(input.good());
    const std::string json((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>());
    piano::Song s;std::string error;CHECK(piano::parseSong(json.data(),json.size(),s,error));
    CHECK(s.notes.size()==500 && s.lyricCues.size()==4 && s.lyricCues[0].text=="第一句歌词");
    const auto stored=piano::encodeSong(s);piano::Song restored;CHECK(piano::parseSong(stored.data(),stored.size(),restored,error));
    piano::NotationLayout layout;layout.build(restored);piano::View v;v.count=500;v.mode=piano::Mode::Play;
    v.settings.showLyrics=true;v.settings.bpm=108;v.index=31;v.elapsedBeats=.825;
    auto page=layout.page(restored,v);CHECK(page.hasLyrics && page.currentLyric=="第一句歌词" && page.nextLyric=="第二句歌词");
    lcd::ScoreScreen screen;ScreenCanvas canvas;screen.draw(canvas,v,page,17680);
    if(!preview.empty())CHECK(canvas.save(preview+"/mobile-lyrics-import.ppm"));
    const auto writes=canvas.writes;screen.draw(canvas,v,page,17680);CHECK(canvas.writes==writes);
    v.index=47;v.elapsedBeats=.215;page=layout.page(restored,v);CHECK(page.currentLyric=="第二句歌词");
    v.index=108;v.elapsedBeats=0;page=layout.page(restored,v);CHECK(page.hasLyrics && page.currentLyric.empty() && page.nextLyric=="第三句歌词");
    v.index=117;page=layout.page(restored,v);CHECK(page.currentLyric=="第三句歌词");
  });
  group("score metadata and source spelling survive storage round trip", [] {
    std::string data = payload(note(7, 1, 0, true)); data.pop_back(); data += ",\"timeSignature\":\"4/4\"}";
    const auto end = data.find("durationBeats"); data.insert(end, "barBefore\":true,\"lineBefore\":true,\"");
    piano::Song s; std::string error; CHECK(piano::parseSong(data.data(), data.size(), s, error));
    CHECK(s.timeSignature == "4/4" && s.notes[0].barBefore && s.notes[0].lineBefore);
    const auto encoded = piano::encodeSong(s); piano::Song restored;
    CHECK(piano::parseSong(encoded.data(), encoded.size(), restored, error));
    CHECK(restored.notes[0].pitch == 7 && restored.notes[0].sharp && restored.notes[0].octave == 0);
    CHECK(restored.timeSignature == "4/4" && restored.notes[0].lineBefore);
    data = payload(note()); data.pop_back(); data += ",\"timeSignature\":\"5/3\"}";
    CHECK(!piano::parseSong(data.data(), data.size(), restored, error));
    data = payload("{\"pitch\":1,\"octave\":0,\"sharp\":false,\"durationBeats\":1,\"lineBefore\":1}");
    CHECK(!piano::parseSong(data.data(), data.size(), restored, error));
  });
  group("two score rows promote, preserve source breaks, bars and long holds", [] {
    auto s = song({}); for (int i = 0; i < 19; ++i) s.notes.push_back({60 + (i % 3) * 2, 1});
    s.notes[4].barBefore = true;
    piano::NotationLayout layout; layout.build(s); piano::View v; v.count = s.notes.size();
    auto page = layout.page(s, v); CHECK(page.rows[0].count == 8 && page.rows[1].count == 8 && page.cursor == 0);
    CHECK(page.rows[0].cells[4].barBefore); CHECK(page.rows[0].cells[1].pitch == 2);
    v.index = 7; page = layout.page(s, v); CHECK(page.rows[0].first == 0 && page.cursor == 7);
    v.index = 8; page = layout.page(s, v); CHECK(page.rows[0].first == 8 && page.rows[1].first == 16 && page.rows[1].count == 3 && page.cursor == 0);
    v.index = 19; page = layout.page(s, v); CHECK(page.rows[0].first == 16 && page.rows[1].count == 0 && page.cursor == -1 && page.progress == 1);
    s.notes[3].lineBefore = true; layout.build(s); v.index = 2; page = layout.page(s, v);
    CHECK(page.rows[0].count == 3 && page.rows[1].first == 3);
    s = song({{60, 8.5}, {62, .25}}); layout.build(s); v.index = 0; v.elapsedBeats = 7.9; page = layout.page(s, v);
    CHECK(page.cursor == 7 && page.rows[0].cells[7].continuation && page.rows[1].cells[0].continuation);
    v.elapsedBeats = 8; page = layout.page(s, v); CHECK(page.rows[0].first == 8 && page.cursor == 0 && page.rows[0].cells[0].beats == .5);
    CHECK(page.rows[0].cells[1].pitch == 2 && !page.rows[0].cells[1].continuation);
    s = song(std::vector<piano::Note>(1024, {60, 64})); layout.build(s); v.index = 1023; v.elapsedBeats = 63;
    page = layout.page(s, v); CHECK(page.rows[0].first == 65528 && page.cursor == 7);
  });
  group("explicit lyric phrases break rows without changing playback notes", [&] {
    auto s = song({}); s.title = "分句测试"; for (int i=0;i<14;++i) s.notes.push_back({60+(i%3)*2,.5});
    s.notes[3].lineBefore = true; s.notes[5].phraseBefore = true;
    s.notes[5].lyric = "下"; s.notes[6].lyric = "句";
    const auto encoded=piano::encodeSong(s); piano::Song roundtrip; std::string error;
    CHECK(piano::parseSong(encoded.data(), encoded.size(), roundtrip, error));
    CHECK(roundtrip.notes[5].phraseBefore && roundtrip.notes.size()==s.notes.size());
    piano::NotationLayout layout; layout.build(roundtrip); piano::View v; v.count=roundtrip.notes.size();
    auto page=layout.page(roundtrip,v); CHECK(page.rows[0].count==5 && page.rows[1].first==5 && page.rows[1].phraseStart);
    if (!preview.empty()) { lcd::ScoreScreen screen; ScreenCanvas canvas; v.mode=piano::Mode::Teach; screen.draw(canvas,v,page,0); CHECK(canvas.save(preview + "/phrase-preview.ppm")); }
    v.index=5; page=layout.page(roundtrip,v); CHECK(page.rows[0].phraseStart && page.cursor==0 && page.rows[0].count==8);
    v.index=13; page=layout.page(roundtrip,v); CHECK(!page.rows[0].phraseStart && page.cursor==0);
    CHECK(roundtrip.notes[5].beats==.5 && roundtrip.notes[5].midi==s.notes[5].midi);
    s.notes[5].phraseBefore=false; s.lyricCues={{0,"第一句"},{2.5,"第二句"}};
    layout.build(s);v.index=0;page=layout.page(s,v);CHECK(page.rows[0].count==5 && page.rows[1].phraseStart);
    s.lyricCues[1].startBeat=2.6;layout.build(s);page=layout.page(s,v);CHECK(page.rows[0].count==3 && !page.rows[1].phraseStart);
  });
  group("inferred phrase hints use rests and relative duration without changing playback", [&] {
    auto s=song({}); s.title="候选分句";
    for(int i=0;i<5;++i) s.notes.push_back({60,.5});
    s.notes.push_back({60,1.5}); s.notes.push_back({62,.5});
    for(int i=0;i<5;++i) s.notes.push_back({62,.5});
    s.notes.push_back({-1,.5}); s.notes.push_back({64,.5});
    piano::NotationLayout layout; layout.build(s); piano::View v; v.count=s.notes.size();
    auto page=layout.page(s,v); CHECK(page.rows[0].count==6 && page.rows[1].estimatedStart && !page.rows[1].phraseStart);
    v.index=6;page=layout.page(s,v);CHECK(page.rows[0].estimatedStart && page.cursor==0);
    v.index=13;page=layout.page(s,v);CHECK(page.rows[0].estimatedStart && page.cursor==0);
    CHECK(s.notes[5].beats==1.5 && s.notes[12].midi<0 && s.notes[12].beats==.5);
    if (!preview.empty()) { lcd::ScoreScreen screen; ScreenCanvas canvas; v.index=0;v.mode=piano::Mode::Teach;screen.draw(canvas,v,layout.page(s,v),0);CHECK(canvas.save(preview+"/phrase-estimated.ppm")); }
    s.notes[8].phraseBefore=true; layout.build(s);v.index=6;page=layout.page(s,v);
    CHECK(!page.rows[0].estimatedStart && page.rows[1].phraseStart);
  });
  group("free score advancing does not retrigger held note", [] {
    piano::Engine e; auto s=song({{60,.5},{60,.5},{62,.5}}); CHECK(e.start(s,piano::Mode::FreeScore,0));
    e.keys(1,1); e.tick(1); const auto trigger=e.view().trigger;
    e.tick(300); CHECK(e.view().index==1 && e.view().midi==60 && e.view().trigger==trigger);
    e.keys(0,301); CHECK(e.view().midi==-1); e.keys(1,302); CHECK(e.view().trigger>trigger);
  });
  group("display musical clock freezes on pause and waits for the correct repeated key", [] {
    piano::Engine e; CHECK(e.start(song({{60, 3}, {60, 1}}), piano::Mode::Play, 100)); e.tick(850);
    CHECK(std::abs(e.view().elapsedBeats - 1.5) < .001 && e.view().noteActive);
    e.pause(true, 850); e.tick(4000); CHECK(e.view().elapsedBeats == 1.5 && !e.view().noteActive);
    e.pause(false, 4000); e.tick(4250); CHECK(e.view().elapsedBeats == 2 && e.view().noteActive);
    const auto generation = e.view().songGeneration;
    CHECK(e.start(song({{60, 1}, {60, 1}}), piano::Mode::Teach, 5000)); e.tick(9000);
    CHECK(e.view().elapsedBeats == 0 && !e.view().noteActive && e.view().songGeneration != generation);
    e.keys(2, 9001); e.tick(9001); CHECK(!e.view().noteActive); e.keys(0, 9002); e.keys(1, 9003); e.tick(9003);
    CHECK(e.view().noteActive && e.view().elapsedBeats == 0); e.tick(9503);
    CHECK(e.view().index == 1 && !e.view().noteActive); e.keys(1, 9504); CHECK(!e.view().noteActive);
    e.keys(0, 9505); e.keys(1, 9506); e.tick(9506); CHECK(e.view().noteActive);
  });
  group("production TFT renderer: pressed highlight, scroll, idle and bounded glyphs", [&] {
    auto s = song({}); s.title = "欢乐颂"; s.timeSignature = "4/4"; s.bpm = 100;
    const int degrees[] = {3,3,4,5,5,4,3,2,1,1,2,3,3,2,2,0,3,3,4,5,5,4,3,2};
    for (unsigned i = 0; i < 24; ++i) { s.notes.push_back({piano::noteMidi(degrees[i], 0, false), .5}); s.notes.back().barBefore = i % 4 == 0; }
    piano::NotationLayout layout; layout.build(s); piano::Engine engine; CHECK(engine.start(s, piano::Mode::Play, 0));
    lcd::ScoreScreen screen; ScreenCanvas canvas;
    screen.draw(canvas, engine.view(), layout.page(s, engine.view()), 0); const auto pressed = canvas.pixels;
    if (!preview.empty()) CHECK(canvas.save(preview + "/score-00.ppm"));
    engine.tick(100); screen.draw(canvas, engine.view(), layout.page(s, engine.view()), 100); CHECK(canvas.pixels != pressed);
    const auto writes = canvas.writes; screen.draw(canvas, engine.view(), layout.page(s, engine.view()), 100); CHECK(canvas.writes == writes);
    for (unsigned i = 1; i < 20; ++i) {
      engine.tick(i * 300); auto v = engine.view(); auto page = layout.page(s, v); screen.draw(canvas, v, page, i * 300);
      if (i == 8) {
        CHECK(page.rows[0].first == 8 && page.cursor == 0 && page.rows[1].first == 16);
        lcd::ScoreScreen fresh; ScreenCanvas expected; fresh.draw(expected, v, page, i * 300);
        CHECK(canvas.pixels == expected.pixels); // Partial updates leave no old-row artifacts.
      }
      if (!preview.empty()) { char name[40]; std::snprintf(name, sizeof(name), "/score-%02u.ppm", i); CHECK(canvas.save(preview + name)); }
    }
    auto v = engine.view(); v.paused = true; v.noteActive = false; screen.draw(canvas, v, layout.page(s, v), 6000);
    if (!preview.empty()) CHECK(canvas.save(preview + "/score-paused.ppm"));
    s.notes[1].pitch = 4; s.notes[1].octave = -1; s.notes[1].sharp = true; s.notes[1].beats = .25;
    s.notes[2].octave = 1; s.notes[2].beats = .75; layout.build(s); v.index = 1; v.songGeneration++; v.paused = false;
    screen.draw(canvas, v, layout.page(s, v), 6100);
    if (!preview.empty()) CHECK(canvas.save(preview + "/score-symbols.ppm"));
    v = piano::View{}; v.manualKey = 3; v.midi = 65; screen.draw(canvas, v, {}, 7000);
    if (!preview.empty()) CHECK(canvas.save(preview + "/score-manual.ppm"));
    CHECK(lcd::findGlyph(0x6b22) && lcd::findGlyph(0x9882));
  });
  group("Chinese UTF-8 glyph rendering, clipping and screen bounds", [] {
    CHECK(lcd::textWidth(lcd::kTitle)==80);CHECK(lcd::textWidth(lcd::kMenu)==64);
    const char* cursor="迷A";CHECK(lcd::nextCodepoint(cursor)==0x8ff7);CHECK(lcd::nextCodepoint(cursor)=='A');CHECK(*cursor==0);
    CHECK(lcd::textWidth("\xe4\xb8")==8);CHECK(lcd::textWidth("\xe4X")==16);
    CHECK(lcd::textWidth("\xf0\x9f\x8e\xb9")==8); // Unsupported emoji is one visible fallback.
    FontCanvas canvas;CHECK(lcd::drawText(canvas,8,224,lcd::kMenuHint,0xffff)==lcd::textWidth(lcd::kMenuHint));
    CHECK(canvas.glyphs==13); // Ten Chinese glyphs plus the three ASCII characters in 1.2.
    size_t lit=0;for(auto pixel:canvas.pixels)if(pixel)++lit;CHECK(lit>100);
    FontCanvas clipped;CHECK(lcd::drawText(clipped,8,4,lcd::kTitle,0x07e0,19)==16);CHECK(clipped.glyphs==1);
    for(const auto* row:lcd::kMenuLabels)CHECK(lcd::textWidth(row)<=160);
    CHECK(lcd::textWidth(lcd::kMenuNavKeys)<=296); CHECK(lcd::textWidth(lcd::kMenuEditKeys)<=296); CHECK(lcd::textWidth(lcd::kVolumeEditKeys)<=296);CHECK(lcd::textWidth(lcd::kConnectScroll)<=296);
    CHECK(lcd::textWidth(lcd::kMenuShortcuts)<=296);
    for(const auto* label:lcd::kStorageNames)CHECK(lcd::textWidth(label)+lcd::textWidth(" / ")+lcd::textWidth(lcd::kAudioFault)<=304);
    for(const auto* label:lcd::kInstrumentNames)CHECK(lcd::textWidth(label)>0);
    CHECK(lcd::textWidth(lcd::kExitHint)<=304);
  });
  group("game release bonus follows duration and excludes flow", [] {
    piano::GameHoldBonus bonus; piano::Note n; n.pitch=2;n.beats=2;
    piano::View v;v.index=0;v.settings.bpm=120;v.elapsedBeats=1.6;
    bonus.accepted(0,n);CHECK(bonus.release(2,v,false)==0);CHECK(bonus.release(0,v,false)==0);
    bonus.accepted(0,n);v.elapsedBeats=1.68;CHECK(bonus.release(0,v,false)==20);CHECK(bonus.release(0,v,false)==0);
    bonus.accepted(0,n);v.elapsedBeats=0;v.index=1;CHECK(bonus.release(0,v,false)==20);
    bonus.accepted(0,n);CHECK(bonus.release(0,v,true)==0);
    bonus.accepted(0,n);v.paused=true;CHECK(bonus.release(0,v,false)==0);
    bonus.accepted(0,n);bonus.reset();v.paused=false;CHECK(bonus.release(0,v,false)==0);
  });
  group("manual chord maps held keys independently with shared modifiers", [] {
    piano::View v;v.keyMask=(1u<<0)|(1u<<2);auto n=piano::manualVoices(v);
    CHECK(n[0]==60);CHECK(n[1]==-1);CHECK(n[2]==64);
    v.keyMask=(1u<<2);v.low=true;v.sharp=true;v.settings.transpose=2;n=piano::manualVoices(v);
    CHECK(n[0]==-1);CHECK(n[2]==55);CHECK(n[6]==-1);
    v.keyMask=0;n=piano::manualVoices(v);for(int midi:n)CHECK(midi==-1);
  });
  group("manual mixer sounds a chord and releases keys", [] {
    piano::ManualMixer chord(22050,28672),single(22050,28672);piano::SampleBank bank;
    piano::View v;v.settings.instrument=0;v.keyMask=1;int16_t a[256],b[256];
    for(int i=0;i<12;++i){single.render(v,bank,a,128);chord.render(v,bank,b,128);}
    v.keyMask=5;chord.render(v,bank,b,128);v.keyMask=1;single.render(v,bank,a,128);
    CHECK(!std::equal(a,a+256,b));
    v.keyMask=0;for(int i=0;i<100;++i)chord.render(v,bank,b,128);
    for(auto value:b)CHECK(std::abs(value)<=1);
  });
  group("unavailable sample routes to audible synth without replacing a ready bank", [] {
    piano::SampleBank bank;
    CHECK(piano::playableInstrument(0,bank)==0);
    CHECK(piano::playableInstrument(11,bank)==1);
    int16_t unused=0; bank.pcm=&unused; bank.instrument=4;
    CHECK(piano::playableInstrument(4,bank)==4);
    CHECK(piano::playableInstrument(11,bank)==1);
    piano::ManualMixer mixer(22050,board::kPeakAmplitude);
    piano::View view; view.settings.instrument=piano::playableInstrument(11,bank); view.keyMask=7;
    int16_t stereo[256]; mixer.render(view,bank,stereo,128);
    CHECK(std::any_of(stereo,stereo+256,[](int16_t value){return value!=0;}));
  });
  group("manual chord does not divide the held key gain by chord size", [] {
    const char* root=std::getenv("PIANO_SAMPLE_DIRECTORY"); CHECK(root);
    std::ifstream sample(std::string(root)+"/11.hps", std::ios::binary); CHECK(sample.good());
    const std::vector<uint8_t> data((std::istreambuf_iterator<char>(sample)), std::istreambuf_iterator<char>());
    piano::SampleBank bank; CHECK(bank.bind(data.data(),data.size(),11));
    for (int instrument : {0, 11}) {
    piano::ManualMixer mixed(22050,28672), first(22050,28672), second(22050,28672), third(22050,28672);
    piano::View v; v.settings.instrument=instrument; v.volume=25;
    int16_t together[256], a[256], b[256], c[256];
    for (int block=0;block<12;++block) {
      v.keyMask=1; mixed.render(v,bank,together,128); first.render(v,bank,a,128);
    }
    for (int block=0;block<20;++block) {
      v.keyMask=7; mixed.render(v,bank,together,128);
      v.keyMask=1; first.render(v,bank,a,128);
      v.keyMask=2; second.render(v,bank,b,128);
      v.keyMask=4; third.render(v,bank,c,128);
      for (int i=0;i<256;++i) CHECK(together[i]==a[i]+b[i]+c[i]);
    }
    for (int block=0;block<20;++block) {
      v.keyMask=1; mixed.render(v,bank,together,128); first.render(v,bank,a,128);
      v.keyMask=0; second.render(v,bank,b,128); third.render(v,bank,c,128);
      for (int i=0;i<256;++i) CHECK(together[i]==a[i]+b[i]+c[i]);
    }
    v.keyMask=127; v.volume=100;
    for (int block=0;block<100;++block) {
      mixed.render(v,bank,together,128);
      for (auto sample : together) CHECK(std::abs(int(sample))<28672);
    }
    }
  });
  group("polyphonic sustain holds released notes until pedal up", [] {
    auto original=song({{60,2}});
    original.title="Pedal";
    original.performance={{0,480,60,100,0},{960,1920,67,100,0}};
    original.pedal={1,1680}; // Pedal up at tick 840, after key-up.
    const auto json=piano::encodeSong(original);
    piano::Song decoded; std::string error;
    CHECK(piano::parseSong(json.data(),json.size(),decoded,error));
    CHECK(decoded.pedal==original.pedal);
    piano::Engine e; CHECK(e.start(decoded,piano::Mode::Play,0));
    CHECK(e.performanceView().pedal);
    piano::PerformanceMixer mixer(22050,28672); int16_t audio[256];
    mixer.render(e.performanceView(),{},0,60,false,audio,128);
    e.tick(300); CHECK(e.performanceView().voices[0].midi<0 && e.performanceView().pedal);
    for (int i=0;i<40;++i) mixer.render(e.performanceView(),{},0,60,false,audio,128);
    CHECK(std::any_of(audio,audio+256,[](int16_t value){return std::abs(int(value))>100;}));
    e.tick(450); CHECK(!e.performanceView().pedal);
    for (int i=0;i<40;++i) mixer.render(e.performanceView(),{},0,60,false,audio,128);
    CHECK(std::all_of(audio,audio+256,[](int16_t value){return value==0;}));
  });
  group("physical free-paced game uses input cursor and repeat triggers", [] {
    piano::Song s=song({{60,2},{75,1},{-1,.5},{64,1}});s.notes[0].pitch=1;s.notes[1].pitch=2;s.notes[1].octave=1;s.notes[1].sharp=true;s.notes[3].pitch=3;
    piano::Engine e;piano::GameFlow f;CHECK(e.start(s,piano::Mode::FreeScore,0));e.stop();f.reset(e,0);
    CHECK(f.step(e,1,10).accepted==0);CHECK(e.view().midi==60);CHECK(f.view(e,10).index==1);
    CHECK(f.step(e,3,100).accepted==1);CHECK(e.view().midi==75);CHECK(f.view(e,100).index==3);
    CHECK(f.step(e,7,120).accepted==3);CHECK(f.view(e,120).finished);
    piano::Song repeat=song({{60,1},{60,1}});for(auto& n:repeat.notes)n.pitch=1;
    CHECK(e.start(repeat,piano::Mode::FreeScore,2000));e.stop();f.reset(e,2000);CHECK(f.step(e,1,2010).accepted==0);
    auto trigger=e.view().trigger;auto held=f.step(e,1,2510);CHECK(held.accepted==1 && held.automatic);CHECK(e.view().trigger>trigger);
    f.step(e,0,2520);CHECK(e.view().midi<0);
  });
  group("physical game assist handles early transition and held same degree", [] {
    piano::Song s=song({{60,2},{75,1},{-1,.5},{64,1}});
    s.notes[0].pitch=1;s.notes[1].pitch=2;s.notes[1].octave=1;s.notes[1].sharp=true;s.notes[3].pitch=3;
    piano::Engine e;piano::GameAssist a;CHECK(e.start(s,piano::Mode::FreeScore,0));
    CHECK(a.step(e,1,10).accepted==0);CHECK(e.view().midi==60);
    CHECK(a.step(e,3,100).accepted==1);CHECK(e.view().midi==75);CHECK(e.view().index==1);
    CHECK(a.step(e,7,130).accepted==-1);CHECK(e.view().index==1);
    a.step(e,0,140);a.step(e,0,700);CHECK(e.view().index==2);
    a.step(e,0,1000);CHECK(e.view().index==3);CHECK(a.step(e,4,1010).accepted==3);
    piano::Song repeat=song({{60,1},{60,1}});for(auto& n:repeat.notes)n.pitch=1;
    e.stop();a.reset();CHECK(e.start(repeat,piano::Mode::FreeScore,2000));CHECK(a.step(e,1,2010).accepted==0);
    const auto trigger=e.view().trigger;const auto continued=a.step(e,1,2510);
    CHECK(continued.accepted==1 && continued.automatic);CHECK(e.view().trigger>trigger);
    a.step(e,0,2520);CHECK(e.view().midi<0);
  });
  group("dense same-lane notes do not cover their neighbours", [] {
    using S=lcd::GameStyle;
    ScreenCanvas c; lcd::GameScreen screen; lcd::GameFrame f; f.ready=true;f.count=3;
    f.tiles[0]={7,-1,false,false,80,100,0};
    f.tiles[1]={7,0,false,false,75,95,0};
    f.tiles[2]={6,1,false,false,80,100,0};
    screen.draw(c,f,1);const int x=6+6*44+2+20;
    CHECK(c.pixels[99*320+x]==S::blend(S::background,S::accent(0),9));
    CHECK(c.pixels[95*320+x]==S::background);
    CHECK(c.pixels[94*320+x]==S::blend(S::background,S::accent(2),9));
    CHECK(c.pixels[99*320+(6+5*44+2+20)]==S::blend(S::background,S::accent(4),9));
  });
  group("guided games scroll all visible notes and keep numbered labels and accurate highlights", [&] {
    using S = lcd::GameStyle;
    const auto json = payload(note(3) + "," + note(2,1,1,true) + "," + note(0) + "," + note(4) + "," + note(4));
    piano::Song guide; std::string error;
    CHECK(piano::parseSong(json.data(),json.size(),guide,error));
    for (const bool freePaced : {false, true}) {
      piano::Engine engine;
      piano::GameAssist assist;
      piano::GameFlow flow;
      CHECK(engine.start(guide,piano::Mode::FreeScore,0));
      if (freePaced) { engine.stop(); flow.reset(engine,0); }
      lcd::GameFrame frame; frame.ready=true; frame.title="滚动测试"; frame.mode=freePaced?"自主节奏":"辅助自由";
      lcd::GuidedGame guided;
      ScreenCanvas canvas;
      lcd::GameScreen screen;
      int accepted=-1;
      uint32_t started=0;
      auto press=[&](uint16_t keys,uint32_t now) {
        const int hit=freePaced ? flow.step(engine,keys,now).accepted : assist.step(engine,keys,now).accepted;
        if (hit>=0) { accepted=hit; started=now; }
        const auto view=freePaced ? flow.view(engine,now) : engine.view();
        guided.update(frame,engine.song(),view,accepted,now-started,now);
        return hit;
      };
      CHECK(press(0,0)==-1 && frame.count==4 && frame.keys==0);
      CHECK(frame.tiles[0].degree==3 && frame.tiles[0].bottom==S::line && !frame.tiles[0].hit);
      CHECK(frame.tiles[1].degree==2 && frame.tiles[2].degree==4);
      CHECK(std::abs(frame.tiles[0].bottom-frame.tiles[1].bottom - 500*S::pixelsPerMs)<.01);
      CHECK(std::abs(frame.tiles[1].bottom-frame.tiles[2].bottom - 1000*S::pixelsPerMs)<.01);
      CHECK(press(64,33)==-1 && frame.keys==64 && frame.count==4 && !frame.tiles[0].hit);
      CHECK(frame.tiles[0].bottom==S::line);
      press(0,66);
      CHECK(press(4,99)==0 && frame.keys==4 && engine.view().midi==64 && frame.tiles[0].holding);
      screen.draw(canvas,frame,99);
      ScreenCanvas expected;
      const int keyX=S::left+2*S::lane+2, y=S::keyTop+6;
      expected.fillRect(keyX+8,y,24,16,S::accent(2));
      lcd::drawText(expected,keyX+16,y,"3",S::background,8,S::accent(2));
      for(int row=y;row<y+16;++row) {
        const int offset=row*S::width+keyX+8;
        CHECK(std::equal(canvas.pixels.begin()+offset,canvas.pixels.begin()+offset+24,expected.pixels.begin()+offset));
      }

      press(4,132);
      const auto tileFor=[&](int degree) -> const lcd::GameTile& {
        const auto* end=frame.tiles+frame.count;
        const auto* tile=std::find_if(static_cast<const lcd::GameTile*>(frame.tiles),end,
          [=](const auto& item){ return item.degree==degree; });
        CHECK(tile!=end);
        return *tile;
      };
      const float before=tileFor(2).bottom;
      const float beforeLater=tileFor(4).bottom;
      CHECK(before>S::line-500*S::pixelsPerMs);
      CHECK(press(6,165)==1 && frame.keys==2 && engine.view().midi==75);
      CHECK(tileFor(2).holding && tileFor(2).octave==1 && tileFor(2).sharp && !tileFor(4).holding);
      CHECK(std::abs(tileFor(2).bottom-before-33*S::pixelsPerMs)<.01);
      CHECK(std::abs((tileFor(2).bottom-before)-(tileFor(4).bottom-beforeLater))<.01f);
      press(6,198);
      CHECK(std::abs(tileFor(2).bottom-before-66*S::pixelsPerMs)<.01);
      CHECK(press(0,231)==-1 && !frame.keys);
      CHECK(std::none_of(frame.tiles,frame.tiles+frame.count,[](const auto& tile){ return tile.holding; }));
      press(0,800); press(0,1700);
      CHECK(frame.tiles[0].degree==4 && frame.tiles[0].bottom==S::line);
      CHECK(press(8,1710)==3 && frame.keys==8 && frame.tiles[0].holding);
      press(8,2180);
      CHECK(press(8,2210)==4 && frame.keys==8);
      CHECK(std::count_if(frame.tiles,frame.tiles+frame.count,[](const auto& tile){ return tile.holding; })==1);
      CHECK(press(0,2220)==-1 && !frame.keys);
      press(0,3300);
      CHECK(frame.count==0 && frame.progress==1);

      CHECK(engine.start(guide,piano::Mode::FreeScore,3400));
      if (freePaced) { engine.stop(); flow.reset(engine,3400); }
      assist.reset();
      accepted=-1;
      press(0,3400);
      CHECK(frame.progress==0 && frame.tiles[0].bottom==S::line);
    }
  });
  group("polyphonic playback frame follows sounding source notes, rests and tempo", [&] {
    using S=lcd::GameStyle;
    piano::Song s=song({{60,4}});
    // Several different octaves share the physical F lane.
    s.performance={{0,960,53,72,0},{0,480,65,90,1},{480,960,76,75,1},{1920,3840,72,80,0}};
    piano::Engine engine; CHECK(engine.start(s,piano::Mode::Play,0));
    lcd::GameFrame frame; frame.ready=true; frame.title="多音测试";
    lcd::GameScreen screen; ScreenCanvas canvas;
    lcd::PerformanceLayout layout;
    auto update=[&] { lcd::performanceGame(frame,layout,engine.song(),engine.view(),engine.performanceView()); };
    update();
    CHECK(frame.polyphonic && frame.keys==(1u<<3) && frame.count==4);
    CHECK(frame.tiles[0].degree==4 && frame.tiles[0].octave==-1 && frame.tiles[1].degree==4 && frame.tiles[1].octave==0);
    CHECK(frame.tiles[0].holding && frame.tiles[1].holding);
    CHECK(frame.tiles[0].columns==2 && frame.tiles[1].columns==2 && frame.tiles[0].column!=frame.tiles[1].column);
    CHECK(frame.tiles[2].degree==3 && frame.tiles[2].octave==1 && !frame.tiles[2].hit && frame.tiles[2].bottom<S::line);
    CHECK(frame.tiles[3].degree==1 && frame.tiles[3].octave==1 && !frame.tiles[3].hit);
    screen.draw(canvas,frame,0);
    if (!preview.empty()) CHECK(canvas.save(preview+"/poly-game-overlap.ppm"));
    engine.tick(250); update();
    CHECK(frame.count==3 && frame.keys==((1u<<2)|(1u<<3)) && frame.inputStyle[2]==S::colorIndex(1,false));
    CHECK(frame.tiles[0].degree==4 && frame.tiles[0].octave==-1 && frame.tiles[1].degree==3 && frame.tiles[1].bottom==S::line);
    screen.draw(canvas,frame,250);
    ScreenCanvas expected;
    const int keyX=S::left+2*S::lane+2, keyY=S::keyTop+6;
    const auto paper=S::accent(S::colorIndex(1,false));
    expected.fillRect(keyX+8,keyY,24,16,paper);
    lcd::drawText(expected,keyX+16,keyY,"3",S::background,8,paper);
    for(int y=keyY;y<keyY+16;++y) {
      const int offset=y*S::width+keyX+8;
      CHECK(std::equal(canvas.pixels.begin()+offset,canvas.pixels.begin()+offset+24,expected.pixels.begin()+offset));
    }
    const int tileTop=static_cast<int>(std::floor(frame.tiles[1].top));
    const int tileY=tileTop+(S::line-tileTop-8)/2;
    const auto* compactDigit=lcd::findGlyph('3');
    CHECK(compactDigit && compactDigit->width==8);
    for(int row=0;row<8;++row) for(int col=0;col<8;++col) {
      const bool ink=(compactDigit->bitmap[2*row] | compactDigit->bitmap[2*row+1]) & (0x80 >> col);
      CHECK((canvas.pixels[(tileY+row)*320+keyX+16+col]==S::white)==ink);
    }
    CHECK(canvas.pixels[(tileY-2)*320+keyX+19]==S::white);
    CHECK(engine.pause(true,300)); engine.tick(12300); update();
    CHECK(frame.keys==0 && frame.count==3 && frame.progress>0 && frame.progress<.2f);
    CHECK(engine.pause(false,12300)); engine.tick(12500); update();
    CHECK(frame.keys==0 && frame.count==1 && frame.tiles[0].degree==1 && frame.tiles[0].octave==1 && !frame.tiles[0].holding);
    auto settings=engine.view().settings; settings.transpose=2; engine.settings(settings,12500); engine.tick(13000); update();
    CHECK(frame.keys==(1u<<1) && frame.tiles[0].degree==2 && frame.tiles[0].octave==1 && frame.tiles[0].holding);
    engine.tick(14000); update(); CHECK(frame.count==0 && frame.keys==0 && frame.progress==1);
    CHECK(!lcd::showsPerformance(engine.performanceView(),piano::View{}));
  });
  group("performance columns stay fixed when overlap scrolls in and out", [] {
    piano::Song s=song({{60,4}});
    s.performance={{0,6000,60,80,0},{4800,5200,72,80,1},{6500,7200,60,80,0}};
    piano::Engine engine; CHECK(engine.start(s,piano::Mode::Play,0));
    lcd::GameFrame frame; lcd::PerformanceLayout layout;
    for(uint32_t now : {0u,1000u,2800u}) {
      engine.tick(now);
      lcd::performanceGame(frame,layout,engine.song(),engine.view(),engine.performanceView());
      CHECK(frame.tiles[0].column==0 && frame.tiles[0].columns==2 && frame.tiles[0].holding);
      CHECK(frame.count==(now==0 || now==2800?2u:3u));
      if(now==1000) CHECK(frame.tiles[1].column==1 && frame.tiles[1].columns==2);
      if(now==2800) CHECK(frame.tiles[1].column==0 && frame.tiles[1].columns==1);
    }
    engine.tick(3200);
    lcd::performanceGame(frame,layout,engine.song(),engine.view(),engine.performanceView());
    CHECK(frame.count==1 && frame.tiles[0].columns==1);

    s.performance={{0,960,64,80,0},{0,960,65,80,1}};
    piano::View view;
    layout.update(s,view);
    CHECK(layout[0].columns==1 && layout[1].columns==1);
    view.settings.transpose=1;
    layout.update(s,view);
    CHECK(layout[0].columns==2 && layout[1].columns==2 && layout[0].column!=layout[1].column);
  });
  group("guided and automatic play draw equivalent notes and accents identically", [&] {
    using S=lcd::GameStyle;
    const auto json=payload(note(2,1,1,true)+","+note(4,1,-1)+","+note(3));
    piano::Song s; std::string error; CHECK(piano::parseSong(json.data(),json.size(),s,error));
    s.performance={{0,960,75,80,0},{960,1920,53,80,0},{1920,2880,64,80,0}};
    piano::Engine automatic, assisted;
    CHECK(automatic.start(s,piano::Mode::Play,0));
    CHECK(assisted.start(s,piano::Mode::FreeScore,0));
    piano::GameAssist input;
    CHECK(input.step(assisted,2,0).accepted==0);
    lcd::GameFrame autoFrame, guideFrame;
    autoFrame.ready=guideFrame.ready=true;
    autoFrame.mode="自动播放"; guideFrame.mode="辅助自由";
    autoFrame.title=guideFrame.title="音游演奏";
    lcd::GuidedGame guided;
    ScreenCanvas autoCanvas, guideCanvas;
    lcd::GameScreen autoScreen, guideScreen;
    lcd::PerformanceLayout layout;
    for (uint32_t now : {0u,150u,300u}) {
      automatic.tick(now);
      input.step(assisted,2,now);
      lcd::performanceGame(autoFrame,layout,s,automatic.view(),automatic.performanceView());
      guided.update(guideFrame,s,assisted.view(),0,now,now);
      autoScreen.draw(autoCanvas,autoFrame,now);
      guideScreen.draw(guideCanvas,guideFrame,now);
      CHECK(std::equal(autoCanvas.pixels.begin()+S::boardTop*S::width,autoCanvas.pixels.end(),guideCanvas.pixels.begin()+S::boardTop*S::width));
    }
    if(!preview.empty()) {
      CHECK(autoCanvas.save(preview+"/game-style-automatic.ppm"));
      CHECK(guideCanvas.save(preview+"/game-style-assisted.ppm"));
    }

    autoFrame.keys=0;
    autoFrame.tiles[0]={2,1,true,false,100,114,0};
    autoFrame.count=1;
    autoScreen.draw(autoCanvas,autoFrame,330);
    const int center=S::left+S::lane+2+20;
    CHECK(autoCanvas.pixels[101*S::width+center-1]==S::white); // Upper octave dot on a short block.
    const auto* glyph=lcd::findGlyph('2'); CHECK(glyph && glyph->width==8);
    for(int row=0;row<8;++row) for(int col=0;col<8;++col) {
      const bool ink=(glyph->bitmap[2*row] | glyph->bitmap[2*row+1]) & (0x80 >> col);
      CHECK((autoCanvas.pixels[(103+row)*S::width+center+col]==S::white)==ink);
    }
    CHECK(autoCanvas.pixels[(S::line-3)*S::width+center]==S::background);
  });
  group("guided performance gates original chords and accompaniment on melody input", [] {
    piano::Song s;
    std::string error;
    const auto json=payload(note(3)+","+note(2)+","+note(5));
    CHECK(piano::parseSong(json.data(),json.size(),s,error));
    s.performance={{0,1440,53,70,0},{0,850,76,90,1},{480,660,60,74,2},
      {960,1680,74,87,1},{1440,1920,55,80,0},{1920,2520,79,92,1},{2400,2800,60,80,0}};
    s.pedal={1,2880};
    for (const bool freePaced : {false,true}) {
      piano::Engine engine;
      CHECK(engine.start(s,piano::Mode::FreeScore,0));
      piano::GameAssist assist;
      piano::GameFlow flow;
      if (freePaced) {engine.stop();flow.reset(engine,0);}
      piano::GuidedPerformance follow;
      follow.start(s,0);
      const auto step=[&](uint16_t keys,uint32_t now,bool suspended=false) {
        const int accepted=freePaced?flow.step(engine,keys,now).accepted:assist.step(engine,keys,now).accepted;
        follow.step(s,freePaced?flow.view(engine,now):engine.view(),accepted,now,suspended);
        return accepted;
      };
      const auto& state=follow.view();
      CHECK(state.enabled && follow.nextDegree(s)==3 && follow.nextEvent()==2);
      CHECK(step(1,5)==-1 && state.positionTicks==0 && state.voices[0].midi<0);
      step(0,6);
      CHECK(step(4,10)==0 && state.voices[0].midi==53 && state.voices[1].midi==76 && state.pedal);
      CHECK(state.voices[1].velocity==90 && follow.nextDegree(s)==2);
      CHECK(step(4,260)==-1 && state.voices[2].midi==60 && state.positionTicks==480);
      step(0,510);
      CHECK(state.positionTicks==960 && !state.pedal && state.voices[0].midi<0 && state.voices[1].midi<0);
      step(0,1000);
      CHECK(state.positionTicks==960 && state.voices[1].midi<0);
      CHECK(step(2,1010)==1 && state.voices[0].midi==53 && state.voices[1].midi==74);
      CHECK(follow.nextDegree(s)==5);
      CHECK(step(18,1090)==2 && state.positionTicks==1920 && state.voices[1].midi==79 && !state.pedal);
      CHECK(follow.nextDegree(s)==0);
      step(0,1340);
      CHECK(state.voices[0].midi==60 && state.voices[1].midi==79);
      step(0,1400,true);
      CHECK(state.paused && state.positionTicks==2400 && state.voices[1].midi<0);
      step(0,2000,true);
      CHECK(state.positionTicks==2400);
      step(0,2000);
      CHECK(!state.paused && state.voices[0].midi==60 && state.voices[1].midi==79);
      step(0,2400);
      CHECK(state.positionTicks==2800 && state.voices[1].midi<0);
      const auto previousEpoch=state.epoch;
      follow.rewind(2500);
      CHECK(state.epoch!=previousEpoch && state.positionTicks==0 && follow.nextDegree(s)==3);
    }
    piano::GuidedPerformance follow;
    s.notes[1].midi=71; // No matching pitch at this start; keep the existing single-note route.
    follow.start(s,0);
    CHECK(!follow.view().enabled);
    s.performance.clear();
    follow.start(s,0);
    CHECK(!follow.view().enabled);
  });
  group("chord practice arranges seven keys and waits for independently pressed chord notes", [] {
    auto s=song({{60,1}});
    s.performance={{0,1800,60,75,0},{0,480,72,75,1},{0,480,61,85,2},{0,1200,64,70,3},
      {960,1800,76,90,4},{960,1800,53,66,0}};
    CHECK(piano::ChordPractice::arrange(s));
    CHECK(s.performance.size()==4 && s.performance[0].midi==61 && s.performance[0].slot==0);
    CHECK(s.performance[1].midi==64 && s.performance[1].end==960 && s.performance[1].slot==2);
    CHECK(s.performance[2].midi==76 && s.performance[3].midi==53);
    piano::ChordPractice practice; practice.start(s,0);
    piano::Settings settings;
    CHECK(practice.targets()==5 && practice.cueCount()==2 && practice.audio().enabled);
    practice.step(s,settings,1,10);
    CHECK(practice.audio().voices[0].midi==61 && practice.audio().voices[2].midi<0 && practice.firstCue()==1);
    const auto firstTrigger=practice.audio().voices[0].trigger;
    practice.step(s,settings,0,20);
    CHECK(practice.audio().voices[0].midi<0 && practice.firstCue()==1);
    practice.step(s,settings,1,30);
    CHECK(practice.audio().voices[0].trigger!=firstTrigger && practice.firstCue()==1);
    practice.step(s,settings,7,40);
    CHECK(practice.audio().voices[1].midi<0 && practice.audio().voices[2].midi==64 && practice.firstCue()==3);
    practice.step(s,settings,5,50);
    CHECK(practice.firstCue()==3 && practice.targets()==12 && practice.audio().voices[1].midi<0);
    practice.step(s,settings,13,60);
    CHECK(practice.audio().voices[3].midi==53 && practice.audio().voices[2].midi==64 && !practice.finished());
    practice.step(s,settings,9,70);
    CHECK(practice.audio().voices[2].midi<0 && practice.audio().voices[3].midi==53);
    practice.step(s,settings,13,80);
    CHECK(practice.audio().voices[2].midi==76 && !practice.finished());
    practice.step(s,settings,0,1000);
    CHECK(practice.finished() && practice.display().positionTicks==1800 && practice.audio().voices[3].midi<0);
    settings.repeat=true;
    practice.step(s,settings,0,1001);
    CHECK(practice.targets()==5 && practice.display().positionTicks<960);

    auto single=song({{-1,1},{67,1},{69,1}});
    CHECK(piano::ChordPractice::arrange(single) && single.performance.size()==2);
    CHECK(single.performance[0].start==960 && single.performance[0].slot==4);
  });
  group("chord practice waterfall matches automatic speed with early hits and silent wrong keys", [] {
    using S=lcd::GameStyle;
    auto s=song({{60,1},{64,1},{69,1}});
    s.performance={{0,350,60,80,0},{960,1440,64,82,1},{960,1600,67,86,2},{1920,3000,69,80,0}};
    piano::Engine automatic;
    CHECK(automatic.start(s,piano::Mode::Play,0));
    CHECK(piano::ChordPractice::arrange(s));
    piano::ChordPractice practice; practice.start(s,0);
    piano::View view=automatic.view();
    lcd::GameFrame autoFrame, practiceFrame;
    lcd::PerformanceLayout autoLayout, practiceLayout;
    auto update=[&](uint32_t now,uint16_t keys) {
      automatic.tick(now);
      practice.step(s,view.settings,keys,now);
      lcd::performanceGame(autoFrame,autoLayout,automatic.song(),automatic.view(),automatic.performanceView());
      lcd::performanceGame(practiceFrame,practiceLayout,s,view,practice.display(),practice.firstCue(),practice.cueCount(),true);
    };
    auto bottom=[](const lcd::GameFrame& frame,int degree) {
      const auto* end=frame.tiles+frame.count;
      const auto* tile=std::find_if(frame.tiles,end,[=](const auto& note){return note.degree==degree;});
      CHECK(tile!=end);
      return tile->bottom;
    };
    update(0,1);
    const float opening=bottom(practiceFrame,3);
    update(33,64);
    CHECK(practiceFrame.keys==0 && practice.targets()==20);
    CHECK(std::abs(bottom(practiceFrame,3)-opening-33*S::pixelsPerMs)<.001);
    CHECK(std::abs(bottom(practiceFrame,3)-bottom(autoFrame,3))<.001);
    update(200,68);
    CHECK(practiceFrame.keys==4 && practice.targets()==20 && practice.display().positionTicks<960);
    update(220,84);
    CHECK(practiceFrame.keys==20 && practice.targets()==32);
    CHECK(std::abs(bottom(practiceFrame,6)-bottom(autoFrame,6))<.001);
    update(1000,0);
    CHECK(practice.display().positionTicks==1920 && practiceFrame.keys==0);
    update(1500,16);
    CHECK(practice.display().positionTicks==1920 && practiceFrame.keys==0 && practice.targets()==32);
    update(1520,48);
    CHECK(practice.display().positionTicks==1920 && practiceFrame.keys==32);
    update(1553,32);
    CHECK(std::abs(bottom(practiceFrame,6)-S::line-33*S::pixelsPerMs)<.001);
  });
  group("short chord cues get readable full-width tiles within available space", [] {
    using S=lcd::GameStyle;
    piano::Song s=song({{60,1}});
    s.performance={{0,60,60,80,0},{0,120,64,80,2},{640,680,60,80,0}};
    CHECK(piano::ChordPractice::arrange(s));
    piano::ChordPractice practice; practice.start(s,0);
    lcd::GameFrame frame; frame.ready=true; frame.guideMask=practice.targets();
    lcd::PerformanceLayout layout;
    lcd::performanceGame(frame,layout,s,piano::View{},practice.display(),practice.firstCue(),practice.cueCount(),true);
    CHECK(frame.keySlots && frame.count==3 && frame.tiles[0].columns==1 && frame.tiles[1].columns==1);
    CHECK(frame.tiles[0].bottom-frame.tiles[0].top>=12.f && frame.tiles[1].bottom-frame.tiles[1].top>=12.f);
    CHECK(s.performance[0].end==60 && s.performance[1].end==120);
    ScreenCanvas canvas; lcd::GameScreen screen;
    screen.draw(canvas,frame,0);
    const int x=S::left+2+20;
    CHECK(std::any_of(canvas.pixels.begin()+(S::line-5)*S::width+x-4,
      canvas.pixels.begin()+(S::line-5)*S::width+x+4,[](uint16_t p){return p==S::white;}));
  });
  group("polyphonic score uses numbered pitches with sharps and precise octaves", [] {
    using S=lcd::ScoreScreen;
    piano::PerformanceView performance;
    performance.enabled=true; performance.endTick=480;
    performance.voices[0].midi=76;  // Upper 3.
    performance.voices[1].midi=42;  // Two octaves below middle #4.
    performance.voices[2].midi=64;  // Middle 3.
    ScreenCanvas canvas, expected;
    S screen;
    piano::View view; view.mode=piano::Mode::Play;
    screen.draw(canvas,view,piano::ScorePage{},0,&performance);
    expected.fillRect(8,79,150,21,S::kBackground);
    lcd::drawText(expected,21,81,"#4",S::kWhite,16,S::kBackground);
    expected.fillRect(27,97,2,2,S::kWhite);
    expected.fillRect(30,97,2,2,S::kWhite);
    lcd::drawText(expected,75,81,"3",S::kWhite,8,S::kBackground);
    lcd::drawText(expected,125,81,"3",S::kWhite,8,S::kBackground);
    expected.fillRect(128,79,2,2,S::kWhite);
    for(int y=79;y<100;++y) {
      const int offset=y*320+8;
      CHECK(std::equal(canvas.pixels.begin()+offset,canvas.pixels.begin()+offset+150,expected.pixels.begin()+offset));
    }
  });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("Time Boils the Rain guide triggers the original left and right hand in the mixer and screen", [&] {
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song; std::string error;
      CHECK(piano::parseSong(json.data(),json.size(),song,error));
      piano::Engine engine; piano::GameAssist assist;
      CHECK(engine.start(song,piano::Mode::FreeScore,0));
      piano::GuidedPerformance follow;follow.start(song,0);
      CHECK(follow.view().enabled && follow.nextDegree(song)==3);
      const auto hit=assist.step(engine,4,10);
      CHECK(hit.accepted==0);
      follow.step(song,engine.view(),hit.accepted,10,false);
      const auto& original=follow.view();
      auto bass=original, treble=original;
      unsigned opening=0;
      for(size_t i=0;i<original.voices.size();++i) {
        const int pitch=original.voices[i].midi;
        if(pitch==53) {treble.voices[i]=piano::PerformanceVoice{};opening|=1;}
        else if(pitch==76) {bass.voices[i]=piano::PerformanceVoice{};opening|=2;}
        else CHECK(pitch<0);
      }
      CHECK(opening==3);
      piano::PerformanceMixer fullMix(22050,board::kPeakAmplitude), bassMix(22050,board::kPeakAmplitude), trebleMix(22050,board::kPeakAmplitude);
      int16_t mixed[256],left[256],right[256];
      fullMix.render(original,{},0,4,false,mixed,128);
      bassMix.render(bass,{},0,4,false,left,128);
      trebleMix.render(treble,{},0,4,false,right,128);
      unsigned both=0;
      for(size_t i=0;i<256;++i) { CHECK(mixed[i]==left[i]+right[i]); both+=left[i]!=0 && right[i]!=0; }
      CHECK(both>100);
      lcd::GameFrame frame;frame.ready=true;frame.guided=true;frame.title=song.title;frame.mode="辅助自由";
      frame.guideMask=1u<<(follow.nextDegree(song)-1);
      lcd::PerformanceLayout layout;
      lcd::performanceGame(frame,layout,song,engine.view(),original,follow.nextEvent());
      CHECK(frame.keys==((1u<<2)|(1u<<3)) && frame.guideMask==(1u<<5));
      CHECK(std::count_if(frame.tiles,frame.tiles+frame.count,[](const auto& tile){return tile.hit;})==2);
      CHECK(std::count_if(frame.tiles,frame.tiles+frame.count,[](const auto& tile){return tile.cue;})==1);
      ScreenCanvas canvas;lcd::GameScreen screen;screen.draw(canvas,frame,10);
      if(!preview.empty()) CHECK(canvas.save(preview+"/guided-original-chord.ppm"));
    });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("confirmed difficulty chooses the automatic playback arrangement for the same song", [&] {
      using C=piano::MenuCommand;
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song; std::string error; CHECK(piano::parseSong(json.data(),json.size(),song,error));
      piano::MenuController menu;
      piano::Engine engine;
      auto start=[&](uint32_t now) {
        CHECK(piano::hasPlaybackArrangement(song,menu.state.playbackArrangement));
        CHECK(engine.start(song,piano::Mode::Play,now,menu.state.playbackArrangement));
        CHECK(piano::isPlaybackArrangement(engine.view(),menu.state.playbackArrangement));
      };
      start(0);
      CHECK(engine.view().midi==64 && !engine.performanceView().enabled);
      CHECK(engine.song().performance.size()==693);
      engine.tick(100);
      CHECK(engine.pause(true,100));
      engine.tick(1100);
      CHECK(engine.view().midi<0 && engine.view().index==0);
      CHECK(engine.pause(false,1100));
      engine.tick(1800);
      CHECK(engine.view().index==1 && engine.view().midi==57 && !engine.performanceView().enabled);

      menu.selectArrangement(C::ChordPractice);
      menu.selectArrangement(C::FreeScore);
      CHECK(!piano::isPlaybackArrangement(engine.view(),menu.state.playbackArrangement));
      start(2000);
      auto performance=engine.performanceView();
      CHECK(performance.enabled && performance.started==2);
      CHECK(std::any_of(performance.voices.begin(),performance.voices.end(),[](const auto& voice){return voice.midi==53;}));
      CHECK(std::any_of(performance.voices.begin(),performance.voices.end(),[](const auto& voice){return voice.midi==76;}));
      CHECK(engine.pause(true,2100));
      CHECK(engine.pause(false,2200) && engine.performanceView().enabled);

      menu.selectArrangement(C::Simple);
      CHECK(!piano::isPlaybackArrangement(engine.view(),menu.state.playbackArrangement));
      start(2300);
      CHECK(!lcd::showsPerformance(engine.performanceView(),engine.view()) && engine.view().midi==64);
      song.performance.clear();
      song.pedal.clear();
      menu.state.page=piano::MenuPage::Launch; menu.state.row=2;
      menu.selectArrangement(menu.input(2));
      CHECK(!piano::hasPlaybackArrangement(song,menu.state.playbackArrangement));
      menu.selectArrangement(menu.input(2));
      start(2400);
      CHECK(!engine.performanceView().enabled && engine.view().midi==64);
    });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("simple melody waterfall shows one input cue and follows queued audio", [&] {
      using S=lcd::GameStyle;
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song; std::string error; CHECK(piano::parseSong(json.data(),json.size(),song,error));
      CHECK(!song.performance.empty() && song.notes[0].pitch==3 && song.notes[1].pitch==6);
      piano::Engine engine; CHECK(engine.start(std::move(song),piano::Mode::Simple,0));
      lcd::GameFrame frame; frame.ready=true; frame.mode=lcd::kSimplePractice; frame.title=engine.song().title;
      lcd::GuidedGame guided;
      lcd::GameScreen screen;
      ScreenCanvas canvas;
      auto update=[&](uint32_t now) {
        const auto clock=engine.view(false);
        guided.update(frame,engine.song(),engine.view(),engine.simpleSoundingNote(),0,now,&clock);
        screen.draw(canvas,frame,now);
      };
      engine.tick(1000);
      update(1000);
      CHECK(frame.guided && !frame.polyphonic && frame.keys==0 && frame.guideMask==(1u<<2));
      CHECK(frame.count>=2 && frame.tiles[0].degree==3 && frame.tiles[0].cue && frame.tiles[0].bottom==S::line);
      CHECK(frame.tiles[1].degree==6 && !frame.tiles[1].cue && !engine.performanceView().enabled);
      CHECK(canvas.pixels[S::keyTop*S::width+S::left+2*S::lane+22]==S::white);
      const auto nextBottom=frame.tiles[1].bottom;
      if (!preview.empty()) CHECK(canvas.save(preview+"/difficulty-simple.ppm"));
      engine.keys(8,1010); // The low 4 in the original chord is not an input cue here.
      update(1010);
      CHECK(engine.view().index==0 && engine.view().midi<0 && frame.guideMask==4 && frame.keys==0);
      engine.keys(0,1020);
      engine.keys(4|64,1030); // A wrong key arriving with the expected key stays silent.
      update(1030);
      const auto view=engine.view();
      CHECK(view.index==1 && view.midi==64 && view.expected==57 && !engine.performanceView().enabled);
      CHECK(frame.guideMask==32 && frame.keys==4 && frame.tiles[0].hit && frame.tiles[1].cue);
      CHECK(frame.tiles[1].bottom==nextBottom);
      engine.keys(0,1040);
      engine.tick(1063);
      update(1063);
      CHECK(std::abs((frame.tiles[1].bottom-nextBottom)-33*S::pixelsPerMs)<.001);
      const auto beforeQueued=frame.progress;
      engine.keys(32,1090);
      update(1090);
      CHECK(engine.view().index==2 && engine.view().midi==64 && frame.guideMask==2 && frame.keys==4);
      CHECK(frame.progress==beforeQueued);
      engine.keys(0,1100);
      engine.tick(1830);
      update(1830);
      CHECK(engine.view().midi==57 && frame.guideMask==2 && frame.keys==32);
      engine.tick(2200);
      update(2200);
      CHECK(engine.view().index==2 && engine.view().midi<0 && frame.keys==0 && frame.guideMask==2);
      const auto waiting=frame.progress;
      engine.tick(3200);
      update(3200);
      CHECK(frame.progress==waiting);
    });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("Time Boils the Rain chord practice sounds each pressed key and shows the remaining key", [&] {
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song;std::string error;CHECK(piano::parseSong(json.data(),json.size(),song,error));
      CHECK(piano::ChordPractice::arrange(song));
      piano::ChordPractice practice;practice.start(song,0);
      piano::View v;v.settings.bpm=song.bpm;
      CHECK(practice.targets()==12 && practice.cueCount()==2);
      practice.step(song,v.settings,4,10);
      CHECK(practice.audio().voices[2].midi==76 && practice.audio().voices[3].midi<0);
      lcd::GameFrame frame; frame.ready=true;frame.guided=true;frame.title=song.title;frame.mode=lcd::kComplexPractice;
      frame.guideMask=practice.targets();
      lcd::PerformanceLayout layout;
      lcd::performanceGame(frame,layout,song,v,practice.display(),practice.firstCue(),practice.cueCount(),true);
      CHECK(frame.keys==4 && frame.guideMask==12);
      CHECK(std::count_if(frame.tiles,frame.tiles+frame.count,[](const auto& n){return n.cue && !n.hit;})==1);
      ScreenCanvas canvas; lcd::GameScreen screen;screen.draw(canvas,frame,10);
      if(!preview.empty()) CHECK(canvas.save(preview+"/chord-one-key.ppm"));
      practice.step(song,v.settings,12,80);
      CHECK(practice.audio().voices[2].midi==76 && practice.audio().voices[3].midi==53);
      CHECK(practice.firstCue()==3 && practice.targets()==1);
      practice.step(song,v.settings,8,100);
      CHECK(practice.audio().voices[2].midi<0 && practice.audio().voices[3].midi==53);
    });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("Time Boils the Rain renders both opening pitches from its firmware timeline", [&] {
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song; std::string error; CHECK(piano::parseSong(json.data(),json.size(),song,error));
      piano::NotationLayout notation; notation.build(song);
      piano::Engine engine; CHECK(engine.start(std::move(song),piano::Mode::Play,0));
      auto view=engine.view(); auto performance=engine.performanceView();
      lcd::GameFrame frame; frame.ready=true; frame.title=engine.song().title; frame.mode="自动播放";
      lcd::PerformanceLayout layout;
      lcd::performanceGame(frame,layout,engine.song(),view,performance);
      CHECK(performance.started==2 && performance.count==693 && frame.keys==((1u<<2)|(1u<<3)));
      CHECK(frame.inputStyle[2]==lcd::GameStyle::colorIndex(1,false) && frame.inputStyle[3]==lcd::GameStyle::colorIndex(-1,false));
      CHECK(std::count_if(frame.tiles,frame.tiles+frame.count,[](const auto& tile){return tile.holding;})==2);
      ScreenCanvas canvas; lcd::GameScreen game; game.draw(canvas,frame,0);
      if (!preview.empty()) CHECK(canvas.save(preview+"/poly-time-rain-opening.ppm"));
      lcd::ScoreScreen score; score.draw(canvas,view,notation.page(engine.song(),view),0,&performance);
      if (!preview.empty()) CHECK(canvas.save(preview+"/poly-time-rain-notes.ppm"));
    });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("Time Boils the Rain playback batches SPI windows and preserves each rendered frame", [&] {
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song; std::string error; CHECK(piano::parseSong(json.data(),json.size(),song,error));
      piano::Engine engine; CHECK(engine.start(std::move(song),piano::Mode::Play,0));
      lcd::GameFrame frame; frame.ready=true; frame.title=engine.song().title; frame.mode="自动播放";
      lcd::GameScreen screen;
      lcd::PerformanceLayout layout;
      std::vector<uint16_t> composed(320*240), previous(320*240);
      lcd::FrameSurface surface(composed.data());
      SpiCanvas display;
      lcd::GameFrame reference;
      ScreenCanvas referenceCanvas;
      unsigned oldWindows=0, oldBytes=0, newWindows=0, newBytes=0;
      std::vector<unsigned> oldFrameBytes, newFrameBytes;
      for (const uint32_t start : {0u,60000u,120000u,145000u}) {
        for (uint32_t now=start;now<start+6000;now+=33) {
          engine.tick(now);
          const auto view=engine.view();
          const auto performance=engine.performanceView();
          CHECK(performance.enabled);
          frame.pauseAvailable=true;
          lcd::performanceGame(frame,layout,engine.song(),view,performance);
          screen.draw(surface,frame,now);
          if (now==start) {
            reference.ready=frame.ready; reference.title=frame.title; reference.mode=frame.mode;
            reference.pauseAvailable=frame.pauseAvailable;
            auto fullScan=performance;
            fullScan.paused=true; // Force the full scan while retaining this frame's active voices.
            lcd::performanceGame(reference,layout,engine.song(),view,fullScan);
            auto referenceScreen=screen;
            referenceScreen.draw(referenceCanvas,reference,now);
            CHECK(referenceCanvas.pixels==composed);
            lcd::presentFrame(display,composed.data(),previous.data(),true);
            CHECK(display.pixels==composed);
            continue;
          }
          unsigned pixels=0, windows=0;
          for (int y=0;y<240;++y) {
            bool inWindow=false;
            for (int x=0;x<320;++x) {
              const int offset=y*320+x;
              const bool changed=composed[offset]!=previous[offset];
              pixels+=changed;
              windows+=changed && !inWindow;
              inWindow=changed;
            }
          }
          oldWindows+=windows;
          oldFrameBytes.push_back(pixels*2+windows*11);
          oldBytes+=oldFrameBytes.back();
          const unsigned before=display.windows;
          const unsigned sent=lcd::presentFrame(display,composed.data(),previous.data());
          CHECK(display.pixels==composed && previous==composed);
          const unsigned batches=display.windows-before;
          newWindows+=batches;
          newFrameBytes.push_back(sent*2+batches*11);
          newBytes+=newFrameBytes.back();
        }
      }
      std::sort(oldFrameBytes.begin(),oldFrameBytes.end());
      std::sort(newFrameBytes.begin(),newFrameBytes.end());
      const size_t p95=(oldFrameBytes.size()-1)*95/100;
      std::printf("TFT frames=%zu windows=%u->%u bus_bytes=%u->%u p95_frame_bytes=%u->%u\n",
        oldFrameBytes.size(),oldWindows,newWindows,oldBytes,newBytes,oldFrameBytes[p95],newFrameBytes[p95]);
      CHECK(newWindows<oldWindows && newBytes<oldBytes && newFrameBytes[p95]<oldFrameBytes[p95]);
    });
  if (const char* fixture=std::getenv("PIANO_REAL_PERFORMANCE_DISPLAY"))
    group("Time Boils the Rain assisted screen matches actual key sounds", [&] {
      std::ifstream input(fixture,std::ios::binary); CHECK(input.good());
      const std::string json((std::istreambuf_iterator<char>(input)),{});
      piano::Song song; std::string error; CHECK(piano::parseSong(json.data(),json.size(),song,error));
      piano::Engine engine; piano::GameAssist assist;
      CHECK(engine.start(std::move(song),piano::Mode::FreeScore,0));
      lcd::GameFrame frame; frame.ready=true; frame.title=engine.song().title; frame.mode="辅助自由";
      lcd::GuidedGame guided;
      guided.update(frame,engine.song(),engine.view(),-1,0,0);
      CHECK(assist.step(engine,4,10).accepted==0);
      assist.step(engine,4,130);
      guided.update(frame,engine.song(),engine.view(),0,120,130);
      CHECK(frame.count>=3 && frame.keys==4 && engine.view().midi==64);
      CHECK(frame.tiles[0].degree==3 && frame.tiles[0].octave==0 && frame.tiles[0].holding && !frame.tiles[1].holding);
      ScreenCanvas canvas; lcd::GameScreen screen; screen.draw(canvas,frame,130);
      if (!preview.empty()) CHECK(canvas.save(preview+"/assisted-time-rain-first.ppm"));
      CHECK(assist.step(engine,36,150).accepted==1);
      assist.step(engine,36,190);
      guided.update(frame,engine.song(),engine.view(),1,40,190);
      CHECK(frame.count>=3 && frame.keys==32 && engine.view().midi==57);
      CHECK(std::count_if(frame.tiles,frame.tiles+frame.count,[](const auto& tile){ return tile.holding && tile.degree==6 && tile.octave==-1; })==1);
      screen.draw(canvas,frame,190);
      if (!preview.empty()) CHECK(canvas.save(preview+"/assisted-time-rain-next.ppm"));
    });
  group("shared game screen clips tiles, aligns keys and clears state transitions", [] {
    for(const char* label:{"音游演奏","待选曲","准备好下一首了吗","选择一首歌曲，开启节奏演奏","长按1.2秒","打开菜单","选择歌曲","音量","HIGH 长按菜单","LOW 暂停","LOW 继续","低高#1234567分连"}){
      const char* p=label;while(*p)CHECK(lcd::findGlyph(lcd::nextCodepoint(p))!=nullptr);
    }
    ScreenCanvas canvas;lcd::GameScreen screen;lcd::GameFrame frame;
    screen.draw(canvas,frame,0);CHECK(canvas.pixels[0]==lcd::GameStyle::background);
    frame.ready=true;frame.title="非常非常长的曲目名称用于检查标题不会覆盖得分";frame.mode="辅助自由";frame.score=99999999;frame.combo=12345;frame.progress=.43f;frame.keys=64|128;
    frame.count=3;frame.tiles[0]={1,0,true,false,70,90,0};frame.tiles[1]={2,1,true,false,20,60,0};frame.tiles[2]={7,-1,false,true,-100,181,.5f};
    screen.draw(canvas,frame,10);CHECK(canvas.pixels[182*320+6]==lcd::GameStyle::mint);CHECK(canvas.pixels[(187+23)*320+(6+6*44+30)]==lcd::GameStyle::accent(2));CHECK(canvas.pixels[(187+23)*320+(6+30)]==lcd::GameStyle::panel);
    const auto play=canvas.pixels;frame.paused=true;screen.draw(canvas,frame,20);CHECK(canvas.pixels!=play);frame.paused=false;screen.draw(canvas,frame,30);CHECK(canvas.pixels==play);
    frame.ready=false;screen.draw(canvas,frame,40);ScreenCanvas fresh;lcd::GameScreen other;other.draw(fresh,frame,40);CHECK(canvas.pixels==fresh.pixels);
  });
  group("game footer shows live sound settings and keeps playback shortcuts readable", [&] {
    using S=lcd::GameStyle;
    for(const char* name:lcd::kInstrumentNames) CHECK(lcd::textWidth(name)<=112);

    ScreenCanvas canvas;
    lcd::GameScreen screen;
    lcd::GameFrame frame;
    const auto checkText=[&](int x,int y,int width,const char* label,uint16_t ink) {
      ScreenCanvas expected;
      expected.fillRect(x,y,width,16,S::background);
      CHECK(lcd::drawText(expected,x,y,label,ink,width,S::background)==lcd::textWidth(label));
      for(int row=y;row<y+16;++row) {
        const auto offset=row*S::width+x;
        CHECK(std::equal(canvas.pixels.begin()+offset,canvas.pixels.begin()+offset+width,expected.pixels.begin()+offset));
      }
    };
    frame.volume=0;
    frame.instrument=1;
    screen.draw(canvas,frame,0);
    checkText(8,222,32,"音量",S::muted);
    checkText(43,222,32,"0%",S::white);
    checkText(89,222,112,"合成钢琴",S::white);
    checkText(208,222,104,"HIGH 长按菜单",S::mint);

    frame.ready=true;
    frame.title="时间煮雨（精编、优化版）";
    frame.mode="自由演奏";
    frame.progress=.26f;
    frame.volume=35;
    frame.instrument=11;
    frame.count=4;
    frame.tiles[0]={2,0,false,false,152,182,0};
    frame.tiles[1]={7,0,false,false,96,156,0};
    frame.tiles[2]={1,0,false,false,64,96,0};
    frame.tiles[3]={5,0,false,false,28,65,0};
    screen.draw(canvas,frame,30);
    checkText(43,222,32,"35%",S::white);
    checkText(89,222,112,"音乐厅钢琴",S::white);
    if(!preview.empty()) CHECK(canvas.save(preview+"/game-footer-free.ppm"));

    frame.mode="自动播放";
    frame.pauseAvailable=true;
    frame.progress=1.f;
    frame.volume=100;
    frame.instrument=4;
    screen.draw(canvas,frame,60);
    checkText(210,20,32,"100%",S::muted);
    checkText(248,20,64,"LOW 暂停",S::mint);
    checkText(43,222,32,"100%",S::white);
    checkText(89,222,112,"Salamander钢琴",S::white);
    checkText(208,222,104,"HIGH 长按菜单",S::mint);
    frame.paused=true;
    screen.draw(canvas,frame,90);
    checkText(248,20,64,"LOW 继续",S::mint);
    if(!preview.empty()) CHECK(canvas.save(preview+"/game-footer-paused.ppm"));
  });
  group("six home sections, navigation, editors and no hidden recording shortcuts", [] {
    using P=piano::MenuPage; using C=piano::MenuCommand;
    piano::MenuController menu;
    CHECK(menu.state.gameDisplay); // Power-on menu state matches the device's game display.
    CHECK(menu.input(2)==C::LoadSongs && menu.state.page==P::SongList);
    CHECK(menu.input(1)==C::SongNext); CHECK(menu.input(0)==C::SongPrevious);
    CHECK(menu.input(2)==C::SongSelect && menu.state.page==P::Performance && menu.state.row==0);
    CHECK(menu.input(1)==C::None && menu.input(2)==C::SelectPerformance && menu.state.page==P::Launch);
    CHECK(menu.state.playbackArrangement==piano::PlaybackArrangement::Original);
    CHECK(menu.input(1)==C::None && menu.input(2)==C::PlayPause);
    CHECK(menu.input(0)==C::None && menu.input(2)==C::ChordPractice);
    CHECK(menu.input(3)==C::None && menu.state.page==P::Performance && menu.state.row==1);
    CHECK(menu.input(3)==C::None && menu.state.page==P::SongList);
    CHECK(menu.input(3)==C::SongCancel && menu.state.page==P::Home);
    menu.state.home=1;
    CHECK(menu.input(2)==C::None && menu.state.page==P::Performance && menu.state.row==1);
    for(auto command:{C::ChordPractice,C::FreeScore,C::GameAssist,C::GameFlow,C::Simple}) {
      CHECK(menu.input(2)==C::SelectPerformance && menu.state.page==P::Launch);
      CHECK(menu.input(2)==command);
      CHECK(menu.input(3)==C::None && menu.state.page==P::Performance);
      CHECK(menu.input(1)==C::None);
    }
    CHECK(menu.input(3)==C::None && menu.state.page==P::Home);
    menu.state.gameDisplay=false;
    CHECK(menu.input(2)==C::None && menu.state.page==P::Performance);
    menu.state.row=3;
    CHECK(menu.input(2)==C::SelectPerformance && menu.input(2)==C::RhythmPractice);
    menu.input(3); menu.input(3);
    const P pages[]={P::SongList,P::Performance,P::Sound,P::VisualMode,P::Connection,P::Device};
    for(int i=2;i<6;++i) {
      menu.state.home=i; CHECK(menu.input(2)==C::None && menu.state.page==pages[i]);
      CHECK(menu.input(3)==C::None && menu.state.page==P::Home && menu.state.home==i);
    }
    menu.input(1); CHECK(menu.state.home==0); CHECK(menu.input(3)==C::Exit);
    menu.state.home=3;CHECK(menu.input(2)==C::None && menu.state.page==P::VisualMode);CHECK(menu.input(1)==C::None);CHECK(menu.input(2)==C::GameDisplay && menu.state.gameDisplay && menu.state.page==P::Home);
    CHECK(menu.input(2)==C::None && menu.state.page==P::VisualMode);CHECK(menu.input(2)==C::NotationDisplay && !menu.state.gameDisplay);CHECK(menu.input(2)==C::None);menu.input(1);menu.input(1);CHECK(menu.input(2)==C::None && menu.state.page==P::Rhythm);menu.input(3);
    menu.state.page=P::Launch; menu.state.row=0;
    menu.state.row=1; CHECK(menu.input(2)==C::PlayPause);
    menu.state.row=2; CHECK(menu.input(2)==C::ToggleArrangement);
    menu.input(1); CHECK(menu.input(2)==C::Lyrics); menu.input(1); CHECK(menu.state.row==0);
    for(int key:{6,8,9,10}) CHECK(menu.input(key)==C::None);
    CHECK(menu.input(4)==C::VolumeDown && menu.input(5)==C::VolumeUp);
    menu.state.page=P::Sound; menu.state.row=0;
    CHECK(menu.input(2)==C::BeginEdit && menu.state.editing);
    CHECK(menu.input(0)==C::Decrease && menu.input(1)==C::Increase);
    CHECK(menu.input(4)==C::None && menu.input(5)==C::None);
    CHECK(menu.input(3)==C::CancelEdit && !menu.state.editing);
    menu.input(2); CHECK(menu.input(2)==C::CommitEdit && !menu.state.editing);
    menu.state.page=P::Rhythm; menu.state.row=0; menu.input(2);
    CHECK(menu.input(4)==C::DecreaseTen && menu.input(5)==C::IncreaseTen);
    CHECK(menu.input(7)==C::CancelEdit && !menu.state.editing && menu.state.page==P::Home);
    for(auto field:{std::make_pair(P::Sound,0),std::make_pair(P::Rhythm,0),std::make_pair(P::Rhythm,2)}) {
      menu.state.page=field.first;menu.state.row=field.second;menu.input(2);
      CHECK(menu.input(0)==C::Decrease && menu.input(1)==C::Increase);menu.input(3);
    }
    menu.state.page=P::Sound;menu.state.row=1;menu.input(2);
    CHECK(menu.input(0)==C::Increase && menu.input(1)==C::Decrease);menu.input(3);
    menu.state.page=P::Rhythm; menu.state.row=1; CHECK(menu.input(2)==C::Metronome);
    menu.state.page=P::Connection; menu.state.networkLines.resize(10);
    for(int i=0;i<19;++i) menu.input(1); CHECK(menu.state.infoOffset==3);
    for(int i=0;i<19;++i) menu.input(0); CHECK(menu.state.infoOffset==0);
    menu.state.page=P::Device; menu.input(2); CHECK(menu.state.page==P::Diagnostics);
    for(int key:{0,1,2,4,5,6}) CHECK(menu.input(key)==C::None && menu.state.page==P::Diagnostics);
    menu.input(3); CHECK(menu.state.page==P::Device); CHECK(menu.input(-1)==C::None);
  });
  group("song deletion requires confirmation and cancellation keeps browser", [] {
    piano::MenuController m; using P=piano::MenuPage; using C=piano::MenuCommand;
    m.state.page=P::SongList; CHECK(m.input(4)==C::AskDelete); CHECK(m.state.page==P::DeleteSong);
    CHECK(m.input(1)==C::None); CHECK(m.input(3)==C::None && m.state.page==P::SongList);
    m.input(4); CHECK(m.input(2)==C::Delete && m.state.page==P::SongList);
  });
  group("circular menus and held navigation repeat only after 1.2 seconds", [] {
    using P=piano::MenuPage; using C=piano::MenuCommand;
    piano::MenuController m; m.input(0);CHECK(m.state.home==5);m.input(1);CHECK(m.state.home==0);
    for(auto page:{P::Launch,P::Sound,P::Rhythm,P::Device}) {
      m.state.page=page;m.state.row=0;m.input(0);CHECK(m.state.row==piano::menuRows(page)-1);
      m.input(1);CHECK(m.state.row==0);
    }
    CHECK(piano::cycleIndex(511,1,512)==0 && piano::cycleIndex(0,-1,512)==511);
    CHECK(piano::cycleIndex(0,1,1)==0 && piano::cycleIndex(0,-1,1)==0 && piano::cycleIndex(0,1,0)==0);
    m.state.page=P::Sound;m.state.row=2;CHECK(piano::menuItem(m.state)==piano::MenuItem::Output);
    CHECK(m.input(2)==C::BeginEdit);CHECK(m.input(1)==C::Increase);CHECK(m.input(3)==C::CancelEdit);
    piano::LocalControls c;c.scan(0x200,0);CHECK(c.scan(0x200,1200)==7);c.scan(0,1201);
    CHECK(c.scan(2,1202)==1);CHECK(c.scan(2,2401)==-1);CHECK(c.scan(2,2402)==1);
    CHECK(c.scan(2,2551)==-1);CHECK(c.scan(2,2552)==1);
    CHECK(c.scan(3,2600)==-1);CHECK(c.scan(2,5000)==-1);c.scan(0,5001);
    CHECK(c.scan(1,5002)==0);CHECK(c.scan(1,6202)==0);c.scan(0,6203);
    CHECK(c.scan(4,6204)==2);CHECK(c.scan(4,10000)==-1);c.scan(0,10001);
    CHECK(c.scan(2,UINT32_MAX-100)==1);CHECK(c.scan(2,1099)==1);c.close();CHECK(c.scan(2,2000)==-1);
  });
  group("volume popup uses 1 percent taps and repeats only while its adjustment key is held", [] {
    using C=piano::MenuCommand; using P=piano::MenuPage;
    piano::LocalControls controls;
    controls.scan(512,0);
    CHECK(controls.scan(512,1200)==7 && controls.menu());
    controls.scan(0,1210);
    piano::MenuController menu;
    menu.state.page=P::Sound; menu.state.row=1;
    CHECK(menu.input(2)==C::BeginEdit && menu.state.editing);
    piano::Engine engine; engine.setVolume(3);
    auto tick=[&](uint16_t keys,uint32_t now,bool editing=true) {
      const auto command=menu.input(controls.scan(keys,now,false,editing));
      if (command==C::Increase || command==C::Decrease)
        engine.setVolume(engine.view().volume+(command==C::Increase?1:-1)*piano::menuEditStep(command));
      return engine.view().volume;
    };
    CHECK(tick(1,1220)==4 && tick(1,1450)==4 && tick(0,1451)==4);
    CHECK(tick(2,1500)==3 && tick(2,1899)==3 && tick(2,1900)==2);
    CHECK(tick(2,1989)==2 && tick(2,1990)==1 && tick(2,2080)==0 && tick(2,2170)==0);
    CHECK(tick(0,2171)==0 && tick(1,2200)==1);
    CHECK(tick(1,2600,false)==1 && tick(0,2601,false)==1);
    engine.setVolume(99);
    CHECK(tick(1,2610)==100 && tick(1,3010)==100 && tick(0,3011)==100);
  });
  group("device output selection preserves phone readiness and expires safely", [] {
    piano::AudioOutput out; CHECK(!out.ready(0)); CHECK(!out.selectPhone(true,0)); CHECK(!out.muted(0));
    CHECK(out.acquire(123,100));CHECK(out.muted(101));
    CHECK(out.selectPhone(false,102));CHECK(!out.muted(102) && out.ready(102));
    CHECK(!out.acquire(456,103));CHECK(out.renew(123,1000));CHECK(!out.muted(1001));
    CHECK(out.selectPhone(true,1002));CHECK(out.muted(1002));
    CHECK(!out.muted(3000) && !out.ready(3000));CHECK(!out.selectPhone(true,3000));
    CHECK(out.selectPhone(false,3000));CHECK(out.acquire(456,3001));CHECK(out.release(456));CHECK(!out.muted(3002));
  });
  group("cached song summaries browse and cancel without full score loading", [] {
    std::vector<piano::MenuSongSummary> songs;
    for(int i=0;i<512;++i) {
      piano::MenuSongSummary song;song.id="song-"+std::to_string(i);song.title="曲目"+std::to_string(i);
      song.bpm=100+i%100;song.bpmProvided=i%2==0;songs.push_back(song);
    }
    piano::MenuController menu;menu.state.page=piano::MenuPage::SongList;
    const size_t selected=11;
    piano::showMenuSong(menu.state,songs,selected,true);
    for(size_t index=0;index<songs.size();++index) {
      piano::showMenuSong(menu.state,songs,index,true);
      CHECK(menu.state.songTitle==songs[index].title && menu.state.songBpm==songs[index].bpm);
      CHECK(menu.state.songBpmProvided==songs[index].bpmProvided && menu.state.songCount==512);
      CHECK(menu.state.songTop>=0 && menu.state.songTop<=508);
      CHECK(menu.state.songRows[index-menu.state.songTop]==songs[index].title);
    }
    CHECK(menu.input(3)==piano::MenuCommand::SongCancel);
    piano::showMenuSong(menu.state,songs,selected,false);CHECK(menu.state.songTitle==songs[selected].title);
    songs[selected].title="更新曲名";songs[selected].bpm=77;
    piano::showMenuSong(menu.state,songs,selected,true);CHECK(menu.state.songTitle=="更新曲名" && menu.state.songBpm==77);
    songs.resize(1);piano::showMenuSong(menu.state,songs,0,true);
    CHECK(menu.state.songCount==1 && menu.state.songTop==0 && menu.state.songRows[1].empty() && menu.state.songPosition=="1/1");
  });
  group("rhythm degree matching restores score pitch and rejects early presses", [] {
    piano::Song s=song({{73,.5},{49,.5},{-1,.5},{74,.5}}); s.notes[0].pitch=1;s.notes[1].pitch=1;s.notes[3].pitch=2;
    piano::Engine e; CHECK(e.start(s,piano::Mode::Rhythm,0));
    e.keys(2,1); CHECK(e.view().midi==-1 && e.view().index==0);
    e.keys(0,2);e.keys(1,3);CHECK(e.view().midi==73); const auto trigger=e.view().trigger;e.keys(0x201,4);CHECK(e.view().trigger==trigger); e.keys(0,5); e.tick(253);
    CHECK(e.view().index==1);e.keys(1,254);CHECK(e.view().midi==49);e.tick(504);e.keys(0,505);e.tick(754);
    e.keys(2,755);CHECK(e.view().midi==74);e.tick(1005);CHECK(e.view().finished && e.view().midi==-1);
    piano::Engine free;CHECK(free.start(s,piano::Mode::FreeScore,0));free.keys(2,1);CHECK(free.view().midi==62 && free.view().count==4);
    piano::Song builtin;piano::builtinSong("demo-scale",builtin);e.keys(0,2000);CHECK(e.start(builtin,piano::Mode::Rhythm,2000));e.keys(1,2001);CHECK(e.view().midi==60);
    e.keys(0,2010);e.keys(2,2020);e.tick(2601);CHECK(e.view().index==1 && e.view().midi==-1);
    e.keys(2,2602);CHECK(e.view().midi==-1);e.keys(0,2603);e.keys(2,2604);CHECK(e.view().midi==62);
  });
  group("simple accepts ahead, serializes durations and rests, and never retriggers current audio", [] {
    piano::Song s=song({{73,.5},{49,.5},{-1,.5},{74,.5}});s.notes[0].pitch=1;s.notes[1].pitch=1;s.notes[3].pitch=2;
    piano::Engine e;CHECK(e.start(s,piano::Mode::Simple,0));
    e.keys(2,1);CHECK(e.view().index==0 && e.view().midi==-1);
    e.keys(0,2);e.keys(1,3);CHECK(e.view().index==1 && e.view().expected==49 && e.view().midi==73);
    const auto trigger=e.view().trigger;
    e.keys(0x201,4);CHECK(e.view().index==1 && e.view().trigger==trigger);
    e.keys(0,5);e.keys(1,6);e.keys(2,7);CHECK(e.view().index==4 && !e.view().finished && e.view().midi==73 && e.view().trigger==trigger);
    e.tick(252);CHECK(e.view().midi==73);e.tick(253);CHECK(e.view().midi==49 && e.view().trigger!=trigger);
    e.tick(503);CHECK(e.view().midi==-1);e.tick(753);CHECK(e.view().midi==74);
    e.tick(1003);CHECK(e.view().finished && e.view().midi==-1);
    e.keys(0,1100);CHECK(e.start(s,piano::Mode::Simple,1100));e.tick(2000);CHECK(e.view().index==0 && e.view().midi==-1);
  });
  group("short repeated notes stay distinct while different pitches connect at the score boundary", [] {
    const auto json=payload(note(5,.5)+","+note(5,.125)+","+note(3,1),"73");
    piano::Song s; std::string error; CHECK(piano::parseSong(json.data(),json.size(),s,error));
    CHECK(s.notes[1].beats==.125);
    for(auto mode : {piano::Mode::Play,piano::Mode::Simple}) {
      piano::Engine engine; CHECK(engine.start(s,mode,0));
      if(mode==piano::Mode::Simple) {
        engine.keys(16,0); engine.keys(0,25); engine.keys(16,50);
        engine.keys(0,75); engine.keys(4,100);
      }
      engine.tick(398); CHECK(engine.view().midi==67);
      engine.tick(405); CHECK(engine.view().midi==-1);
      engine.tick(411); CHECK(engine.view().midi==67 && engine.view(false).index==1);
      engine.tick(513); CHECK(engine.view().midi==67);
      engine.tick(514); CHECK(engine.view().midi==64 && engine.view(false).index==2);
    }
  });
  group("simple queue pause, tempo, stop, repeat and rollover", [] {
    piano::Engine e;auto s=song({{60,1},{62,1}});CHECK(e.start(s,piano::Mode::Simple,0));
    e.keys(1,10);e.keys(2,20);CHECK(e.pause(true,100));e.tick(1000);CHECK(e.view().midi==-1 && !e.view().finished);
    CHECK(e.pause(false,1100));e.tick(1509);CHECK(!e.view().finished);e.tick(1510);CHECK(e.view().midi==62);
    auto settings=e.view().settings;settings.bpm=60;e.settings(settings,1510);e.tick(2509);CHECK(!e.view().finished);e.tick(2510);CHECK(e.view().finished);
    e.keys(0,2600);CHECK(e.start(s,piano::Mode::Simple,2600));e.keys(1,2601);e.keys(2,2602);e.stop();e.keys(0,2603);e.tick(5000);CHECK(e.view().mode==piano::Mode::Manual && e.view().midi==-1);
    CHECK(e.start(s,piano::Mode::Simple,6000));settings=e.view().settings;settings.repeat=true;e.settings(settings,6000);
    e.keys(1,6001);e.keys(2,6002);e.tick(7001);CHECK(e.view().mode==piano::Mode::Simple && e.view().index==0 && e.view().midi==-1);
    settings.repeat=false;e.settings(settings,7002);e.keys(0,7002);CHECK(e.start(s,piano::Mode::Simple,UINT32_MAX-100));
    e.keys(1,UINT32_MAX-99);e.keys(2,UINT32_MAX-98);e.tick(900);CHECK(e.view().finished);
  });
  group("frame composition presents final pixels only and idle sends no pixels", [&preview] {
    std::vector<uint16_t> composed(320*240),previous(320*240,0xffff);
    lcd::FrameSurface surface(composed.data()); lcd::MenuScreen renderer; piano::MenuState state;piano::View v;SpiCanvas lcd;
    renderer.draw(surface,state,v,0);composed[0]=0xffff;lcd::presentFrame(lcd,composed.data(),previous.data(),true);CHECK(lcd.pixels==composed);
    CHECK(lcd.windows==1 && lcd.transactions==1);
    const auto original=lcd.pixels;const auto writes=lcd.writes;
    state.home=1;renderer.draw(surface,state,v,33);
    unsigned changed=0;for(size_t i=0;i<composed.size();++i) if(original[i]!=composed[i])++changed;
    const unsigned sent=lcd::presentFrame(lcd,composed.data(),previous.data());
    CHECK(sent>=changed && sent<320*240/2);CHECK(lcd.writes-writes==sent);CHECK(lcd.pixels==composed);
    CHECK(lcd.transactions==2);CHECK(lcd::presentFrame(lcd,composed.data(),previous.data())==0);CHECK(lcd.transactions==2);
    state.page=piano::MenuPage::Performance;renderer.draw(surface,state,v,66);lcd::presentFrame(lcd,composed.data(),previous.data());CHECK(lcd.pixels==composed);
    if(!preview.empty())CHECK(lcd.save(preview+"/menu-performance.ppm"));
    state.row=2;renderer.draw(surface,state,v,99);lcd::presentFrame(lcd,composed.data(),previous.data());
    if(!preview.empty())CHECK(lcd.save(preview+"/menu-performance-rhythm.ppm"));
  });
  group("phone audio preserves note-off, repeated trigger, settings and overrun reset", [] {
    piano::AudioEvents stream;piano::View v;std::array<piano::AudioEvent,64> events;bool reset=false;
    CHECK(stream.read(0,events,reset)==0);stream.capture(v,0);CHECK(stream.read(0,events,reset)==1 && reset);
    const auto first=events[0].seq;v.midi=72;v.trigger=1;stream.capture(v,10);stream.capture(v,11);
    v.midi=-1;stream.capture(v,20);v.midi=72;v.trigger=2;stream.capture(v,25);
    CHECK(stream.read(first,events,reset)==3 && !reset);CHECK(events[0].midi==72 && events[1].midi==-1 && events[2].trigger==2);
    for(unsigned i=0;i<100;++i) {v.trigger++;stream.capture(v,100+i);}
    CHECK(stream.read(first,events,reset)==1 && reset);CHECK(stream.read(UINT32_MAX,events,reset)==1 && reset);
  });
  group("missing lyric data is explicit on production score screen", [] {
    piano::Song s=song({{60,1}});piano::Engine e;CHECK(e.start(s,piano::Mode::FreeScore,0));
    auto settings=e.view().settings;settings.showLyrics=true;e.settings(settings,0);
    piano::NotationLayout layout;layout.build(s);auto page=layout.page(s,e.view());CHECK(!page.hasLyrics);
    lcd::ScoreScreen screen;ScreenCanvas canvas;screen.draw(canvas,e.view(),page,0);
    bool ink=false;for(int y=103;y<121;++y)for(int x=8;x<312;++x) if(canvas.pixels[y*320+x]==lcd::ScoreScreen::kMint) ink=true;
    CHECK(ink);s.notes[0].lyric="琴";layout.build(s);CHECK(layout.page(s,e.view()).hasLyrics);
  });
  group("parameter cancellation is per field; phone writes end conflicting edits", [] {
    using I=piano::MenuItem; piano::MenuEdit edit; piano::View v;
    v.volume=10; v.settings.bpm=90; v.settings.instrument=1; v.settings.transpose=-3;
    for(auto field:{I::Volume,I::Instrument,I::Tempo,I::Transpose}) {
      edit.begin(field,v); const int old=edit.original;
      auto changed=v; changed.settings.bpm=100; changed.settings.instrument=2; changed.volume=55; changed.settings.transpose=4;
      CHECK(edit.value(changed)!=old && edit.active);
      edit.remote(field==I::Volume?I::Tempo:I::Volume); CHECK(edit.active && edit.original==old);
      edit.remote(field); CHECK(!edit.active); // cancel must not restore this field over phone state
      edit.begin(field,v); edit.finish(); CHECK(!edit.active);
    }
  });
  group("instrument browsing stages selection without changing the active voice", [] {
    piano::MenuController menu; piano::MenuEdit edit; piano::View active;
    active.settings.instrument=2;
    menu.state.page=piano::MenuPage::Sound;menu.state.row=0;
    CHECK(menu.input(2)==piano::MenuCommand::BeginEdit);
    edit.begin(piano::MenuItem::Instrument,active);
    CHECK(edit.pendingInstrument==3);
    for(int id=4;id<=piano::kMaxInstrument;++id) {
      CHECK(menu.input(1)==piano::MenuCommand::Increase);edit.browseInstrument(1);
      CHECK(edit.pendingInstrument==id && active.settings.instrument==2 && edit.original==2);
    }
    edit.browseInstrument(1);CHECK(edit.pendingInstrument==3);
    CHECK(menu.input(3)==piano::MenuCommand::CancelEdit);edit.finish();CHECK(active.settings.instrument==2);
    menu.input(2);edit.begin(piano::MenuItem::Instrument,active);
    CHECK(edit.pendingInstrument==3);
    CHECK(menu.input(0)==piano::MenuCommand::Decrease);edit.browseInstrument(-1);CHECK(edit.pendingInstrument==11);
    edit.browseInstrument(-1);CHECK(edit.pendingInstrument==10);
    edit.browseInstrument(4);CHECK(edit.pendingInstrument==5 && active.settings.instrument==2);
    CHECK(menu.input(2)==piano::MenuCommand::CommitEdit);
    active.settings.instrument=edit.pendingInstrument;edit.finish();CHECK(active.settings.instrument==5);
    edit.begin(piano::MenuItem::Instrument,active);edit.browseInstrument(1);
    edit.remote(piano::MenuItem::Instrument);CHECK(!edit.active); // stale preview must not overwrite phone changes
    CHECK(lcd::textWidth(lcd::kInstrumentEditKeys)<=300);
    lcd::MenuScreen renderer;ScreenCanvas canvas;
    menu.state.editing=true;menu.state.previewInstrument=5;
    renderer.draw(canvas,menu.state,active,0);const auto before=canvas.pixels;
    menu.state.previewInstrument=6;renderer.draw(canvas,menu.state,active,33);CHECK(before!=canvas.pixels);
    lcd::MenuScreen fresh;ScreenCanvas expected;fresh.draw(expected,menu.state,active,33);CHECK(canvas.pixels==expected.pixels);
    const auto writes=canvas.writes;renderer.draw(canvas,menu.state,active,66);CHECK(canvas.writes==writes);
  });
  group("TFT six-section pages and editors leave no stale pixels or idle writes", [&] {
    using P=piano::MenuPage;
    piano::MenuState state; piano::View v; v.volume=10;
    state.songTitle="欢乐颂"; state.songPosition="2/12"; state.songCount=12; state.songIndex=1;
    state.songRows={"内置音阶","欢乐颂","送别","知我"}; state.playingTitle="送别";
    state.networkLines={"热点已启动","热点名称","HarmonicaPlayer-ESP32","热点密码","harmonica","连接设备数：0","手机先连接上述热点","App填写地址，或浏览器打开","http://192.168.4.1/"};
    state.deviceLines={"固件：0.6.0-board2_1","存储：存储卡+内置备份","声音就绪","按键任务就绪"};
    lcd::MenuScreen renderer; ScreenCanvas canvas; uint32_t now=100;
    auto render=[&] {
      renderer.draw(canvas,state,v,now); auto writes=canvas.writes; renderer.draw(canvas,state,v,now); CHECK(writes==canvas.writes);
      lcd::MenuScreen fresh; ScreenCanvas expected; fresh.draw(expected,state,v,now); CHECK(canvas.pixels==expected.pixels);
      now+=33;
    };
    for(int i=0;i<6;++i) { state.home=i; render(); }
    state.home=1; render(); if(!preview.empty()) CHECK(canvas.save(preview+"/menu-home.ppm"));
    state.phoneReady=true;state.phoneOutput=true;render();
    if(!preview.empty()) CHECK(canvas.save(preview+"/menu-home-phone.ppm"));
    state.phoneOutput=false;state.phoneReady=false;render();
    const P pages[]={P::Launch,P::Sound,P::Rhythm,P::Connection,P::Device,P::SongList,P::Diagnostics,P::Performance,P::DeleteSong};
    for(auto page:pages) {
      state.page=page; state.row=0; state.infoOffset=0; render();
      if(!preview.empty()) { char name[40];std::snprintf(name,sizeof(name),"/menu-page-%d.ppm",static_cast<int>(page));CHECK(canvas.save(preview+name)); }
      if(page==P::Sound) {
        state.row=2;state.editing=true;state.previewOutput=1;render();
        if(!preview.empty()) CHECK(canvas.save(preview+"/menu-output-phone.ppm"));
        state.phoneReady=true;render();state.phoneOutput=true;render();
        state.previewOutput=0;render();state.phoneReady=false;state.phoneOutput=false;state.editing=false;state.previewOutput=-1;
      }
      if(page==P::Sound || page==P::Rhythm) {
        for(int row=0;row<piano::menuRows(page);++row) {
          state.row=row; render();
          if(piano::menuItem(state)==piano::MenuItem::Metronome) continue;
          state.editing=true; render();
          if(!preview.empty()) { char name[48];std::snprintf(name,sizeof(name),"/menu-edit-%d-%d.ppm",static_cast<int>(page),row);CHECK(canvas.save(preview+name)); }
          v.volume=100; v.settings.bpm=400; v.settings.transpose=-12; v.settings.instrument=2; render();
          state.editing=false; render();
        }
      }
    }
    state.page=P::Sound; state.row=0; state.editing=true;
    for(int id:{0,3,4,7,10,4,0}) { v.settings.instrument=id; render(); }
    state.editing=false; render();
    state.page=P::Connection; state.infoOffset=2; render();
    state.page=P::Launch; state.row=3; render();
    v.settings.showLyrics=true; render();
    if(!preview.empty()) CHECK(canvas.save(preview+"/menu-lyrics.ppm"));
    v.settings.showLyrics=false; render();
    state.page=P::Connection;
    if(!preview.empty()) CHECK(canvas.save(preview+"/menu-connection-scroll.ppm"));
    state.page=P::Diagnostics; for(uint16_t mask:{0u,1u,2u,8u,0x380u,0x3ffu,0u}) { state.keyMask=mask; render(); }
    state.page=P::SongList; state.songTitle=std::string(240,'A'); now=0; render(); now=2400; render();
    if(!preview.empty()) CHECK(canvas.save(preview+"/menu-long-title.ppm"));
    state.songCount=0; render();
    state.notice="设置已应用，但保存失败"; render(); state.notice.clear(); render();
    state.page=P::Home; render(); renderer.reset(); render();
    for(const auto& line:lcd::MenuScreen::wrap(std::string(63,'A'))) CHECK(lcd::textWidth(line.c_str())<=296);
    std::string chinese;for(int i=0;i<80;++i)chinese+="琴";
    for(const auto& line:lcd::MenuScreen::wrap(chinese)) CHECK(lcd::textWidth(line.c_str())<=296);
  });
  group("song titles render both pairs of Chinese book title marks", [&] {
    const std::string json=R"({"id":"title-marks","title":"王力宏\u300a爱错\u300b","notes":[{"pitch":5,"octave":0,"sharp":false,"durationBeats":1}]})";
    piano::Song parsed, restored;
    std::string error;
    CHECK(piano::parseSong(json.data(),json.size(),parsed,error) && parsed.title=="王力宏《爱错》");
    const auto encoded=piano::encodeSong(parsed);
    CHECK(piano::parseSong(encoded.data(),encoded.size(),restored,error) && restored.title==parsed.title);
    for (uint32_t codepoint : {0x3008u,0x3009u,0x300au,0x300bu}) {
      const auto* glyph=lcd::findGlyph(codepoint);
      CHECK(glyph && glyph->width==16 && std::any_of(std::begin(glyph->bitmap),std::end(glyph->bitmap),[](uint8_t row){return row!=0;}));
    }
    CHECK(lcd::textWidth("《爱错》")==64 && lcd::textWidth("〈打上花火〉")==96);
    piano::MenuState state;
    state.page=piano::MenuPage::SongList;
    piano::showMenuSong(state,{{parsed.id,parsed.title},{"fireworks","〈打上花火〉"}},0,true);
    ScreenCanvas canvas;
    lcd::MenuScreen screen;
    screen.draw(canvas,state,{},0);
    if(!preview.empty()) CHECK(canvas.save(preview+"/menu-title-marks.ppm"));
  });
  group("valid phone payload, UTF-8, rests, accidentals, nullable BPM", [] {
    piano::Song s; std::string err; auto data=payload(note()+","+note(0,.25)+","+note(7,1,0,true),"null");
    CHECK(piano::parseSong(data.data(),data.size(),s,err)); CHECK(err.empty()); CHECK(s.bpm==120);
    CHECK(!s.bpmProvided);
    CHECK(s.title=="\xe7\x9f\xa5\xe6\x88\x91"); CHECK(s.notes.size()==3); CHECK(s.notes[0].midi==60); CHECK(s.notes[1].midi==-1); CHECK(s.notes[2].midi==72);
    CHECK(std::fabs(piano::frequency(69)-440)<.001); CHECK(std::fabs(piano::frequency(60)-261.625565)<.001);
    CHECK(piano::noteMidi(1,-1,false)==48); CHECK(piano::noteMidi(7,1,true)==84);
    data=payload(note(),"57");CHECK(piano::parseSong(data.data(),data.size(),s,err));CHECK(s.bpmProvided&&s.bpm==57);
  });
  group("reject malformed JSON, duplicate fields and path/control injection", [] {
    std::vector<std::string> bad={"", "[]", "{}", payload(note())+"oops", "["+payload(note())+"]",
      "{\"id\":\"ok\",\"id\":\"other\",\"title\":\"t\",\"notes\":["+note()+"]}",
      "{\"id\":\"../x\",\"title\":\"t\",\"notes\":["+note()+"]}",
      "{\"id\":\"ok\\u0000bad\",\"title\":\"t\",\"notes\":["+note()+"]}", payload("{\"pitch\":1,\"pitch\":2,\"octave\":0,\"sharp\":false,\"durationBeats\":1}")};
    for (const auto& data:bad) { piano::Song s; s.id="unchanged"; std::string err; CHECK(!piano::parseSong(data.data(),data.size(),s,err)); CHECK(s.id=="unchanged"); CHECK(!err.empty()); }
    CHECK(!piano::safeId("x/y")); CHECK(!piano::safeId("x%2fy")); CHECK(piano::safeId("abc-XYZ_012"));
    auto data=payload(note()); data.insert(3,1,'\0'); piano::Song s; std::string e; CHECK(!piano::parseSong(data.data(),data.size(),s,e));
    // The API accepts a sized buffer, with no readable byte after it assumed.
    data=payload(note()); std::vector<char> buffer(data.begin(),data.end()); CHECK(piano::parseSong(buffer.data(),buffer.size(),s,e));
  });
  group("note and tempo bounds, all-or-nothing decode", [] {
    for (const auto& data:std::vector<std::string>{payload(note(8)),payload(note(-1)),payload(note(1,0)),payload(note(1,-1)),payload(note(1,65)),payload(note(1,1,2)),payload(note(),"19"),payload(note(),"401"),payload(note(),"120.5"),payload("{\"pitch\":1,\"octave\":0,\"sharp\":\"false\",\"durationBeats\":1}"),payload("{\"pitch\":1,\"octave\":0,\"sharp\":false,\"durationBeats\":1e999}")}) {
      piano::Song s; std::string e; CHECK(!piano::parseSong(data.c_str(),data.size(),s,e));
    }
    std::string notes;
    for(size_t i=0;i<piano::kMaxNotes;i++){if(i)notes+=',';notes+=note();}
    auto data=payload(notes); piano::Song s; std::string e; CHECK(piano::parseSong(data.c_str(),data.size(),s,e)); CHECK(s.notes.size()==1024);
    data=payload(notes+","+note()); CHECK(!piano::parseSong(data.c_str(),data.size(),s,e)); CHECK(s.notes.size()==1024);
  });
  group("bounded JSON nesting without confusing quoted braces", [] {
    auto data=payload(note()); data.pop_back(); data+=",\"lyric\":\"braces { [ \\\" quoted\"}";
    piano::Song s;std::string e;CHECK(piano::parseSong(data.data(),data.size(),s,e));
    data=payload(note()); data.pop_back();data+=",\"extra\":"+std::string(20,'[')+"0"+std::string(20,']')+"}";
    CHECK(!piano::parseSong(data.data(),data.size(),s,e));
    data=std::string(120*1024+1,' ');CHECK(!piano::parseSong(data.data(),data.size(),s,e));
  });
  group("phone command DTO types, volume limits and stop contract", [] {
    for(int value:{0,80,100}) { auto data="{\"volumePercent\":"+std::to_string(value)+"}"; int volume=-1; CHECK(piano::readVolume(data.data(),data.size(),volume)); CHECK(volume==value); }
    for(auto data:{"{}","{\"volumePercent\":-1}","{\"volumePercent\":101}","{\"volumePercent\":1.5}","{\"volumePercent\":\"80\"}","{\"volumePercent\":true}","{\"volumePercent\":1,\"volumePercent\":2}"}) { int v=-1; CHECK(!piano::readVolume(data,std::strlen(data),v)); }
    for(auto data:{"{\"id\":\"song-1\"}","{\"songId\":\"song-1\"}"}) {std::string id;CHECK(piano::readSongId(data,std::strlen(data),id));CHECK(id=="song-1");}
  });
  group("seven white keys, modifiers alone and simultaneous opposites", [] {
    piano::Engine e;
    for(int i=0;i<7;i++){e.keys(0,0);e.keys(1u<<i,25);CHECK(e.view().midi==piano::noteMidi(i+1,0,false));}
    e.keys(1u<<7,30);CHECK(e.view().midi==-1);
    e.keys(1u<<8,31);CHECK(e.view().midi==-1);
    e.keys(1u<<9,32);CHECK(e.view().midi==-1);
    e.keys(1|(1u<<7),33);CHECK(e.view().midi==48);
    e.keys(1|(1u<<7)|(1u<<8),34);CHECK(e.view().midi==49);
    e.keys(1|(1u<<9),35);CHECK(e.view().midi==72);
    e.keys(1|(1u<<7)|(1u<<9)|(1u<<8),36);CHECK(e.view().midi==61);
    e.keys(0,37);CHECK(e.view().midi==-1);
  });
  group("last-pressed note priority and release returns to previous held key", [] {
    piano::Engine e;e.keys(1,0);e.keys(3,1);CHECK(e.view().midi==62);e.keys(1,2);CHECK(e.view().midi==60);
    e.keys(5,3);CHECK(e.view().midi==64);e.keys(4,4);CHECK(e.view().midi==64);e.keys(0,5);CHECK(e.view().midi==-1);
  });
  group("absolute playback timing, rests, delayed service and millis rollover", [] {
    piano::Engine e;CHECK(e.start(song({{60,.5},{-1,.5},{62,1}}),piano::Mode::Play,100));
    CHECK(e.view().midi==60);e.tick(337);CHECK(e.view().midi==60);e.tick(349);CHECK(e.view().midi==-1);e.tick(350);CHECK(e.view().midi==-1);CHECK(e.view().index==1);
    e.tick(600);CHECK(e.view().midi==62);e.tick(1100);CHECK(e.view().finished);CHECK(e.view().mode==piano::Mode::Manual);CHECK(e.view().midi==-1);
    CHECK(e.start(song({{60,1},{62,1},{64,1}}),piano::Mode::Play,0));e.tick(1200);CHECK(e.view().index==2);e.tick(10000);CHECK(e.view().finished);
    CHECK(e.start(song({{60,1},{62,1}}),piano::Mode::Play,UINT32_MAX-100));e.tick(399);CHECK(e.view().index==1);CHECK(e.view().midi==62);
  });
  group("manual takeover, stop and runtime volume", [] {
    piano::Engine e;CHECK(e.start(song({{60,1}}),piano::Mode::Play,0));e.keys(2,1);CHECK(e.view().mode==piano::Mode::Manual);CHECK(e.view().midi==62);
    e.keys(0,2);e.stop();CHECK(e.view().midi==-1);e.setVolume(0);CHECK(e.view().volume==0);e.setVolume(100);CHECK(e.view().volume==100);
    CHECK(!e.start(song({}),piano::Mode::Play,0));
  });
  group("teach validates exact pitch, ignores wrong presses and requires release", [] {
    piano::Engine e;CHECK(e.start(song({{60,1},{60,1},{61,1}}),piano::Mode::Teach,0));
    e.keys(2,1);CHECK(e.view().midi==-1);CHECK(e.view().index==0);e.keys(0,2);e.keys(1,3);CHECK(e.view().midi==60);
    e.tick(503);CHECK(e.view().index==1);CHECK(e.view().midi==-1);e.keys(1,504);CHECK(e.view().midi==-1);
    e.keys(0,505);e.keys(1,506);CHECK(e.view().midi==60);e.tick(1006);CHECK(e.view().index==2);
    e.keys(0,1007);e.keys(1|(1u<<8),1008);CHECK(e.view().midi==61);
    e.keys(0,1009);e.tick(1508);CHECK(e.view().finished);
    CHECK(e.start(song({{-1,.5},{62,1}}),piano::Mode::Teach,2000));e.tick(2250);CHECK(e.view().expected==62);CHECK(e.view().midi==-1);
    e.stop();e.keys(0,3000);CHECK(e.start(song({{61,1}}),piano::Mode::Teach,3000));e.keys(1,3001);CHECK(e.view().midi==-1);e.keys(1|(1u<<8),3002);CHECK(e.view().midi==61);
  });
  group("actual stereo synthesis: pitch, amplitude, volume scaling and silence", [] {
    piano::AudioSynth synth(22050,600);std::vector<int16_t> samples(22050*2);synth.render(69,100,samples.data(),22050);
    int crossings=0;double power=0;
    for(size_t i=1000;i<22050;i++){CHECK(samples[2*i]==samples[2*i+1]);CHECK(std::abs(int(samples[2*i]))<=600);power+=double(samples[2*i])*samples[2*i];if(samples[2*i]>0&&samples[2*(i-1)]<=0)++crossings;}
    double measured=crossings*22050.0/(22050-1000);CHECK(std::abs(measured-440)<2);CHECK(std::sqrt(power/(22050-1000))>125&&std::sqrt(power/(22050-1000))<127);
    synth.render(69,50,samples.data(),22050);power=0;for(size_t i=1000;i<22050;i++)power+=double(samples[2*i])*samples[2*i];double rms=std::sqrt(power/(22050-1000));CHECK(rms>62&&rms<64);
    synth.render(69,0,samples.data(),22050);for(size_t i=1000;i<22050;i++)CHECK(samples[2*i]==0&&samples[2*i+1]==0);
    synth.render(69,100,samples.data(),22050);synth.render(-1,100,samples.data(),22050);for(size_t i=1000;i<22050;i++)CHECK(samples[2*i]==0);
    std::printf("SYNTH frequency=%.3fHz half-volume-rms=%.3f\n",measured,rms);
  });
  group("production maximum volume: full output, monotonic RMS and unclipped mixed timbres", [] {
    const int peak = board::kPeakAmplitude;
    CHECK(peak > 28000 && peak <= 32767);
    std::vector<int16_t> samples(board::kSampleRate * 2);
    double previous = 0;
    for (int volume : {10, 50, 100}) {
      piano::AudioSynth synth(board::kSampleRate, peak);
      synth.render(48, volume, samples.data(), board::kSampleRate);
      double power = 0; int measuredPeak = 0;
      for (size_t i = 1000; i < board::kSampleRate; ++i) {
        const int value = samples[i * 2];
        CHECK(value == samples[i * 2 + 1] && std::abs(value) <= peak);
        measuredPeak = std::max(measuredPeak, std::abs(value)); power += double(value) * value;
      }
      const double rms = std::sqrt(power / (board::kSampleRate - 1000));
      CHECK(rms > previous && std::abs(rms - peak * volume / 100.0 / std::sqrt(2.0)) < peak * .002);
      CHECK(measuredPeak >= peak * volume / 100.0 - 2); previous = rms;
      synth.render(48, 0, samples.data(), board::kSampleRate);
      for (size_t i = 1000; i < board::kSampleRate; ++i) CHECK(samples[i * 2] == 0 && samples[i * 2 + 1] == 0);
    }
    for (int instrument = 0; instrument < 3; ++instrument) for (int midi : {36, 69, 96}) {
      piano::AudioSynth synth(board::kSampleRate, peak);
      synth.render(midi, 100, samples.data(), board::kSampleRate, instrument, true, 1);
      for (size_t i = 0; i < samples.size(); ++i) CHECK(std::abs(int(samples[i])) <= peak);
    }
    std::printf("OUTPUT peak=%d (100%%), sine RMS=%.1f; electrical power is not hardware-measured\n", peak, previous);
  });
  group("pure-tone octave balance: amplitude ratios, unchanged pitch, all-semitone slope", [] {
    const unsigned rate = board::kSampleRate; const int peak = board::kPeakAmplitude;
    std::vector<int16_t> samples(rate * 2);
    double reference = 0, previous = 1e9;
    for (int midi = 36; midi <= 96; ++midi) {
      piano::AudioSynth synth(rate, peak); synth.render(midi, 10, samples.data(), rate, 0);
      double power = 0; int crossings = 0;
      for (size_t i = 1000; i < rate; ++i) {
        CHECK(samples[i*2] == samples[i*2+1] && std::abs(int(samples[i*2])) <= peak);
        power += double(samples[i*2]) * samples[i*2];
        if (samples[i*2] > 0 && samples[(i-1)*2] <= 0) ++crossings;
      }
      const double rms = std::sqrt(power / (rate - 1000));
      CHECK(std::abs(crossings * double(rate) / (rate - 1000) - piano::frequency(midi)) < 2);
      if (midi == 48) reference = rms;
      if (midi < 48) CHECK(std::abs(rms - 2027.0) < 4); // No boost to low notes.
      if (midi > 48) CHECK(rms < previous); // Smooth at every semitone, not just octave switches.
      if (midi == 60) CHECK(std::abs(rms / reference - .5) < .003);
      if (midi == 72) CHECK(std::abs(rms / reference - .25) < .003);
      if (midi == 84) CHECK(std::abs(rms / reference - .125) < .003);
      if (midi == 96) CHECK(std::abs(rms / reference - .0625) < .003);
      previous = rms;
      if (midi % 12 == 0) std::printf("BALANCE midi=%d volume10 digital RMS=%.2f, acoustic loudness unmeasured\n", midi, rms);
    }
    // The correction is specific to the reported pure-tone issue. Other timbres
    // keep their harmonic/decay design; neither loses a complete octave of gain.
    for (int instrument : {1,2}) {
      double low = 0;
      for (int midi : {48,72}) {
        piano::AudioSynth synth(rate, peak); synth.render(midi, 10, samples.data(), rate, instrument);
        double power = 0; for (size_t i = 1000; i < rate; ++i) power += double(samples[i*2])*samples[i*2];
        const double rms = std::sqrt(power / (rate - 1000));
        if (midi == 48) low = rms; else CHECK(rms / low > .97 && rms / low < 1.03);
      }
    }
  });
  group("storage commit preserves previous version across injected failures", [] {
    const std::string path="/songs/song-1.json",body="new",old="old";
    for(const auto& fault:std::vector<std::string>{"write:"+path+".tmp","read:"+path+".tmp","rename:"+path+":"+path+".bak","rename:"+path+".tmp:"+path}) {
      FakeStore fs;fs.files[path]=old;fs.failures.insert(fault);std::string error;
      CHECK(!piano::commitSong(fs,path,body,error));CHECK(fs.files[path]==old);CHECK(!error.empty());
    }
    FakeStore fs;fs.files[path]=old;std::string error;CHECK(piano::commitSong(fs,path,body,error));CHECK(fs.files[path]==body);CHECK(!fs.exists(path+".bak"));
    fs.files.erase(path);fs.files[path+".bak"]=old;CHECK(piano::commitSong(fs,path,body,error));CHECK(fs.files[path]==body);
  });
  group("rollback failure retains recovery backup and delete cannot resurrect", [] {
    const std::string path="/songs/song-1.json",body="new";FakeStore fs;fs.files[path]="old";
    fs.failures.insert("rename:"+path+".tmp:"+path);fs.failures.insert("rename:"+path+".bak:"+path);std::string error;
    CHECK(!piano::commitSong(fs,path,body,error));CHECK(!fs.exists(path));CHECK(fs.files[path+".bak"]=="old");
    fs.failures.clear();CHECK(piano::removeSong(fs,path));CHECK(fs.files.empty());
    fs.files[path]="old";fs.files[path+".bak"]="older";fs.failures.insert("remove:"+path+".bak");
    CHECK(!piano::removeSong(fs,path));CHECK(fs.files[path]=="old");
  });
  group("large polyphonic song commits, reloads all notes and protects old copy on read errors", [] {
    piano::Song score;
    score.id="long-piano"; score.title="大曲谱回读校验"; score.bpm=73; score.bpmProvided=true;
    for (size_t i=0; i<595; ++i) {
      piano::Note voice{60,.24999974999999997};
      voice.pitch=1;
      score.notes.push_back(voice);
    }
    for (uint32_t i=0; i<2083; ++i) {
      const uint32_t tick=(i/3)*240;
      score.performance.push_back({tick,tick+200,static_cast<uint8_t>(48+(i%24)),80,0});
    }
    auto body=piano::encodeSong(score);
    CHECK(body.size()>65536 && body.size()<120*1024);
    piano::Song initial; std::string parseError;
    CHECK(piano::parseSong(body.data(),body.size(),initial,parseError));
    FakeStore fs;
    const std::string path="/songs/long-piano.json";
    fs.files[path]="previous score";
    std::string error;
    fs.failures.insert("corrupt:"+path+".tmp");
    CHECK(!piano::commitSong(fs,path,body,error));
    CHECK(fs.files[path]=="previous score" && !fs.exists(path+".tmp"));
    fs.failures.clear();
    CHECK(piano::commitSong(fs,path,body,error));
    CHECK(fs.files[path]==body && !fs.exists(path+".bak"));
    SongFileReader stored{fs.files.at(path)};
    ReservedSongText readback;
    CHECK(piano::readSongText(stored,body.size(),readback) && readback.bytes==body);
    piano::Song restored;
    CHECK(piano::parseSong(readback.bytes.data(),readback.length(),restored,parseError));
    CHECK(restored.bpm==73 && restored.notes.size()==595 && restored.performance.size()==2083);
    CHECK(restored.performance.back().midi==score.performance.back().midi &&
          restored.performance.back().end==score.performance.back().end);
    piano::Engine engine;
    CHECK(engine.start(std::move(restored),piano::Mode::Play,0));
    CHECK(engine.performanceView().started==3 && engine.performanceView().count==2083);
    SongFileReader shortRead{body}; shortRead.limit=65537;
    CHECK(!piano::readSongText(shortRead,body.size(),readback));
    SongFileReader shortVerify{body}; shortVerify.limit=65537;
    CHECK(!piano::matchesSongFile(shortVerify,body));
    SongFileReader wrongSize{body};
    CHECK(!piano::matchesSongFile(wrongSize,body+" "));
  });
  group("pause/resume, tempo fractions, transpose and repeat across rollover", [] {
    piano::Engine e; CHECK(!e.pause(true,0));
    CHECK(e.start(song({{60,1},{62,1}}),piano::Mode::Play,100));
    CHECK(e.pause(true,300)); e.tick(2000); CHECK(e.view().paused && e.view().midi==-1 && e.view().index==0);
    CHECK(e.pause(false,2300)); e.tick(2590); CHECK(e.view().index==0); e.tick(2600); CHECK(e.view().index==1);
    auto s=e.view().settings; s.bpm=60; s.transpose=-2; e.settings(s,2850);
    e.tick(3349); CHECK(e.view().index==1); e.tick(3350); CHECK(e.view().finished);
    CHECK(e.start(song({{60,1},{60,1}}),piano::Mode::Play,0));
    s=e.view().settings;s.repeat=true;e.settings(s,0);e.tick(1500);CHECK(e.view().mode==piano::Mode::Play&&e.view().index==1);
    CHECK(e.start(song({{60,1}}),piano::Mode::Play,UINT32_MAX-100));e.tick(399);CHECK(e.view().index==0&&e.view().mode==piano::Mode::Play);
    e.stop();e.keys(1,400);CHECK(e.view().midi==58);
  });
  group("settings reject partial/invalid changes; song JSON round trip", [] {
    piano::Settings s;
    CHECK(!s.showLyrics);
    CHECK(piano::readSettings("{\"showLyrics\":true}",19,s) && s.showLyrics);
    CHECK(!piano::readSettings("{\"showLyrics\":1}",16,s) && s.showLyrics);
    for(const char* data:{"{\"bpm\":0}","{\"instrument\":12}","{\"transpose\":13}","{\"repeat\":1}","{\"unknown\":true}","{\"bpm\":80,\"instrument\":-1}"}) {CHECK(!piano::readSettings(data,std::strlen(data),s));CHECK(s.bpm==120&&s.instrument==3);}
    const char* good="{\"instrument\":2,\"bpm\":90,\"transpose\":-3,\"metronome\":true,\"repeat\":true}";
    CHECK(piano::readSettings(good,std::strlen(good),s));CHECK(s.bpm==90&&s.instrument==2&&s.metronome&&s.repeat);
    piano::Song a=song({{48,.25},{49,.75},{-1,1},{84,2}});a.title="曲谱";
    const auto encoded=piano::encodeSong(a);piano::Song b;std::string error;
    CHECK(piano::parseSong(encoded.data(),encoded.size(),b,error));CHECK(b.notes.size()==4&&b.notes[3].midi==84&&b.title==a.title);
    a.notes[0].midi=20;CHECK(piano::encodeSong(a).empty());
    a=song({{60,1},{72,1}});a.title="自然音";const auto natural=piano::encodeSong(a);
    cJSON* json=cJSON_Parse(natural.c_str());CHECK(json!=nullptr);cJSON* ns=cJSON_GetObjectItemCaseSensitive(json,"notes");
    for(int i=0;i<2;i++){const auto* n=cJSON_GetArrayItem(ns,i);CHECK(cJSON_GetObjectItemCaseSensitive(n,"pitch")->valueint==1);CHECK(cJSON_GetObjectItemCaseSensitive(n,"octave")->valueint==i);CHECK(cJSON_IsFalse(cJSON_GetObjectItemCaseSensitive(n,"sharp")));}cJSON_Delete(json);
  });
  group("lyrics survive JSON storage and two-row promotion without duplicating holds", [&] {
    auto s=song({}); s.title="小星星"; s.timeSignature="4/4";
    const int pitches[]={1,1,5,5,6,6,5,0,4,4,3,3,2,2,1};
    const char* words[]={"一","闪","一","闪","亮","晶","晶","","满","天","都","是","小","星","星"};
    for(unsigned i=0;i<15;++i) { s.notes.push_back({piano::noteMidi(pitches[i],0,false),1}); s.notes.back().lyric=words[i]; s.notes.back().barBefore=i%4==0; }
    auto encoded=piano::encodeSong(s); piano::Song restored; std::string error;
    CHECK(piano::parseSong(encoded.data(),encoded.size(),restored,error));
    CHECK(restored.notes[0].lyric=="一" && restored.notes[7].lyric.empty() && restored.notes[14].lyric=="星");
    piano::NotationLayout layout; layout.build(restored); piano::View v; v.mode=piano::Mode::Teach; v.count=15;
    auto page=layout.page(restored,v); CHECK(page.rows[0].cells[0].lyric=="一" && page.rows[1].cells[0].lyric=="满");
    lcd::ScoreScreen screen; ScreenCanvas canvas;
    for(bool lyrics:{false,true,false,true}) {
      v.settings.showLyrics=lyrics; screen.draw(canvas,v,page,100);
      lcd::ScoreScreen fresh; ScreenCanvas expected; fresh.draw(expected,v,page,100); CHECK(canvas.pixels==expected.pixels);
      const auto writes=canvas.writes; screen.draw(canvas,v,page,100); CHECK(canvas.writes==writes);
      if(!preview.empty()) CHECK(canvas.save(preview+(lyrics?"/score-lyrics-on.ppm":"/score-lyrics-off.ppm")));
    }
    v.index=8; page=layout.page(restored,v); CHECK(page.rows[0].cells[0].lyric=="满");
    screen.draw(canvas,v,page,200); screen.draw(canvas,v,page,460);
    lcd::ScoreScreen fresh; ScreenCanvas expected; fresh.draw(expected,v,page,460); CHECK(canvas.pixels==expected.pixels);
    v.index=15; v.finished=true; v.mode=piano::Mode::Manual; page=layout.page(restored,v); screen.draw(canvas,v,page,500);
    CHECK(page.rows[1].count==0);
    v=piano::View{}; screen.draw(canvas,v,{},400); // manual takeover removes all lyric pixels
    lcd::ScoreScreen manual; ScreenCanvas manualCanvas; manual.draw(manualCanvas,v,{},400); CHECK(canvas.pixels==manualCanvas.pixels);
    s.notes={{60,9},{62,1}}; s.notes[0].lyric="长"; s.notes[1].lyric="音"; layout.build(s); v.index=0; v.elapsedBeats=8;
    page=layout.page(s,v); CHECK(page.rows[0].cells[0].continuation && page.rows[0].cells[0].lyric.empty() && page.rows[0].cells[1].lyric=="音");
    for(const char* bad:{"4","null","\"bad\\nline\"","\"1234567890123456789012345\""}) {
      auto n=note(); n.pop_back(); n+=std::string(",\"lyric\":")+bad+"}"; auto data=payload(n);
      CHECK(!piano::parseSong(data.data(),data.size(),restored,error)); CHECK(restored.notes[0].lyric=="一");
    }
    auto rest=note(0); rest.pop_back(); rest+=",\"lyric\":\"x\"}"; auto data=payload(rest); CHECK(!piano::parseSong(data.data(),data.size(),restored,error));
  });
  group("rightmost HIGH long press toggles once, short modifier presses have no menu shortcuts", [] {
    piano::LocalControls c;CHECK(c.scan(0x200,100)==-1);CHECK(c.scan(0x200,1299)==-1);CHECK(c.scan(0x200,1300)==7);CHECK(c.menu());
    CHECK(c.scan(0x200,5000)==-1);CHECK(c.scan(1,5001)==-1);c.scan(0,5002);CHECK(c.scan(1,5003)==0);CHECK(c.scan(1,5004)==-1);
    c.scan(0,5005);CHECK(c.scan(1u<<6,5006)==6);c.close();CHECK(!c.menu());
    piano::LocalControls wrap;wrap.scan(0x200,UINT32_MAX-100);CHECK(wrap.scan(0x200,1099)==7);
    wrap.scan(0,1100);CHECK(wrap.scan(0x80,1101)==-1);CHECK(wrap.scan(0,1102)==-1);
    wrap.scan(0x100,1103);CHECK(wrap.scan(0,1104)==-1);CHECK(wrap.scan(0x200,1105)==7);CHECK(!wrap.menu());CHECK(wrap.scan(0,1106)==-1);
    wrap.scan(0x80,1107);wrap.scan(0x180,1108);wrap.scan(0x380,1109);CHECK(wrap.scan(0,1110)==-1);
    wrap.scan(0x200,1200);CHECK(wrap.scan(0x200,2400)==7);CHECK(wrap.menu());
    CHECK(wrap.scan(0x200,5000)==-1);CHECK(wrap.scan(0,5001)==-1);
    CHECK(wrap.scan(0x200,6000)==7);CHECK(!wrap.menu());CHECK(wrap.scan(0x200,7200)==-1);CHECK(wrap.scan(0,7201)==-1);
  });
  group("isolated LOW playback tap pauses and resumes, combinations remain musical", [] {
    piano::LocalControls c; piano::Engine e;
    CHECK(e.start(song({{60,2},{62,2}}),piano::Mode::Play,0));
    auto scan=[&](uint16_t mask,uint32_t now) {
      const auto before=e.view(); const int action=c.scan(mask,now,before.mode==piano::Mode::Play && !before.finished);
      if(action==8) CHECK(e.pause(!before.paused,now));
      e.keys(c.menu()?0:mask,now); e.tick(now); return action;
    };
    CHECK(scan(0x80,100)==-1);CHECK(scan(0,110)==8);CHECK(e.view().paused && e.view().midi==-1);
    scan(0,5000);CHECK(e.view().index==0);
    CHECK(scan(0x80,5001)==-1);CHECK(scan(0x80,5002)==-1);CHECK(scan(0,5010)==8);CHECK(!e.view().paused);
    scan(0,5100);CHECK(e.view().index==0);
    piano::LocalControls combos;
    combos.scan(0x80,0,true);combos.scan(0x81,1,true);combos.scan(0x80,2,true);CHECK(combos.scan(0,3,true)==-1);
    combos.scan(1,4,true);combos.scan(0x81,5,true);combos.scan(0x80,6,true);CHECK(combos.scan(0,7,true)==-1);
    combos.scan(0x80,8,false);CHECK(combos.scan(0,9,false)==-1);
    combos.scan(0x80,10,true);CHECK(combos.scan(0,11,false)==-1);
    combos.scan(0x200,12,true);CHECK(combos.scan(0x200,1212,true)==7);combos.scan(0,1213,true);
    combos.scan(0x80,1214,true);CHECK(combos.scan(0,1215,true)==-1);
    CHECK(combos.scan(0x200,1216,true)==7);CHECK(!combos.menu());CHECK(combos.scan(0x200,5000,true)==-1);
  });
  group("high-octave musical combinations never become menu gestures", [] {
    piano::LocalControls c;
    c.scan(0x200,100);c.scan(0x201,200);CHECK(c.scan(0x201,2000)==-1);CHECK(!c.menu());
    c.scan(0x200,2100);CHECK(c.scan(0x200,4000)==-1);CHECK(!c.menu());
    c.scan(0,4001);c.scan(1,4002);c.scan(0x201,4003);c.scan(0x200,4004);CHECK(c.scan(0x200,6000)==-1);CHECK(!c.menu());
    c.scan(0,6001);c.scan(0x380,6002);CHECK(c.scan(0x380,8000)==-1);CHECK(!c.menu());
    c.scan(0,8001);c.scan(0x200,8002);CHECK(c.scan(0x200,9202)==7);CHECK(c.menu());
  });
  group("all synth timbres bounded, volume zero and metronome-only sound", [] {
    std::vector<int16_t> a(22050*2);
    for(int instrument=0;instrument<3;instrument++) {piano::AudioSynth synth(22050,600);synth.render(84,100,a.data(),22050,instrument,true,1);for(auto v:a)CHECK(std::abs(int(v))<=600);}
    piano::AudioSynth synth(22050,600);synth.render(-1,100,a.data(),22050,0,true);int peak=0;for(auto v:a)peak=std::max(peak,std::abs(int(v)));CHECK(peak>50);
    synth.render(-1,0,a.data(),22050,1,false);for(auto v:a)CHECK(v==0);
    piano::Engine e;auto s=e.view().settings;s.metronome=true;e.settings(s,0);e.tick(0);CHECK(e.view().click);e.tick(30);CHECK(!e.view().click);e.tick(500);CHECK(e.view().click);
    piano::Song demo;CHECK(piano::builtinSong("demo-scale",demo));CHECK(demo.notes.size()==16);CHECK(!piano::builtinSong("other",demo));
  });
  group("whole-line lyrics hold through gaps and scroll without recoloring words", [&] {
    auto s=song(std::vector<piano::Note>(25, {60,1})); s.title="小星星";
    s.notes[0].lyric="一闪一闪"; s.notes[7].midi=-1;
    s.notes[16].midi=-1; s.notes[17].lyric="满天星"; s.notes[24].lyric="满天星";
    piano::NotationLayout layout; layout.build(s); piano::View v;
    v.mode=piano::Mode::Play; v.count=s.notes.size(); v.settings.showLyrics=true;
    auto page=layout.page(s,v); CHECK(page.currentLyric=="一闪一闪" && page.nextLyric=="满天星");
    lcd::ScoreScreen screen; ScreenCanvas canvas; screen.draw(canvas,v,page,0);
    const auto first=canvas.pixels;
    v.index=7; page=layout.page(s,v); screen.draw(canvas,v,page,100);
    for(int y=103;y<121;++y) for(int x=0;x<320;++x) CHECK(first[y*320+x]==canvas.pixels[y*320+x]);
    v.index=8; page=layout.page(s,v); CHECK(page.currentLyric=="一闪一闪"); screen.draw(canvas,v,page,200);
    for(int y=103;y<121;++y) for(int x=0;x<320;++x) CHECK(first[y*320+x]==canvas.pixels[y*320+x]);
    v.index=16; page=layout.page(s,v); CHECK(page.currentLyric=="一闪一闪"); screen.draw(canvas,v,page,300);
    const auto gap=canvas.pixels;
    v.index=17; page=layout.page(s,v); CHECK(page.currentLyric=="满天星" && page.lyricFirst==17);
    screen.draw(canvas,v,page,400); screen.draw(canvas,v,page,530);
    const auto mid=canvas.pixels;
    if(!preview.empty()) CHECK(canvas.save(preview+"/lyrics-scroll-mid.ppm"));
    CHECK(canvas.pixels!=gap);
    screen.draw(canvas,v,page,660); lcd::ScoreScreen fresh; ScreenCanvas expected; fresh.draw(expected,v,page,660);
    CHECK(canvas.pixels==expected.pixels);
    for(int y=0;y<240;++y) if(!((y>=103 && y<121)||(y>=166 && y<184)))
      for(int x=0;x<320;++x) CHECK(mid[y*320+x]==canvas.pixels[y*320+x]);
    if(!preview.empty()) CHECK(canvas.save(preview+"/lyrics-scroll-settled.ppm"));
    auto writes=canvas.writes; screen.draw(canvas,v,page,700); CHECK(canvas.writes==writes);
    v.index=24; page=layout.page(s,v); CHECK(page.lyricFirst==24 && page.currentLyric=="满天星");
    screen.draw(canvas,v,page,800); screen.draw(canvas,v,page,930);
    v.settings.showLyrics=false; screen.draw(canvas,v,page,940);
    lcd::ScoreScreen off; ScreenCanvas offCanvas; off.draw(offCanvas,v,page,940); CHECK(canvas.pixels==offCanvas.pixels);
    v.settings.showLyrics=true; v.songGeneration++; v.index=0; page=layout.page(s,v); screen.draw(canvas,v,page,950);
    lcd::ScoreScreen restarted; ScreenCanvas restartCanvas; restarted.draw(restartCanvas,v,page,950); CHECK(canvas.pixels==restartCanvas.pixels);
    s=song({{60,24},{62,1}}); s.notes[0].lyric="长"; s.notes[1].lyric="音"; layout.build(s); v.index=0; v.elapsedBeats=16;
    page=layout.page(s,v); CHECK(page.currentLyric=="长" && page.nextLyric=="音");
    v.index=2; v.finished=true; page=layout.page(s,v); CHECK(page.currentLyric=="音" && page.nextLyric.empty());
    s=song(std::vector<piano::Note>(17,{60,1})); layout.build(s); v.index=8; page=layout.page(s,v);
    CHECK(page.currentLyric.empty() && page.nextLyric.empty());
  });
  group("phone output silences all PCM, preserves synth volume and expires after disconnect", [] {
    piano::AudioOutput output; CHECK(!output.muted(0)); CHECK(!output.acquire(0,0));
    piano::AudioSynth synth(22050,board::kPeakAmplitude); int16_t samples[256];
    CHECK(output.acquire(123,100)); CHECK(!output.acquire(456,101));
    CHECK(!output.release(456)); CHECK(!output.renew(456,102));
    synth.render(72,80,samples,128,2,true,1); output.silence(samples,256,102);
    for(auto sample:samples) CHECK(sample==0);
    CHECK(output.renew(123,1900)); CHECK(output.muted(3899)); CHECK(!output.muted(3900));
    CHECK(!output.renew(123,3900)); CHECK(output.acquire(456,3900));
    CHECK(!output.release(123)); CHECK(output.muted(3901)); CHECK(output.release(456));
    synth.render(72,80,samples,128,2,true,1); output.silence(samples,256,3902);
    bool audible=false; for(auto sample:samples) if(sample) audible=true; CHECK(audible);
    CHECK(output.acquire(789,UINT32_MAX-100)); CHECK(output.muted(1898)); CHECK(!output.muted(1899));
    CHECK(output.acquire(789,UINT32_MAX-100)); CHECK(output.renew(789,100)); CHECK(output.muted(2099)); CHECK(!output.muted(2100));
  });
  group("single-note practice and playback use the original sample attack and release", [] {
    const char* root=std::getenv("PIANO_SAMPLE_DIRECTORY"); CHECK(root);
    std::ifstream input(std::string(root)+"/11.hps",std::ios::binary);
    CHECK(input.good());
    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>());
    piano::SampleBank bank; CHECK(bank.bind(bytes.data(),bytes.size(),11));
    auto s=song({{73,.5},{74,.5}});
    s.performance={{0,480,73,68,0},{480,960,74,82,0}};
    piano::Engine simple, melody, original;
    CHECK(simple.start(s,piano::Mode::Simple,0));
    CHECK(melody.start(s,piano::Mode::Play,0,piano::PlaybackArrangement::Melody));
    CHECK(original.start(s,piano::Mode::Play,0));
    simple.keys(1,0);
    simple.keys(0,10);
    simple.keys(2,20);
    CHECK(simple.view().index==2 && simple.view().midi==73 && simple.view().velocity==68);
    CHECK(!simple.performanceView().enabled && !melody.performanceView().enabled);

    piano::PerformanceMixer simpleMix(board::kSampleRate,board::kPeakAmplitude);
    piano::PerformanceMixer melodyMix(board::kSampleRate,board::kPeakAmplitude);
    piano::PerformanceMixer originalMix(board::kSampleRate,board::kPeakAmplitude);
    int16_t practice[256], automatic[256], source[256];
    unsigned nonSilent=0;
    for(unsigned block=0;block<16;++block) {
      simpleMix.render(piano::melodyAudio(simple.view()),bank,11,80,false,practice,128);
      melodyMix.render(piano::melodyAudio(melody.view()),bank,11,80,false,automatic,128);
      originalMix.render(original.performanceView(),bank,11,80,false,source,128);
      CHECK(std::equal(practice,practice+256,source) && std::equal(automatic,automatic+256,source));
      nonSilent+=std::count_if(source,source+256,[](int16_t value){return value!=0;});
    }
    CHECK(nonSilent>2000);
    simple.tick(250);
    melody.tick(250);
    original.tick(250);
    CHECK(simple.view().midi==74 && simple.view().velocity==82);
    CHECK(melody.view().midi==74 && melody.view().velocity==82);
    for(unsigned block=0;block<8;++block) {
      simpleMix.render(piano::melodyAudio(simple.view()),bank,11,80,false,practice,128);
      melodyMix.render(piano::melodyAudio(melody.view()),bank,11,80,false,automatic,128);
      originalMix.render(original.performanceView(),bank,11,80,false,source,128);
      CHECK(std::equal(practice,practice+256,source) && std::equal(automatic,automatic+256,source));
    }
    CHECK(melody.pause(true,260) && piano::melodyAudio(melody.view()).voices[0].midi<0);
    s.performance.clear();
    CHECK(simple.start(s,piano::Mode::Simple,500));
    simple.keys(0,501);
    simple.keys(1,502);
    CHECK(simple.view().midi==73 && simple.view().velocity==72);
  });
  group("sampled score output stays local and shifted attacks retain their source dynamics", [] {
    auto s=song({{67,.5},{67,.5},{67,1},{64,1}});
    s.performance={{30,200,79,51,0},{516,700,67,66,0},{1900,2200,64,83,0}};
    piano::Engine melody, original, practice;
    CHECK(melody.start(s,piano::Mode::Play,0,piano::PlaybackArrangement::Melody));
    CHECK(original.start(s,piano::Mode::Play,0));
    CHECK(practice.start(s,piano::Mode::Simple,0));
    practice.keys(16,0);
    CHECK(melody.view().midi==67 && melody.view().velocity==51 && practice.view().velocity==51);
    CHECK(piano::usesLocalScoreAudio(melody.view(),melody.performanceView()));
    CHECK(piano::usesLocalScoreAudio(original.view(),original.performanceView()));
    CHECK(piano::usesLocalScoreAudio(practice.view(),practice.performanceView()));
    melody.tick(250); CHECK(melody.view().velocity==66);
    melody.tick(500); CHECK(melody.view().velocity==72);
    melody.tick(1000); CHECK(melody.view().velocity==83);
    melody.stop(); CHECK(!piano::usesLocalScoreAudio(melody.view(),melody.performanceView()));
    auto v=practice.view(); v.settings.instrument=0;
    CHECK(!piano::usesLocalScoreAudio(v,practice.performanceView()));
  });
  group("SD sample banks: real assets, bounds, retrigger, release and mute", [] {
    const char* root=std::getenv("PIANO_SAMPLE_DIRECTORY"); CHECK(root);
    std::vector<int16_t> out(22050*2);
    for(int id=3;id<=piano::kMaxInstrument;id++) {
      std::ifstream input(std::string(root)+"/"+std::to_string(id)+".hps",std::ios::binary);
      CHECK(input.good()); std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>());
      piano::SampleBank bank; CHECK(bank.bind(bytes.data(),bytes.size(),id));
      CHECK(!bank.bind(bytes.data(),bytes.size()-1,id)); CHECK(!bank.bind(bytes.data(),bytes.size(),id+1));
      CHECK(bank.bind(bytes.data(),bytes.size(),id));
      for(int midi:{36,49,60,73,96}) {
        piano::SampleVoice voice; std::fill(out.begin(),out.end(),0);
        voice.mix(bank,midi,100,out.data(),22050,board::kPeakAmplitude,false,1);
        int maximum=0; for(auto value:out) {maximum=std::max(maximum,std::abs(int(value)));CHECK(std::abs(int(value))<=board::kPeakAmplitude);}
        CHECK(maximum>100);
        std::fill(out.begin(),out.end(),0);voice.mix(bank,-1,100,out.data(),22050,board::kPeakAmplitude,false,1);
        CHECK(std::abs(int(out[out.size()-2]))<2);
        std::fill(out.begin(),out.end(),0);voice.mix(bank,midi,0,out.data(),22050,board::kPeakAmplitude,true,2);
        CHECK(std::all_of(out.begin(),out.end(),[](int16_t value){return value==0;}));
      }
      // Several full buffers cross the sustain loop / percussive tail boundary.
      piano::SampleVoice held;
      for(int second=0;second<7;second++) {std::fill(out.begin(),out.end(),0);held.mix(bank,61,80,out.data(),22050,board::kPeakAmplitude,false,9);}
      if(id==5||id==6||id==7||id==9) CHECK(std::any_of(out.begin(),out.end(),[](int16_t value){return std::abs(int(value))>20;}));
      else CHECK(std::all_of(out.begin(),out.end(),[](int16_t value){return value==0;}));
      piano::Settings settings; const std::string json="{\"instrument\":"+std::to_string(id)+"}";
      CHECK(piano::readSettings(json.c_str(),json.size(),settings)&&settings.instrument==id);
    }
  });
  group("sample upload bounded chunks, CRC32, headers, ownership and timeout", [] {
    const uint8_t known[]="123456789";CHECK((piano::sampleCrc(0xffffffffu,known,9)^0xffffffffu)==0xcbf43926u);
    const char* root=std::getenv("PIANO_SAMPLE_DIRECTORY");CHECK(root);
    std::ifstream input(std::string(root)+"/3.hps",std::ios::binary);
    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>());
    CHECK(bytes.size()==piano::SampleBank::kBytes);
    const uint32_t expected=999234732u;
    piano::SampleUpload upload;
    CHECK(!upload.begin(2,bytes.size(),123,expected,0));CHECK(!upload.begin(3,12,123,expected,0));CHECK(!upload.begin(3,bytes.size(),0,expected,0));
    CHECK(upload.begin(3,bytes.size(),123,expected,0));CHECK(!upload.begin(4,bytes.size(),456,expected,0));
    CHECK(!upload.append(456,0,bytes.data(),32,1));CHECK(!upload.append(123,1,bytes.data(),32,1));CHECK(!upload.append(123,0,bytes.data(),65537,1));CHECK(upload.received==0);
    CHECK(!upload.valid());
    for(size_t offset=0;offset<bytes.size();) {const size_t count=std::min(size_t(65536),bytes.size()-offset);CHECK(upload.append(123,offset,bytes.data()+offset,count,2));offset+=count;}
    CHECK(upload.valid());CHECK(!upload.append(123,bytes.size(),bytes.data(),1,2));
    upload.expectedCrc^=1;CHECK(!upload.valid());upload.expectedCrc^=1;upload.header[0]^=1;CHECK(!upload.valid());
    piano::SampleUpload expired;CHECK(expired.begin(3,bytes.size(),1,expected,UINT32_MAX-100));
    CHECK(!expired.expired(20));CHECK(expired.expired(120001));CHECK(!expired.append(1,0,bytes.data(),32,120001));CHECK(expired.received==0);
  });
  group("sample binary commit failures preserve target or recoverable backup", [] {
    const std::string path="/piano-samples/3.hps";
    for(const std::string& failure:{"rename:"+path+":"+path+".bak","rename:"+path+".tmp:"+path,"remove:"+path+".bak"}) {
      FakeStore store;store.files[path]="old";store.files[path+".tmp"]="new";
      if(failure.find("remove:")==0)store.files[path+".bak"]="older";
      store.failures.insert(failure);CHECK(!piano::commitSample(store,path));CHECK(store.files[path]=="old");
    }
    FakeStore failed;failed.files[path]="old";failed.files[path+".tmp"]="new";
    failed.failures.insert("rename:"+path+".tmp:"+path);failed.failures.insert("rename:"+path+".bak:"+path);
    CHECK(!piano::commitSample(failed,path));CHECK(failed.files[path+".bak"]=="old");failed.failures.clear();CHECK(piano::recoverSample(failed,path));CHECK(failed.files[path]=="old");
    FakeStore good;good.files[path]="old";good.files[path+".tmp"]="new";CHECK(piano::commitSample(good,path));CHECK(good.files[path]=="new" && !good.exists(path+".bak"));
  });
  std::printf("RESULT %d groups, %d assertions passed\n",groups,checks);
}
