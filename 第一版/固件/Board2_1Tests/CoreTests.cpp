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
struct FakeStore {
  std::map<std::string,std::string> files;
  std::set<std::string> failures;
  bool exists(const std::string& p) { return files.count(p)!=0; }
  bool write(const std::string& p,const std::string& value) { if(failures.count("write:"+p))return false; files[p]=value;return true; }
  bool read(const std::string& p,std::string& value) { if(failures.count("read:"+p)||!exists(p))return false;value=files[p];return true; }
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
int main(int argc, char** argv) {
  const std::string preview = argc > 1 ? argv[1] : "";
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
  group("six home sections, navigation, editors and no hidden recording shortcuts", [] {
    using P=piano::MenuPage; using C=piano::MenuCommand;
    piano::MenuController menu;
    CHECK(menu.input(2)==C::None && menu.state.page==P::Performance); CHECK(menu.input(2)==C::FreeScore); menu.input(1); CHECK(menu.input(2)==C::Simple);
    menu.input(1); CHECK(menu.input(2)==C::RhythmPractice); menu.input(1); CHECK(menu.state.row==0); menu.input(0); CHECK(menu.state.row==2); menu.input(3);
    const P pages[]={P::Home,P::Songs,P::Sound,P::Rhythm,P::Connection,P::Device};
    for(int i=1;i<6;++i) {
      menu.state.home=i; CHECK(menu.input(2)==C::None && menu.state.page==pages[i]);
      CHECK(menu.input(3)==C::None && menu.state.page==P::Home && menu.state.home==i);
    }
    menu.input(1); CHECK(menu.state.home==0); CHECK(menu.input(3)==C::Exit);
    menu.state.home=1; menu.input(2); CHECK(menu.input(2)==C::LoadSongs && menu.state.page==P::SongList);
    CHECK(menu.input(1)==C::SongNext); CHECK(menu.input(0)==C::SongPrevious);
    CHECK(menu.input(3)==C::SongCancel && menu.state.page==P::Songs);
    menu.input(2); CHECK(menu.input(2)==C::SongSelect && menu.state.page==P::Performance && menu.state.row==0);
    CHECK(menu.input(1)==C::None && menu.input(2)==C::Simple);
    menu.state.page=P::Songs; menu.state.row=0;
    menu.state.row=1; CHECK(menu.input(2)==C::PlayPause);
    menu.state.row=2; CHECK(menu.input(2)==C::Lyrics); menu.input(1); CHECK(menu.state.row==0);
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
    for(auto page:{P::Songs,P::Sound,P::Rhythm,P::Device}) {
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
    e.tick(252);CHECK(e.view().midi==-1);e.tick(253);CHECK(e.view().midi==49 && e.view().trigger!=trigger);
    e.tick(503);CHECK(e.view().midi==-1);e.tick(753);CHECK(e.view().midi==74);
    e.tick(1003);CHECK(e.view().finished && e.view().midi==-1);
    e.keys(0,1100);CHECK(e.start(s,piano::Mode::Simple,1100));e.tick(2000);CHECK(e.view().index==0 && e.view().midi==-1);
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
    lcd::FrameSurface surface(composed.data()); lcd::MenuScreen renderer; piano::MenuState state;piano::View v;ScreenCanvas lcd;
    renderer.draw(surface,state,v,0);composed[0]=0xffff;lcd::presentFrame(lcd,composed.data(),previous.data(),true);CHECK(lcd.pixels==composed);
    const auto original=lcd.pixels;const auto writes=lcd.writes;
    state.home=1;renderer.draw(surface,state,v,33);
    unsigned changed=0;for(size_t i=0;i<composed.size();++i) if(original[i]!=composed[i])++changed;
    CHECK(lcd::presentFrame(lcd,composed.data(),previous.data())==changed);CHECK(lcd.writes-writes==changed);CHECK(lcd.pixels==composed);
    CHECK(changed<320*240/2);CHECK(lcd::presentFrame(lcd,composed.data(),previous.data())==0);
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
    for(int id=3;id<=10;++id) {
      CHECK(menu.input(1)==piano::MenuCommand::Increase);edit.browseInstrument(1);
      CHECK(edit.pendingInstrument==id && active.settings.instrument==2 && edit.original==2);
    }
    edit.browseInstrument(1);CHECK(edit.pendingInstrument==0);
    CHECK(menu.input(3)==piano::MenuCommand::CancelEdit);edit.finish();CHECK(active.settings.instrument==2);
    menu.input(2);edit.begin(piano::MenuItem::Instrument,active);
    CHECK(edit.pendingInstrument==2);
    CHECK(menu.input(0)==piano::MenuCommand::Decrease);edit.browseInstrument(-1);CHECK(edit.pendingInstrument==1);
    edit.browseInstrument(-1);CHECK(edit.pendingInstrument==0);
    edit.browseInstrument(5);CHECK(edit.pendingInstrument==5 && active.settings.instrument==2);
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
    state.builtinSelected=false;render();state.phoneReady=true;state.phoneOutput=true;render();
    if(!preview.empty()) CHECK(canvas.save(preview+"/menu-home-phone.ppm"));
    state.phoneOutput=false;state.phoneReady=false;state.builtinSelected=true;render();
    const P pages[]={P::Songs,P::Sound,P::Rhythm,P::Connection,P::Device,P::SongList,P::Diagnostics,P::Performance,P::DeleteSong};
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
    state.page=P::Songs; state.row=2; render();
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
    for(const char* data:{"{\"bpm\":0}","{\"instrument\":11}","{\"transpose\":13}","{\"repeat\":1}","{\"unknown\":true}","{\"bpm\":80,\"instrument\":-1}"}) {CHECK(!piano::readSettings(data,std::strlen(data),s));CHECK(s.bpm==120&&s.instrument==0);}
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
  group("SD sample banks: real assets, bounds, retrigger, release and mute", [] {
    const char* root=std::getenv("PIANO_SAMPLE_DIRECTORY"); CHECK(root);
    std::vector<int16_t> out(22050*2);
    for(int id=3;id<=10;id++) {
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
