#include "DeviceRuntime.h"
#include "Board2_1Config.h"
#include "AudioSynth.h"
#include "AudioOutput.h"
#include "ChineseText.h"
#include "DisplayText.h"
#include "ScoreScreen.h"
#include "GuidedGame.h"
#include "GameScreen.h"
#include "MenuScreen.h"
#include "FrameSurface.h"
#include "ResetTrace.h"
#include "ManualMixer.h"
#include "PerformanceMixer.h"
#include "PerformanceGame.h"
#include <esp_heap_caps.h>
#include <Adafruit_GFX.h>
#include <Adafruit_ST7789.h>
#include <SD.h>
#include <SPI.h>
#include <cmath>
#include "driver/i2s.h"

namespace {
Adafruit_ST7789 tft(board::kTftCs, board::kTftDc, board::kTftReset);
lcd::ScoreScreen scoreScreen;
lcd::GameScreen gameScreen;
lcd::MenuScreen menuScreen;
uint16_t* displayFrame=nullptr;
uint16_t* displayPrevious=nullptr;
bool displayPresented=false;
portMUX_TYPE toneMux = portMUX_INITIALIZER_UNLOCKED;
piano::View toneView; bool toneManual=false;
piano::PerformanceView tonePerformance;
piano::AudioOutput audioOutput;
constexpr size_t kFrames = 128;
void drawLine(int x, int y, const String& text, uint16_t color = ST77XX_WHITE, int width = 304) {
  lcd::drawText(tft, x, y, text.c_str(), color, width);
}
}

void DeviceRuntime::begin(int volume) {
  mutex_ = xSemaphoreCreateMutex();
  sampleMutex_ = xSemaphoreCreateMutex();
  if (!mutex_ || !sampleMutex_) { Serial.println("Audio/control mutex allocation failed"); return; }
  engine_.setVolume(volume);
  for (int pin : board::kKeys) pinMode(pin, INPUT_PULLUP);
  i2s_config_t config = {};
  config.mode = static_cast<i2s_mode_t>(I2S_MODE_MASTER | I2S_MODE_TX);
  config.sample_rate = board::kSampleRate;
  config.bits_per_sample = I2S_BITS_PER_SAMPLE_16BIT;
  config.channel_format = I2S_CHANNEL_FMT_RIGHT_LEFT;
  config.communication_format = I2S_COMM_FORMAT_STAND_I2S;
  config.intr_alloc_flags = ESP_INTR_FLAG_LEVEL1;
  config.dma_buf_count = 4; config.dma_buf_len = kFrames;
  config.tx_desc_auto_clear = true;
  i2s_pin_config_t pins = {};
  pins.mck_io_num = I2S_PIN_NO_CHANGE;
  pins.bck_io_num = board::kBclk; pins.ws_io_num = board::kLrc;
  pins.data_out_num = board::kDin; pins.data_in_num = I2S_PIN_NO_CHANGE;
  if (i2s_driver_install(I2S_NUM_0, &config, 0, nullptr) == ESP_OK) {
    if (i2s_set_pin(I2S_NUM_0, &pins) == ESP_OK) {
      i2s_zero_dma_buffer(I2S_NUM_0); audioReady_ = true;
      if (xTaskCreatePinnedToCore(audioEntry, "piano-audio", 4096, this, 3, nullptr, 1) != pdPASS) audioReady_ = false;
    }
    if (!audioReady_) i2s_driver_uninstall(I2S_NUM_0);
  }
  controlReady_ = xTaskCreatePinnedToCore(controlEntry, "piano-keys", 4096, this, 2, nullptr, 1) == pdPASS;
  Serial.printf("Audio init=%s, controls init=%s\n", audioReady_ ? "ok" : "failed", controlReady_ ? "ok" : "failed");

  pinMode(board::kTftCs, OUTPUT); digitalWrite(board::kTftCs, HIGH);
  pinMode(board::kSdCs, OUTPUT); digitalWrite(board::kSdCs, HIGH);
  pinMode(board::kBacklight, OUTPUT); digitalWrite(board::kBacklight, HIGH);
  SPI.begin(board::kSck, board::kMiso, board::kMosi, board::kTftCs);
  tft.init(240, 320); tft.invertDisplay(false); tft.setSPISpeed(board::kSpiHz); tft.setRotation(board::kDisplayRotation);
  tft.setTextWrap(false); tft.fillScreen(ST77XX_BLACK);
  drawLine(8, 4, lcd::kTitle, ST77XX_CYAN);
  displayFrame=static_cast<uint16_t*>(heap_caps_calloc(320*240,sizeof(uint16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT));
  displayPrevious=static_cast<uint16_t*>(heap_caps_malloc(320*240*sizeof(uint16_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT));
  if(displayFrame && displayPrevious) std::fill(displayPrevious,displayPrevious+320*240,0xffff);
  else { free(displayFrame); free(displayPrevious); displayFrame=nullptr; displayPrevious=nullptr; Serial.println("Display PSRAM allocation failed; direct drawing fallback"); }
  // Shared SPI is used only from Arduino loop/setup, never audio/key tasks.
  sdMounted_ = SD.begin(board::kSdCs, SPI, board::kSpiHz, "/sd", 5, false);
  Serial.printf("SD mount=%s (no format attempted)\n", sdMounted_ ? "ok" : "unavailable");
}
bool DeviceRuntime::start(piano::Song song, piano::Mode mode, int gameMode, piano::PlaybackArrangement arrangement) {
  if (!mutex_ || !controlReady_ || !audioReady_) return false;
  if (gameMode==3 && !piano::ChordPractice::arrange(song)) return false;
  piano::NotationLayout notation; notation.build(song);
  xSemaphoreTake(mutex_, portMAX_DELAY);
  const uint32_t now=millis();const bool ok = engine_.start(std::move(song), mode, now, arrangement);
  if (ok) { notation_ = std::move(notation); gameScore_=gameCombo_=0;gameAcceptedHeld_=0;guidedNote_=-1;gameBonus_.reset();gameMode_=gameMode;gameAssist_.reset();if(gameMode_==2){engine_.stop();gameFlow_.reset(engine_,now);} std::fill(gameHits_,gameHits_+piano::kMaxNotes,0); }
  if (ok) simplePractice_ = mode==piano::Mode::Simple;
  if (ok && gameMode_==3) { engine_.stop(); chordPractice_.start(engine_.song(),now); }
  else if (ok && gameMode_) guidedPerformance_.start(engine_.song(),now);
  xSemaphoreGive(mutex_); return ok;
}
void DeviceRuntime::gameDisplay(bool enabled) {
  if (!mutex_) return;
  xSemaphoreTake(mutex_,portMAX_DELAY);gameDisplay_=enabled;xSemaphoreGive(mutex_);
  gameScreen.reset();scoreScreen.reset();displayPresented=false;
}
bool DeviceRuntime::pause(bool value) {
  if (!mutex_) return false;
  xSemaphoreTake(mutex_, portMAX_DELAY); const bool ok = engine_.pause(value, millis()); xSemaphoreGive(mutex_); return ok;
}
void DeviceRuntime::settings(const piano::Settings& value) {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); engine_.settings(value, millis()); xSemaphoreGive(mutex_);
}
int DeviceRuntime::takeLocalAction() {
  if (!mutex_) return -1;
  xSemaphoreTake(mutex_, portMAX_DELAY); const int a = pendingAction_; pendingAction_ = -1; xSemaphoreGive(mutex_); return a;
}
void DeviceRuntime::menuUi(const piano::MenuState& state) {
  const bool volume=state.editing && piano::menuItem(state)==piano::MenuItem::Volume;
  const bool wasVolume=menuState_.editing && piano::menuItem(menuState_)==piano::MenuItem::Volume;
  if (mutex_ && volume!=wasVolume) {
    xSemaphoreTake(mutex_,portMAX_DELAY); volumeEditing_=volume; xSemaphoreGive(mutex_);
  }
  menuState_ = state;
}
void DeviceRuntime::closeMenu() {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); local_.close(); pendingAction_ = -1; waitRelease_ = true; xSemaphoreGive(mutex_);
}
void DeviceRuntime::stop() {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); engine_.stop();gameMode_=0;simplePractice_=false;gameAssist_.reset();gameBonus_.reset(); xSemaphoreGive(mutex_);
}
void DeviceRuntime::setVolume(int value) {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); engine_.setVolume(value); xSemaphoreGive(mutex_);
}
piano::View DeviceRuntime::view() {
  if (!mutex_) return piano::View{};
  xSemaphoreTake(mutex_, portMAX_DELAY);
  auto v=gameMode_==2?gameFlow_.view(engine_,millis()):engine_.view();
  if(gameMode_==3) { v.mode=piano::Mode::FreeScore; v.finished=chordPractice_.finished(); }
  xSemaphoreGive(mutex_); return v;
}
String DeviceRuntime::songId() {
  if (!mutex_) return "";
  xSemaphoreTake(mutex_, portMAX_DELAY); String id(engine_.song().id.c_str()); xSemaphoreGive(mutex_); return id;
}
String DeviceRuntime::songTitle() {
  if (!mutex_) return "";
  xSemaphoreTake(mutex_, portMAX_DELAY); String title(engine_.song().title.c_str()); xSemaphoreGive(mutex_); return title;
}
uint16_t DeviceRuntime::keyMask() {
  if (!mutex_) return 0;
  xSemaphoreTake(mutex_, portMAX_DELAY); const auto mask = rawKeyMask_; xSemaphoreGive(mutex_); return mask;
}
String DeviceRuntime::audioEvents(uint32_t after) {
  std::array<piano::AudioEvent,64> events; bool reset=false;
  if(!mutex_) return "{\"ok\":false}";
  xSemaphoreTake(mutex_,portMAX_DELAY); const auto count=audioEvents_.read(after,events,reset); xSemaphoreGive(mutex_);
  String body=reset?"{\"reset\":true,\"events\":[":"{\"reset\":false,\"events\":[";
  for(unsigned i=0;i<count;++i) {
    if(i) body+=','; const auto& e=events[i];
    body+="["+String(e.seq)+","+String(e.ms)+","+String(e.midi)+","+String(e.volume)+","+String(e.instrument)+","+String(e.trigger)+","+String(e.click?1:0)+"]";
  }
  body += "],\"phoneOutput\":";
  body += usesPhoneOutput() ? "true}" : "false}";
  return body;
}
bool DeviceRuntime::selectPhoneOutput(bool phone) {
  const auto now = millis();
  portENTER_CRITICAL(&toneMux); const bool ok = audioOutput.selectPhone(phone, now); portEXIT_CRITICAL(&toneMux);
  return ok;
}
bool DeviceRuntime::phoneOutputReady() {
  const auto now = millis();
  portENTER_CRITICAL(&toneMux); const bool ready = audioOutput.ready(now); portEXIT_CRITICAL(&toneMux);
  return ready;
}
bool DeviceRuntime::usesPhoneOutput(bool effective) {
  const auto now = millis();
  portENTER_CRITICAL(&toneMux);
  const bool phone = audioOutput.muted(now) && (!effective || !piano::usesLocalScoreAudio(toneView,tonePerformance));
  portEXIT_CRITICAL(&toneMux);
  return phone;
}
bool DeviceRuntime::phoneOutput(uint32_t session, bool enabled) {
  const uint32_t now = millis();
  portENTER_CRITICAL(&toneMux);
  const bool ok = enabled ? audioOutput.acquire(session, now) : audioOutput.release(session);
  portEXIT_CRITICAL(&toneMux);
  // Allow the four queued I2S buffers to drain before the phone starts sounding.
  if (enabled && ok) vTaskDelay(pdMS_TO_TICKS(40));
  return ok;
}
bool DeviceRuntime::renewPhoneOutput(uint32_t session) {
  const uint32_t now = millis();
  portENTER_CRITICAL(&toneMux); const bool ok = audioOutput.renew(session, now); portEXIT_CRITICAL(&toneMux);
  return ok;
}
void DeviceRuntime::audioEntry(void* arg) { static_cast<DeviceRuntime*>(arg)->audioLoop(); }
void DeviceRuntime::controlEntry(void* arg) { static_cast<DeviceRuntime*>(arg)->controlLoop(); }
void DeviceRuntime::controlLoop() {
  bool raw[10] = {}, stable[10] = {}; uint32_t changed[10] = {};
  for (;;) {
    resetTrace.control=1; uint16_t mask = 0; const uint32_t now = millis();
    for (int i = 0; i < 10; ++i) {
      const bool current = digitalRead(board::kKeys[i]) == LOW;
      if (current != raw[i]) { raw[i] = current; changed[i] = now; }
      if (static_cast<uint32_t>(now - changed[i]) >= board::kDebounceMs) stable[i] = current;
      if (stable[i]) mask |= 1u << i;
    }
    resetTrace.control=2; xSemaphoreTake(mutex_, portMAX_DELAY);
    resetTrace.control=3; const auto before = gameMode_==2?gameFlow_.view(engine_,now):engine_.view();
    const int action = local_.scan(mask, now, before.mode == piano::Mode::Play && !before.finished, volumeEditing_);
    if (action == 8) engine_.pause(!before.paused, now);
    else if (action >= 0 && (pendingAction_ < 0 || action == 7)) pendingAction_ = action;
    if (action == 7) waitRelease_ = true;
    if (!mask) waitRelease_ = false;
    const uint16_t input=local_.menu() || waitRelease_ ? 0 : mask;
    const bool chord=gameMode_==3;
    gameAcceptedHeld_ &= input;
    const uint16_t rising=(input & ~before.keyMask) & 0x7f;
    int gameAccepted=-1;bool gameAutomatic=false;
    if(gameMode_==1){const auto result=gameAssist_.step(engine_,input,now);gameAccepted=result.accepted;gameAutomatic=result.automatic;}
    else if(gameMode_==2){const auto result=gameFlow_.step(engine_,input,now);gameAccepted=result.accepted;gameAutomatic=result.automatic;}
    else if(chord) { chordPractice_.step(engine_.song(),before.settings,input,now); engine_.tick(now); }
    else engine_.keys(input,now);
    const auto pressed=engine_.view();
    if (gameAccepted >= 0) {
      guidedNote_ = gameAccepted;
      guidedNoteStart_ = now;
    }
    if(gameDisplay_ && !local_.menu())gameScore_+=gameBonus_.release(input,gameMode_==2?gameFlow_.view(engine_,now):pressed,gameMode_==2);
    else if(local_.menu())gameBonus_.reset();
    if(gameMode_ && gameDisplay_ && gameAccepted>=0 && static_cast<size_t>(gameAccepted)<engine_.song().notes.size() && gameAccepted<static_cast<int>(piano::kMaxNotes)){
      const auto& note=engine_.song().notes[gameAccepted];const int degree=note.pitch;
      if(degree>=1&&degree<=7 && !gameHits_[gameAccepted]){if(!gameAutomatic)gameBonus_.accepted(gameAccepted,note);gameHits_[gameAccepted]=1;gameScore_+=100;++gameCombo_;}
    }
    if(!gameMode_ && gameDisplay_ && rising && !before.paused && before.mode!=piano::Mode::Manual && before.mode!=piano::Mode::Play && before.index<piano::kMaxNotes){
      const bool accepted=before.mode==piano::Mode::Simple ? pressed.index>before.index : !before.noteActive && pressed.noteActive && pressed.index==before.index;
      if(accepted && !gameHits_[before.index]){gameHits_[before.index]=1;gameScore_+=100;++gameCombo_;const auto& note=engine_.song().notes[before.index];const auto degree=note.pitch;if(degree>=1&&degree<=7){++gameHitSequence_[degree-1];gameHitTime_[degree-1]=now;gameHitStyle_[degree-1]=lcd::GameStyle::colorIndex(note.octave,note.sharp);gameAcceptedHeld_|=1u<<(degree-1);}}
    }
    if(!gameMode_){engine_.tick(now);}
    const auto v = gameMode_==2?gameFlow_.view(engine_,now):engine_.view();
    if (gameAccepted < 0 && v.index < before.index) guidedNote_ = -1;
    if(gameDisplay_ && v.songGeneration==before.songGeneration){
      if(v.index<before.index)std::fill(gameHits_,gameHits_+piano::kMaxNotes,0);
      else for(size_t i=before.index;i<v.index && i<engine_.song().notes.size() && i<piano::kMaxNotes;++i)if(engine_.song().notes[i].midi>=0 && !gameHits_[i])gameCombo_=0;
    }
    rawKeyMask_ = mask;
    const bool backing=gameMode_ && !chord && guidedPerformance_.view().enabled;
    if (backing) {
      if (v.index<before.index) guidedPerformance_.rewind(now);
      guidedPerformance_.step(engine_.song(),v,gameAccepted,now,local_.menu() || waitRelease_);
    }
    const auto performance = chord ? chordPractice_.audio() : backing ? guidedPerformance_.view() : engine_.performanceView();
    auto phoneView = v;
    if (phoneView.settings.instrument == 11) phoneView.settings.instrument = 4;
    if (piano::usesLocalScoreAudio(v,performance)) { phoneView.midi = -1; phoneView.click = false; }
    audioEvents_.capture(phoneView, now);
    const bool manual=gameMode_==0 && !v.paused && !v.finished && (v.mode==piano::Mode::Manual || v.mode==piano::Mode::FreeScore);
    xSemaphoreGive(mutex_);
    resetTrace.control=4; portENTER_CRITICAL(&toneMux); toneView=v;toneManual=manual; tonePerformance=performance; portEXIT_CRITICAL(&toneMux);
    vTaskDelay(pdMS_TO_TICKS(2) ? pdMS_TO_TICKS(2) : 1);
  }
}
void DeviceRuntime::audioLoop() {
  int16_t samples[kFrames * 2]; piano::AudioSynth synth(board::kSampleRate, board::kPeakAmplitude);
  static piano::ManualMixer manualMixer(board::kSampleRate,board::kPeakAmplitude);
  int lastManualInstrument = -1;
  static piano::PerformanceMixer performanceMixer(board::kSampleRate, board::kPeakAmplitude);
  for (;;) {
    resetTrace.audio=1; portENTER_CRITICAL(&toneMux); auto view=toneView; const bool manual=toneManual; const auto performance=tonePerformance; portEXIT_CRITICAL(&toneMux);
    // Audio task never touches SD/SPI. A bank switch renders silence/clicks
    // while the main task owns the bank mutex and replaces its PSRAM buffer.
    resetTrace.audio=2;
    if (xSemaphoreTake(sampleMutex_, 0) == pdTRUE) {
      const int actual = piano::playableInstrument(view.settings.instrument, sampleBank_);
      if (manual) {
        if (actual != lastManualInstrument) manualMixer.reset();
        view.settings.instrument = lastManualInstrument = actual;
        manualMixer.render(view, sampleBank_, samples, kFrames);
      } else {
        lastManualInstrument = -1;
        const auto audio=performance.enabled?performance:piano::melodyAudio(view);
        performanceMixer.render(audio, sampleBank_, actual, view.volume, view.click, samples, kFrames);
      }
      xSemaphoreGive(sampleMutex_);
    } else synth.render(-1, view.volume, samples, kFrames, 0, view.click);
    resetTrace.audio=3; const uint32_t now = millis();
    if (!piano::usesLocalScoreAudio(view,performance)) {
      portENTER_CRITICAL(&toneMux); audioOutput.silence(samples, kFrames * 2, now); portEXIT_CRITICAL(&toneMux);
    }
    resetTrace.audio=4; size_t offset = 0;
    while (offset < sizeof(samples)) {
      size_t written = 0;
      const auto err = i2s_write(I2S_NUM_0, reinterpret_cast<uint8_t*>(samples) + offset, sizeof(samples) - offset, &written, pdMS_TO_TICKS(100));
      if (err != ESP_OK || written == 0) { audioReady_ = false; i2s_zero_dma_buffer(I2S_NUM_0); vTaskDelay(pdMS_TO_TICKS(10)); break; }
      offset += written;
    }
  }
}
void DeviceRuntime::serviceSamples() {
  if (!mutex_ || !sampleMutex_) return;
  const int requested = view().settings.instrument;
  if (requested != attemptedInstrument_) loadSampleBank(requested);
}
bool DeviceRuntime::prepareInstrument(int requested, String& error) {
  if (!mutex_ || !sampleMutex_) { error = "audio not initialized"; return false; }
  const int previous = view().settings.instrument;
  if (requested == previous && piano::playableInstrument(previous, sampleBank_) == previous) return true;
  if (loadSampleBank(requested)) return true;
  error = sampleMessage_;
  if (previous < 3) sampleMessage_ = "synth";
  else if (sampleBank_.instrument == previous) sampleMessage_ = "samples ready";
  else loadSampleBank(previous);
  attemptedInstrument_ = previous;
  return false;
}
bool DeviceRuntime::loadSampleBank(int requested) {
  const uint32_t loadStarted = millis();
  attemptedInstrument_ = requested;
  File file;
  String path;
  if (requested >= 3) {
    path = "/piano-samples/" + String(requested) + ".hps";
    uint8_t header[32];
    if (!sdMounted_) sampleMessage_ = "SD not mounted";
    else if (!(file = SD.open(path, FILE_READ))) sampleMessage_ = "SD sample file missing: " + path;
    else if (file.size() != piano::SampleBank::kBytes) sampleMessage_ = "Invalid sample file size: " + path;
    else if (file.read(header, sizeof(header)) != sizeof(header) || !piano::SampleBank::validHeader(header, requested) || !file.seek(0))
      sampleMessage_ = "Invalid sample header: " + path;
    else sampleMessage_ = "loading samples";
    if (sampleMessage_ != "loading samples") {
      if (file) file.close();
      Serial.printf("Sample bank %d: %s\n", requested, sampleMessage_.c_str());
      return false;
    }
  }
  xSemaphoreTake(sampleMutex_, portMAX_DELAY);
  sampleBank_ = piano::SampleBank{};
  free(sampleBuffer_); sampleBuffer_ = nullptr;
  sampleMessage_ = requested < 3 ? "synth" : "sample load failed";
  if (requested >= 3) {
    sampleBuffer_ = static_cast<uint8_t*>(heap_caps_malloc(piano::SampleBank::kBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    if (!sampleBuffer_) sampleMessage_ = "Not enough PSRAM for samples; largest block=" + String(heap_caps_get_largest_free_block(MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    else {
      size_t received = 0;
      while (received < piano::SampleBank::kBytes) {
        size_t count = file.read(sampleBuffer_ + received, std::min(size_t(16384), piano::SampleBank::kBytes - received));
        if (!count) break;
        received += count; delay(1);
      }
      if (sampleBank_.bind(sampleBuffer_, received, requested)) sampleMessage_ = "samples ready";
      else {
        sampleMessage_ = received!=piano::SampleBank::kBytes ? "Incomplete SD sample read: " + path + " (" + String(received) + "/" + String(piano::SampleBank::kBytes) + ")" : "Invalid sample header: " + path;
        free(sampleBuffer_); sampleBuffer_=nullptr;
      }
    }
    if (file) file.close();
    Serial.printf("Sample bank %d: %s; load %lu ms\n", requested, sampleMessage_.c_str(), static_cast<unsigned long>(millis()-loadStarted));
  }
  xSemaphoreGive(sampleMutex_);
  displayPresented=false;
  return requested < 3 || sampleBank_.instrument == requested;
}
void DeviceRuntime::serviceDisplay(const String& ip, bool flashMounted) {
  if (!mutex_) return;
  static uint32_t last = 0;
  const uint32_t now = millis();
  if (static_cast<uint32_t>(now - last) < 33) return;
  last = now;
  xSemaphoreTake(mutex_, portMAX_DELAY);
  const auto v = gameMode_==2?gameFlow_.view(engine_,now):engine_.view();
  const bool chord = gameMode_==3;
  const bool showSimple = v.mode==piano::Mode::Simple || (simplePractice_ && v.finished);
  const bool menu = local_.menu(), showGame=chord || showSimple || gameDisplay_;
  const auto performance = engine_.performanceView();
  const bool backing = gameMode_ && !chord && guidedPerformance_.view().enabled;
  const bool showPerformance = lcd::showsPerformance(performance,v);
  auto notationView = v;
  if (!menu && !showGame && gameMode_) {
    notationView.noteActive = lcd::isGuidedNoteSounding(engine_.song(), v, guidedNote_);
    if (notationView.noteActive) {
      notationView.index = guidedNote_;
      const double heldBeats = (now - guidedNoteStart_) * (v.settings.bpm / 60000.0);
      notationView.elapsedBeats = std::min(engine_.song().notes[guidedNote_].beats, heldBeats);
    } else notationView.elapsedBeats = std::max(0.0, v.elapsedBeats);
  }
  const auto page = menu || showGame ? piano::ScorePage{} : notation_.page(engine_.song(), notationView);
  static lcd::GuidedGame guidedGame;
  static lcd::PerformanceLayout performanceLayout;
  static lcd::GameFrame game;game.count=0;game.ready=false;game.polyphonic=false;game.guided=false;game.keySlots=false;game.guideMask=0;
  if(!menu && showGame){
    const auto& song=engine_.song();game.ready=!song.notes.empty();game.title=song.title;game.mode=gameMode_==1?"单键伴奏":gameMode_==2?"自主节奏":lcd::kModeNames[static_cast<int>(v.mode)];game.score=gameScore_;game.combo=gameCombo_;game.keys=rawKeyMask_;game.paused=v.paused;game.pauseAvailable=v.mode==piano::Mode::Play && !v.finished;game.session=v.songGeneration;
    game.volume=v.volume;
    game.instrument=v.settings.instrument;
    if (v.mode==piano::Mode::Play) game.mode=showPerformance?"播放 多音":"播放 单音";
    if (showSimple) {
      game.mode=v.finished?lcd::kFinished:lcd::kSimplePractice;
      const auto playback=engine_.view(false);
      guidedGame.update(game,song,v,engine_.simpleSoundingNote(),0,now,&playback);
    } else if (chord) {
      game.mode=chordPractice_.finished()?lcd::kFinished:lcd::kComplexPractice;
      game.guided=true;
      game.guideMask=chordPractice_.targets();
      const auto display=chordPractice_.display();
      lcd::performanceGame(game,performanceLayout,song,v,display,chordPractice_.firstCue(),chordPractice_.cueCount(),true);
    } else if (backing) {
      game.mode+=" 多音";
      const auto& backingView=guidedPerformance_.view();
      auto originalView=v;
      originalView.finished=backingView.positionTicks>=backingView.endTick;
      game.guided=true;
      const int degree=guidedPerformance_.nextDegree(song);
      if(degree) game.guideMask=1u<<(degree-1);
      lcd::performanceGame(game,performanceLayout,song,originalView,backingView,guidedPerformance_.nextEvent());
    } else if (showPerformance) {
      lcd::performanceGame(game,performanceLayout,song,v,performance);
    } else if (gameMode_) {
      game.mode+=" 单音";
      guidedGame.update(game, song, v, guidedNote_, now - guidedNoteStart_, now);
    } else {
      for(int i=0;i<7;i++){game.hitSequence[i]=gameHitSequence_[i];game.hitTime[i]=gameHitTime_[i];game.hitStyle[i]=gameHitStyle_[i];game.inputStyle[i]=lcd::GameStyle::colorIndex((v.high?1:0)-(v.low?1:0),v.sharp);}
      if(v.mode!=piano::Mode::FreeScore && v.noteActive && v.manualKey>=0 && v.manualKey<7 && (gameAcceptedHeld_&(1u<<v.manualKey)))game.inputStyle[v.manualKey]=gameHitStyle_[v.manualKey];
      const bool playing=v.mode==piano::Mode::Play;
      if (playing) game.keys=0;
      double total=0,current=0;for(size_t i=0;i<song.notes.size();++i){if(i<v.index)current+=song.notes[i].beats;total+=song.notes[i].beats;}
      current+=v.elapsedBeats;game.progress=total>0?static_cast<float>(std::min(1.0,current/total)):0;
      double beat=0;const double speed=lcd::GameStyle::pixelsPerMs,msBeat=60000.0/std::max(20,v.settings.bpm);
      for(size_t i=0;i<song.notes.size();++i){const auto& n=song.notes[i];const double bottom=lcd::GameStyle::line-(beat-current)*msBeat*speed;const double height=std::max(20.0,n.beats*msBeat*speed);beat+=n.beats;
        if(n.pitch<1 || n.pitch>7 || bottom<lcd::GameStyle::boardTop || bottom-height>lcd::GameStyle::line || game.count>=lcd::GameFrame::MaxTiles)continue;
        auto& tile=game.tiles[game.count++];tile=lcd::GameTile{};
        tile.degree=n.pitch;tile.octave=n.octave;tile.sharp=n.sharp;tile.hit=i<piano::kMaxNotes && gameHits_[i];tile.holding=tile.hit && i==v.index && v.noteActive && (v.keyMask&(1u<<(n.pitch-1)));tile.bottom=static_cast<float>(bottom);tile.top=static_cast<float>(bottom-height);
        if (playing && i==v.index && v.midi>=0) {
          game.keys=1u<<(n.pitch-1);
          game.inputStyle[n.pitch-1]=lcd::GameStyle::colorIndex(n.octave,n.sharp);
          tile.hit=tile.holding=true;
        }
        tile.fill=tile.hit?(i<v.index?1.f:static_cast<float>(std::min(1.0,v.elapsedBeats/n.beats))):0.f;
      }
    }
  }
  xSemaphoreGive(mutex_);
  static bool previousMenu = false;
  if (!menu) {
    previousMenu=false; menuScreen.reset();
    if(showGame){
      scoreScreen.reset();
      if(displayFrame){lcd::FrameSurface frame(displayFrame);gameScreen.draw(frame,game,now);lcd::presentFrame(tft,displayFrame,displayPrevious,!displayPresented);displayPresented=true;}
      else {static uint32_t directLast=0;if(static_cast<uint32_t>(now-directLast)>=250){directLast=now;gameScreen.draw(tft,game,now);}}
      return;
    }
    gameScreen.reset();
  if(displayFrame) { lcd::FrameSurface frame(displayFrame); scoreScreen.draw(frame,notationView,page,now,&performance); lcd::presentFrame(tft,displayFrame,displayPrevious,!displayPresented); displayPresented=true; }
  else scoreScreen.draw(tft,notationView,page,now,&performance);
    return;
  }
  scoreScreen.reset();gameScreen.reset();
  if(displayFrame) { lcd::FrameSurface frame(displayFrame); menuScreen.draw(frame,menuState_,v,now); lcd::presentFrame(tft,displayFrame,displayPrevious,!displayPresented); displayPresented=true; }
  else menuScreen.draw(tft,menuState_,v,now);
  if (!previousMenu) Serial.printf("Display menu: Wi-Fi=%s storage=%s audio=%s\n", ip.c_str(), lcd::kStorageNames[(sdMounted_ ? 2 : 0) + (flashMounted ? 1 : 0)], audioReady_ ? "ok" : "failed");
  previousMenu = true;
}
