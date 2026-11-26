#include <array>
#include <cstring>
#include <utility>

#include "Board2_1Config.h"
#include "BuiltinSongs.h"
#include "ChordPractice.h"
#include "FrameSurface.h"
#include "GameAssist.h"
#include "GameFlow.h"
#include "GameHoldBonus.h"
#include "GameScreen.h"
#include "GuidedGame.h"
#include "GuidedPerformance.h"
#include "LocalControls.h"
#include "ManualMixer.h"
#include "MenuScreen.h"
#include "PerformanceGame.h"
#include "PerformanceMixer.h"
#include "ScoreScreen.h"

namespace {
using namespace piano;
using C = MenuCommand;
using I = MenuItem;
using P = MenuPage;
constexpr unsigned frames = 128;
constexpr size_t maxSongs = 64;

Engine engine_;
LocalControls local_;
MenuController menu;
MenuEdit edit;
NotationLayout notation_;
GameAssist gameAssist_;
GameFlow gameFlow_;
ChordPractice chordPractice_;
GuidedPerformance guidedPerformance_;
GameHoldBonus gameBonus_;
lcd::MenuScreen menuScreen;
lcd::ScoreScreen scoreScreen;
lcd::GameScreen gameScreen;
lcd::GuidedGame guidedGame;
lcd::PerformanceLayout performanceLayout;
lcd::GameFrame game;
ManualMixer manual(board::kSampleRate, board::kPeakAmplitude);
PerformanceMixer score(board::kSampleRate, board::kPeakAmplitude);
SampleBank bank;

uint16_t pixels[320 * 240];
int16_t pcm[frames * 2];
alignas(int16_t) uint8_t bankBytes[SampleBank::kBytes];
char incoming[board::kMaxPayloadBytes];
std::string importError, deletedSong;
std::vector<Song> songs;
std::vector<MenuSongSummary> summaries;
size_t selected = 0, browsing = 0;

uint64_t rendered = 0;
uint32_t noticeAt = 0, guidedNoteStart_ = 0, settingsRevision = 0;
uint32_t gameHitSequence_[7]{}, gameHitTime_[7]{}, gameScore_ = 0, gameCombo_ = 0;
uint8_t gameHitStyle_[7]{}, gameHits_[kMaxNotes]{};
uint16_t mask = 0, rawKeyMask_ = 0, gameAcceptedHeld_ = 0;
int gameMode_ = 0, guidedNote_ = -1, lastManualInstrument = -1, requestedBank = 0;
bool waitRelease_ = false, gameDisplay_ = true, simplePractice_ = false;
int32_t info[24]{};

uint32_t clockMs() { return static_cast<uint32_t>(rendered * 1000 / board::kSampleRate); }

void updateSong(bool list = false) { showMenuSong(menu.state, summaries, list ? browsing : selected, list); }

void addSong(Song song) {
  const MenuSongSummary summary{song.id, song.title, song.bpm, song.bpmProvided};
  const auto it =
      std::find_if(songs.begin(), songs.end(), [&](const Song& existing) { return existing.id == song.id; });
  if (it == songs.end()) {
    summaries.push_back(summary);
    songs.push_back(std::move(song));
  } else {
    summaries[it - songs.begin()] = summary;
    *it = std::move(song);
  }
  updateSong(local_.menu() && menu.state.page == P::SongList);
}

void init() {
  if (!songs.empty()) return;
  Song scale;
  builtinSong("demo-scale", scale);
  addSong(std::move(scale));

  Song example;
  example.id = "demo-chords";
  example.title = "和弦练习";
  example.bpm = 100;
  example.bpmProvided = true;
  example.timeSignature = "4/4";
  example.lyricCues = {{0, "单音练习"}, {4, "和弦练习"}};
  constexpr int melody[] = {3, 5, 2, 5, 4, 6, 2, 1};
  constexpr int harmony[][2] = {{48, 67}, {48, 64}, {55, 71}, {55, 62},
                                {53, 69}, {53, 65}, {55, 71}, {48, 64}};
  for (size_t i = 0; i < std::size(melody); ++i) {
    const auto midi = noteMidi(melody[i], 0, false);
    Note note(midi, 1);
    note.pitch = melody[i];
    note.barBefore = note.lineBefore = i == 4;
    example.notes.push_back(std::move(note));
    const auto start = static_cast<uint32_t>(i) * kPerformanceTicksPerBeat;
    const auto end = start + kPerformanceTicksPerBeat;
    example.performance.push_back({start, end, static_cast<uint8_t>(harmony[i][0]), 74, 0});
    example.performance.push_back({start, end, static_cast<uint8_t>(harmony[i][1]), 78, 1});
    example.performance.push_back({start, end, static_cast<uint8_t>(midi), 92, 2});
  }
  addSong(std::move(example));
  menu.state.networkLines = {"网页演示", "使用浏览器导入歌曲", "和弦练习与音阶练习"};
  menu.state.deviceLines = {std::string(lcd::kFirmware) + board::kVersion, "网页演示", "声音就绪",
                            "逐键检查"};
  auto settings = engine_.view().settings;
  settings.instrument = 11;
  engine_.settings(settings, 0);
  engine_.setVolume(60);
}

void notice(const char* message, uint32_t now) {
  menu.state.notice = message;
  noticeAt = now;
}

void closeMenu() {
  local_.close();
  waitRelease_ = true;
}

void stop() {
  engine_.stop();
  gameMode_ = 0;
  simplePractice_ = false;
  gameAssist_.reset();
  gameBonus_.reset();
}

bool start(Song song, Mode mode, int gameMode, uint32_t now) {
  if (gameMode == 3 && !ChordPractice::arrange(song)) return false;
  NotationLayout notation;
  notation.build(song);
  if (!engine_.start(std::move(song), mode, now, menu.state.playbackArrangement)) return false;
  notation_ = std::move(notation);
  gameScore_ = gameCombo_ = 0;
  gameAcceptedHeld_ = 0;
  guidedNote_ = -1;
  gameBonus_.reset();
  gameMode_ = gameMode;
  gameAssist_.reset();
  if (gameMode_ == 2) {
    engine_.stop();
    gameFlow_.reset(engine_, now);
  }
  std::fill(std::begin(gameHits_), std::end(gameHits_), 0);
  simplePractice_ = mode == Mode::Simple;
  if (gameMode_ == 3) {
    engine_.stop();
    chordPractice_.start(engine_.song(), now);
  } else if (gameMode_)
    guidedPerformance_.start(engine_.song(), now);
  return true;
}

View playView(uint32_t now) { return gameMode_ == 2 ? gameFlow_.view(engine_, now) : engine_.view(); }

void changeValue(I field, int value, uint32_t now) {
  auto settings = engine_.view().settings;
  switch (field) {
    case I::Volume:
      engine_.setVolume(value);
      return;
    case I::Tempo:
      settings.bpm = std::clamp(value, 20, 400);
      break;
    case I::Transpose:
      settings.transpose = std::clamp(value, -12, 12);
      break;
    default:
      return;
  }
  engine_.settings(settings, now);
}

void play(C command, uint32_t now) {
  const auto view = playView(now);
  const auto& song = songs[selected];
  if (command == C::PlayPause && isPlaybackArrangement(view, menu.state.playbackArrangement) &&
      engine_.song().id == song.id) {
    if (engine_.pause(!view.paused, now)) closeMenu();
    return;
  }
  const auto arrangement =
      command == C::ChordPractice ? PlaybackArrangement::Original : menu.state.playbackArrangement;
  if ((command == C::PlayPause || command == C::ChordPractice) &&
      !hasPlaybackArrangement(song, arrangement)) {
    notice(lcd::kNoOriginal, now);
    return;
  }
  const int gameMode = command == C::GameAssist      ? 1
                       : command == C::GameFlow      ? 2
                       : command == C::ChordPractice ? 3
                                                     : 0;
  const auto mode = command == C::Simple                    ? Mode::Simple
                    : command == C::RhythmPractice          ? Mode::Rhythm
                    : (command == C::FreeScore || gameMode) ? Mode::FreeScore
                    : command == C::Teach                   ? Mode::Teach
                                                            : Mode::Play;
  if (start(song, mode, gameMode, now))
    closeMenu();
  else
    notice(lcd::kSongReadFailed, now);
}

void removeSong(uint32_t now) {
  if (browsing < 2) {
    notice("内置歌曲不能删除", now);
    return;
  }
  deletedSong = songs[browsing].id;
  if (engine_.song().id == deletedSong) stop();
  songs.erase(songs.begin() + browsing);
  summaries.erase(summaries.begin() + browsing);
  if (selected == browsing)
    selected = 0;
  else if (selected > browsing)
    --selected;
  browsing = std::min(browsing, songs.size() - 1);
  notice("歌曲已删除", now);
  updateSong(true);
}

void toggleSetting(C command, uint32_t now) {
  auto settings = engine_.view().settings;
  if (command == C::Metronome)
    settings.metronome = !settings.metronome;
  else if (command == C::Repeat)
    settings.repeat = !settings.repeat;
  else {
    if (!settings.showLyrics) {
      const auto& song = songs[selected];
      if (engine_.view().mode == Mode::Manual) {
        start(song, Mode::FreeScore, 0, now);
        settings = engine_.view().settings;
      }
      if (song.lyricCues.empty() && std::none_of(song.notes.begin(), song.notes.end(),
                                                 [](const Note& note) { return !note.lyric.empty(); }))
        notice("此曲无歌词", now);
    }
    settings.showLyrics = !settings.showLyrics;
  }
  engine_.settings(settings, now);
  ++settingsRevision;
}

void handle(int action, uint32_t now) {
  const auto previousArrangement = menu.state.playbackArrangement;
  const auto command = menu.input(action);
  if (command == C::ToggleArrangement) menu.selectArrangement(command);
  if (previousArrangement != menu.state.playbackArrangement) ++settingsRevision;
  switch (command) {
    case C::BeginEdit:
      edit.begin(menuItem(menu.state), engine_.view());
      if (edit.item == I::Output) edit.pendingOutput = 0;
      break;
    case C::CommitEdit:
      if (edit.item == I::Instrument)
        requestedBank = edit.pendingInstrument;
      else if (edit.item == I::Output && edit.pendingOutput)
        notice("网页使用本机扬声器", now);
      else
        ++settingsRevision;
      edit.finish();
      break;
    case C::CancelEdit:
      changeValue(edit.item, edit.original, now);
      edit.finish();
      break;
    case C::Increase:
    case C::Decrease:
    case C::IncreaseTen:
    case C::DecreaseTen: {
      const int delta =
          menuEditStep(command) * ((command == C::Decrease || command == C::DecreaseTen) ? -1 : 1);
      if (edit.item == I::Instrument)
        edit.browseInstrument(delta);
      else if (edit.item == I::Output)
        edit.pendingOutput = cycleIndex(edit.pendingOutput, delta, 2);
      else
        changeValue(edit.item, edit.value(engine_.view()) + delta, now);
      break;
    }
    case C::VolumeDown:
    case C::VolumeUp:
      engine_.setVolume(engine_.view().volume + (command == C::VolumeDown ? -5 : 5));
      ++settingsRevision;
      break;
    case C::LoadSongs:
      browsing = selected;
      updateSong(true);
      break;
    case C::SongPrevious:
    case C::SongNext:
      browsing = cycleIndex(browsing, command == C::SongPrevious ? -1 : 1, songs.size());
      updateSong(true);
      break;
    case C::SongSelect:
      selected = browsing;
      updateSong();
      break;
    case C::SongCancel:
      updateSong();
      break;
    case C::Delete:
      removeSong(now);
      break;
    case C::NotationDisplay:
    case C::GameDisplay:
      gameDisplay_ = command == C::GameDisplay;
      ++settingsRevision;
      break;
    case C::Lyrics:
    case C::Metronome:
    case C::Repeat:
      toggleSetting(command, now);
      break;
    case C::PlayPause:
    case C::Teach:
    case C::FreeScore:
    case C::Simple:
    case C::RhythmPractice:
    case C::GameAssist:
    case C::GameFlow:
    case C::ChordPractice:
      play(command, now);
      break;
    case C::Stop:
    case C::Manual:
      stop();
      closeMenu();
      break;
    case C::Exit:
      closeMenu();
      break;
    default:
      break;
  }
  if (action == 7) updateSong();
}
void updateControl(uint32_t now, View& tone, PerformanceView& performance) {
  if (!menu.state.notice.empty() && now - noticeAt >= 4000) menu.state.notice.clear();
  const auto previous = playView(now);
  const int action = local_.scan(mask, now, previous.mode == Mode::Play && !previous.finished,
                                 edit.active && edit.item == I::Volume);
  if (action == 8)
    engine_.pause(!previous.paused, now);
  else if (action >= 0)
    handle(action, now);
  if (action == 7) waitRelease_ = true;
  if (!mask) waitRelease_ = false;
  const auto before = playView(now);
  const uint16_t input = local_.menu() || waitRelease_ ? 0 : mask;
  const bool chord = gameMode_ == 3;
  gameAcceptedHeld_ &= input;
  const uint16_t rising = (input & ~before.keyMask) & 0x7f;
  int gameAccepted = -1;
  bool gameAutomatic = false;
  if (gameMode_ == 1) {
    const auto result = gameAssist_.step(engine_, input, now);
    gameAccepted = result.accepted;
    gameAutomatic = result.automatic;
  } else if (gameMode_ == 2) {
    const auto result = gameFlow_.step(engine_, input, now);
    gameAccepted = result.accepted;
    gameAutomatic = result.automatic;
  } else if (chord) {
    chordPractice_.step(engine_.song(), before.settings, input, now);
    engine_.tick(now);
  } else
    engine_.keys(input, now);
  const auto pressed = engine_.view();
  if (gameAccepted >= 0) {
    guidedNote_ = gameAccepted;
    guidedNoteStart_ = now;
  }
  if (gameDisplay_ && !local_.menu())
    gameScore_ +=
        gameBonus_.release(input, gameMode_ == 2 ? gameFlow_.view(engine_, now) : pressed, gameMode_ == 2);
  else if (local_.menu())
    gameBonus_.reset();
  if (gameMode_ && gameDisplay_ && gameAccepted >= 0 &&
      static_cast<size_t>(gameAccepted) < engine_.song().notes.size() &&
      gameAccepted < static_cast<int>(piano::kMaxNotes)) {
    const auto& note = engine_.song().notes[gameAccepted];
    const int degree = note.pitch;
    if (degree >= 1 && degree <= 7 && !gameHits_[gameAccepted]) {
      if (!gameAutomatic) gameBonus_.accepted(gameAccepted, note);
      gameHits_[gameAccepted] = 1;
      gameScore_ += 100;
      ++gameCombo_;
    }
  }
  if (!gameMode_ && gameDisplay_ && rising && !before.paused && before.mode != piano::Mode::Manual &&
      before.mode != piano::Mode::Play && before.index < piano::kMaxNotes) {
    const bool accepted = before.mode == piano::Mode::Simple
                              ? pressed.index > before.index
                              : !before.noteActive && pressed.noteActive && pressed.index == before.index;
    if (accepted && !gameHits_[before.index]) {
      gameHits_[before.index] = 1;
      gameScore_ += 100;
      ++gameCombo_;
      const auto& note = engine_.song().notes[before.index];
      const auto degree = note.pitch;
      if (degree >= 1 && degree <= 7) {
        ++gameHitSequence_[degree - 1];
        gameHitTime_[degree - 1] = now;
        gameHitStyle_[degree - 1] = lcd::GameStyle::colorIndex(note.octave, note.sharp);
        gameAcceptedHeld_ |= 1u << (degree - 1);
      }
    }
  }
  if (!gameMode_) {
    engine_.tick(now);
  }
  const auto v = gameMode_ == 2 ? gameFlow_.view(engine_, now) : engine_.view();
  if (gameAccepted < 0 && v.index < before.index) guidedNote_ = -1;
  if (gameDisplay_ && v.songGeneration == before.songGeneration) {
    if (v.index < before.index)
      std::fill(gameHits_, gameHits_ + piano::kMaxNotes, 0);
    else
      for (size_t i = before.index; i < v.index && i < engine_.song().notes.size() && i < piano::kMaxNotes;
           ++i)
        if (engine_.song().notes[i].midi >= 0 && !gameHits_[i]) gameCombo_ = 0;
  }
  rawKeyMask_ = mask;
  const bool backing = gameMode_ && !chord && guidedPerformance_.view().enabled;
  if (backing) {
    if (v.index < before.index) guidedPerformance_.rewind(now);
    guidedPerformance_.step(engine_.song(), v, gameAccepted, now, local_.menu() || waitRelease_);
  }
  performance = chord     ? chordPractice_.audio()
                : backing ? guidedPerformance_.view()
                          : engine_.performanceView();
  tone = gameMode_ == 2 ? v : engine_.view(false);
}

void draw(uint32_t now) {
  lcd::FrameSurface surface(pixels);
  const auto v = gameMode_ == 2 ? gameFlow_.view(engine_, now) : engine_.view();
  const bool chord = gameMode_ == 3;
  const bool showSimple = v.mode == piano::Mode::Simple || (simplePractice_ && v.finished);
  const bool menu = local_.menu(), showGame = chord || showSimple || gameDisplay_;
  const auto performance = engine_.performanceView();
  const bool backing = gameMode_ && !chord && guidedPerformance_.view().enabled;
  const bool showPerformance = lcd::showsPerformance(performance, v);
  auto notationView = v;
  if (!menu && !showGame && gameMode_) {
    notationView.noteActive = lcd::isGuidedNoteSounding(engine_.song(), v, guidedNote_);
    if (notationView.noteActive) {
      notationView.index = guidedNote_;
      const double heldBeats = (now - guidedNoteStart_) * (v.settings.bpm / 60000.0);
      notationView.elapsedBeats = std::min(engine_.song().notes[guidedNote_].beats, heldBeats);
    } else
      notationView.elapsedBeats = std::max(0.0, v.elapsedBeats);
  }
  const auto page = menu || showGame ? piano::ScorePage{} : notation_.page(engine_.song(), notationView);

  game.count = 0;
  game.ready = false;
  game.polyphonic = false;
  game.guided = false;
  game.keySlots = false;
  game.guideMask = 0;
  if (!menu && showGame) {
    const auto& song = engine_.song();
    game.ready = !song.notes.empty();
    game.title = song.title;
    game.mode = gameMode_ == 1   ? "单键伴奏"
                : gameMode_ == 2 ? "自主节奏"
                                 : lcd::kModeNames[static_cast<int>(v.mode)];
    game.score = gameScore_;
    game.combo = gameCombo_;
    game.keys = rawKeyMask_;
    game.paused = v.paused;
    game.pauseAvailable = v.mode == piano::Mode::Play && !v.finished;
    game.session = v.songGeneration;
    game.volume = v.volume;
    game.instrument = v.settings.instrument;
    if (v.mode == piano::Mode::Play) game.mode = showPerformance ? "播放 多音" : "播放 单音";
    if (showSimple) {
      game.mode = v.finished ? lcd::kFinished : lcd::kSimplePractice;
      const auto playback = engine_.view(false);
      guidedGame.update(game, song, v, engine_.simpleSoundingNote(), 0, now, &playback);
    } else if (chord) {
      game.mode = chordPractice_.finished() ? lcd::kFinished : lcd::kComplexPractice;
      game.guided = true;
      game.guideMask = chordPractice_.targets();
      const auto display = chordPractice_.display();
      lcd::performanceGame(game, performanceLayout, song, v, display, chordPractice_.firstCue(),
                           chordPractice_.cueCount(), true);
    } else if (backing) {
      game.mode += " 多音";
      const auto& backingView = guidedPerformance_.view();
      auto originalView = v;
      originalView.finished = backingView.positionTicks >= backingView.endTick;
      game.guided = true;
      const int degree = guidedPerformance_.nextDegree(song);
      if (degree) game.guideMask = 1u << (degree - 1);
      lcd::performanceGame(game, performanceLayout, song, originalView, backingView,
                           guidedPerformance_.nextEvent());
    } else if (showPerformance) {
      lcd::performanceGame(game, performanceLayout, song, v, performance);
    } else if (gameMode_) {
      game.mode += " 单音";
      guidedGame.update(game, song, v, guidedNote_, now - guidedNoteStart_, now);
    } else {
      for (int i = 0; i < 7; i++) {
        game.hitSequence[i] = gameHitSequence_[i];
        game.hitTime[i] = gameHitTime_[i];
        game.hitStyle[i] = gameHitStyle_[i];
        game.inputStyle[i] = lcd::GameStyle::colorIndex((v.high ? 1 : 0) - (v.low ? 1 : 0), v.sharp);
      }
      if (v.mode != piano::Mode::FreeScore && v.noteActive && v.manualKey >= 0 && v.manualKey < 7 &&
          (gameAcceptedHeld_ & (1u << v.manualKey)))
        game.inputStyle[v.manualKey] = gameHitStyle_[v.manualKey];
      const bool playing = v.mode == piano::Mode::Play;
      if (playing) game.keys = 0;
      double total = 0, current = 0;
      for (size_t i = 0; i < song.notes.size(); ++i) {
        if (i < v.index) current += song.notes[i].beats;
        total += song.notes[i].beats;
      }
      current += v.elapsedBeats;
      game.progress = total > 0 ? static_cast<float>(std::min(1.0, current / total)) : 0;
      double beat = 0;
      const double speed = lcd::GameStyle::pixelsPerMs, msBeat = 60000.0 / std::max(20, v.settings.bpm);
      for (size_t i = 0; i < song.notes.size(); ++i) {
        const auto& n = song.notes[i];
        const double bottom = lcd::GameStyle::line - (beat - current) * msBeat * speed;
        const double height = std::max(20.0, n.beats * msBeat * speed);
        beat += n.beats;
        if (n.pitch < 1 || n.pitch > 7 || bottom < lcd::GameStyle::boardTop ||
            bottom - height > lcd::GameStyle::line || game.count >= lcd::GameFrame::MaxTiles)
          continue;
        auto& tile = game.tiles[game.count++];
        tile = lcd::GameTile{};
        tile.degree = n.pitch;
        tile.octave = n.octave;
        tile.sharp = n.sharp;
        tile.hit = i < piano::kMaxNotes && gameHits_[i];
        tile.holding = tile.hit && i == v.index && v.noteActive && (v.keyMask & (1u << (n.pitch - 1)));
        tile.bottom = static_cast<float>(bottom);
        tile.top = static_cast<float>(bottom - height);
        if (playing && i == v.index && v.midi >= 0) {
          game.keys = 1u << (n.pitch - 1);
          game.inputStyle[n.pitch - 1] = lcd::GameStyle::colorIndex(n.octave, n.sharp);
          tile.hit = tile.holding = true;
        }
        tile.fill = tile.hit
                        ? (i < v.index ? 1.f : static_cast<float>(std::min(1.0, v.elapsedBeats / n.beats)))
                        : 0.f;
      }
    }
  }
  static int previous = -1;
  const int target = menu ? 0 : showGame ? 1 : 2;
  if (previous != target) {
    menuScreen.reset();
    scoreScreen.reset();
    gameScreen.reset();
    previous = target;
  }
  if (menu) {
    const auto actual = engine_.view();
    auto& state = ::menu.state;
    state.keyMask = rawKeyMask_;
    state.previewInstrument = edit.active && edit.item == I::Instrument ? edit.pendingInstrument : -1;
    state.previewOutput = edit.active && edit.item == I::Output ? edit.pendingOutput : -1;
    state.playingTitle = actual.mode == Mode::Manual ? "" : engine_.song().title;
    menuScreen.draw(surface, state, actual, now);
  } else if (showGame) {
    gameScreen.draw(surface, game, now);
  } else {
    scoreScreen.draw(surface, notationView, page, now, &performance);
  }
}
}  // namespace

#define EXPORTED(name) extern "C" __attribute__((export_name(#name)))
EXPORTED(boot) void boot() { init(); }
EXPORTED(keys) void keys(unsigned value) { mask = value & 0x3ff; }
EXPORTED(bank_buffer) uint8_t* bank_buffer() { return bankBytes; }
EXPORTED(bank_capacity) unsigned bank_capacity() { return sizeof(bankBytes); }
EXPORTED(bind_bank) int bind_bank(int id, unsigned size) {
  if (id < 3 || id > kMaxInstrument || size != sizeof(bankBytes) || !SampleBank::validHeader(bankBytes, id))
    return 0;
  bank.bind(bankBytes, size, id);
  auto settings = engine_.view().settings;
  if (settings.instrument != id) ++settingsRevision;
  settings.instrument = id;
  engine_.settings(settings, clockMs());
  manual.reset();
  score = PerformanceMixer(board::kSampleRate, board::kPeakAmplitude);
  return 1;
}
EXPORTED(take_bank_request) int take_bank_request() { return std::exchange(requestedBank, 0); }
EXPORTED(bank_failed) void bank_failed() { notice("音源未载入", clockMs()); }
EXPORTED(restore) void restore(int bpm, int transpose, int volume, int flags) {
  auto settings = engine_.view().settings;
  settings.bpm = std::clamp(bpm, 20, 400);
  settings.transpose = std::clamp(transpose, -12, 12);
  settings.metronome = flags & 1;
  settings.repeat = flags & 2;
  settings.showLyrics = flags & 4;
  menu.state.playbackArrangement = flags & 8 ? PlaybackArrangement::Original : PlaybackArrangement::Melody;
  gameDisplay_ = menu.state.gameDisplay = bool(flags & 16);
  engine_.settings(settings, clockMs());
  engine_.setVolume(volume);
}

EXPORTED(import_buffer) char* import_buffer() { return incoming; }
EXPORTED(import_song) int import_song(unsigned size) {
  if (!size || size > sizeof(incoming)) return 0;
  Song song;
  importError.clear();
  if (!parseSong(incoming, size, song, importError)) return 0;
  if (song.id == "demo-scale" || song.id == "demo-chords") {
    importError = "reserved song ID";
    return 0;
  }
  if (songs.size() >= maxSongs &&
      std::none_of(songs.begin(), songs.end(), [&](const Song& item) { return item.id == song.id; })) {
    importError = "song library full";
    return 0;
  }
  addSong(std::move(song));
  return 1;
}
EXPORTED(import_error) const char* import_error() { return importError.c_str(); }
EXPORTED(deletion) const char* deletion() { return deletedSong.c_str(); }
EXPORTED(clear_deletion) void clear_deletion() { deletedSong.clear(); }
EXPORTED(frame) const uint16_t* frame() {
  draw(clockMs());
  return pixels;
}

EXPORTED(status) const int32_t* status() {
  const auto view = playView(clockMs());
  const auto audio = gameMode_ == 3 ? chordPractice_.audio() : engine_.performanceView();
  const auto& state = menu.state;
  info[0] = local_.menu() ? static_cast<int>(state.page) + 1 : 0;
  info[1] = state.home;
  info[2] = state.row;
  info[3] = static_cast<int>(view.mode);
  info[4] = gameMode_;
  info[5] = static_cast<int>(view.index);
  info[6] = view.paused;
  info[7] = static_cast<int>(std::count_if(audio.voices.begin(), audio.voices.end(),
                                           [](const auto& voice) { return voice.midi >= 0; }));
  info[8] = gameMode_ == 3 ? chordPractice_.targets() : 0;
  info[9] = view.volume;
  info[10] = view.settings.instrument;
  info[11] = view.settings.bpm;
  info[12] = view.settings.transpose;
  info[13] = (view.settings.metronome ? 1 : 0) | (view.settings.repeat ? 2 : 0) |
             (view.settings.showLyrics ? 4 : 0) |
             (state.playbackArrangement == PlaybackArrangement::Original ? 8 : 0) | (gameDisplay_ ? 16 : 0);
  info[14] = settingsRevision;
  info[15] = static_cast<int>(selected);
  info[16] = static_cast<int>(songs.size());
  info[17] = state.editing;
  info[18] = gameMode_ == 3 ? chordPractice_.finished() : view.finished;
  info[19] = static_cast<int>(view.count);
  info[20] = view.expected;
  info[21] = static_cast<int>(audio.positionTicks);
  info[22] = gameScore_;
  info[23] = static_cast<int>(browsing);
  return info;
}

EXPORTED(render) const int16_t* render() {
  View view;
  PerformanceView performance;
  updateControl(clockMs(), view, performance);
  const bool useManual = !gameMode_ && !view.paused && !view.finished &&
                         (view.mode == Mode::Manual || view.mode == Mode::FreeScore);
  const int instrument = playableInstrument(view.settings.instrument, bank);
  if (useManual) {
    if (lastManualInstrument != instrument) manual.reset();
    view.settings.instrument = lastManualInstrument = instrument;
    manual.render(view, bank, pcm, frames);
  } else {
    lastManualInstrument = -1;
    const auto audio = performance.enabled ? performance : melodyAudio(view);
    score.render(audio, bank, instrument, view.volume, view.click, pcm, frames);
  }
  rendered += frames;
  return pcm;
}
