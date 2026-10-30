#include "DeviceRuntime.h"
#include "Board2_1Config.h"
#include "AudioSynth.h"
#include "AudioOutput.h"
#include "ChineseText.h"
#include "DisplayText.h"
#include "ScoreScreen.h"
#include "MenuScreen.h"
#include "FrameSurface.h"
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
lcd::MenuScreen menuScreen;
uint16_t* displayFrame=nullptr;
uint16_t* displayPrevious=nullptr;
bool displayPresented=false;
portMUX_TYPE toneMux = portMUX_INITIALIZER_UNLOCKED;
int toneMidi = -1, toneVolume = 80;
int toneInstrument = 0;
bool toneClick = false;
uint32_t toneTrigger = 0;
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
  tft.init(240, 320); tft.setSPISpeed(board::kSpiHz); tft.setRotation(board::kDisplayRotation);
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
bool DeviceRuntime::start(piano::Song song, piano::Mode mode) {
  if (!mutex_ || !controlReady_ || !audioReady_) return false;
  piano::NotationLayout notation; notation.build(song);
  xSemaphoreTake(mutex_, portMAX_DELAY);
  const bool ok = engine_.start(std::move(song), mode, millis());
  if (ok) notation_ = std::move(notation);
  xSemaphoreGive(mutex_); return ok;
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
void DeviceRuntime::menuUi(const piano::MenuState& state) { menuState_ = state; }
void DeviceRuntime::closeMenu() {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); local_.close(); pendingAction_ = -1; waitRelease_ = true; xSemaphoreGive(mutex_);
}
void DeviceRuntime::stop() {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); engine_.stop(); xSemaphoreGive(mutex_);
}
void DeviceRuntime::setVolume(int value) {
  if (!mutex_) return;
  xSemaphoreTake(mutex_, portMAX_DELAY); engine_.setVolume(value); xSemaphoreGive(mutex_);
}
piano::View DeviceRuntime::view() {
  if (!mutex_) return piano::View{};
  xSemaphoreTake(mutex_, portMAX_DELAY); const auto v = engine_.view(); xSemaphoreGive(mutex_); return v;
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
bool DeviceRuntime::usesPhoneOutput() {
  const auto now = millis();
  portENTER_CRITICAL(&toneMux); const bool phone = audioOutput.muted(now); portEXIT_CRITICAL(&toneMux);
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
    uint16_t mask = 0; const uint32_t now = millis();
    for (int i = 0; i < 10; ++i) {
      const bool current = digitalRead(board::kKeys[i]) == LOW;
      if (current != raw[i]) { raw[i] = current; changed[i] = now; }
      if (static_cast<uint32_t>(now - changed[i]) >= board::kDebounceMs) stable[i] = current;
      if (stable[i]) mask |= 1u << i;
    }
    xSemaphoreTake(mutex_, portMAX_DELAY);
    const auto before = engine_.view();
    const int action = local_.scan(mask, now, before.mode == piano::Mode::Play && !before.finished);
    if (action == 8) engine_.pause(!before.paused, now);
    else if (action >= 0 && (pendingAction_ < 0 || action == 7)) pendingAction_ = action;
    if (action == 7) waitRelease_ = true;
    if (!mask) waitRelease_ = false;
    engine_.keys(local_.menu() || waitRelease_ ? 0 : mask, now); engine_.tick(now); const auto v = engine_.view();
    rawKeyMask_ = mask;
    audioEvents_.capture(v,now);
    xSemaphoreGive(mutex_);
    portENTER_CRITICAL(&toneMux); toneMidi = v.midi; toneVolume = v.volume; toneInstrument = v.settings.instrument; toneClick = v.click; toneTrigger = v.trigger; portEXIT_CRITICAL(&toneMux);
    vTaskDelay(pdMS_TO_TICKS(2) ? pdMS_TO_TICKS(2) : 1);
  }
}
void DeviceRuntime::audioLoop() {
  int16_t samples[kFrames * 2]; piano::AudioSynth synth(board::kSampleRate, board::kPeakAmplitude);
  piano::SampleVoice sampleVoice;
  for (;;) {
    portENTER_CRITICAL(&toneMux); const int midi = toneMidi, volume = toneVolume, instrument = toneInstrument; const bool click = toneClick; const auto trigger = toneTrigger; portEXIT_CRITICAL(&toneMux);
    // Audio task never touches SD/SPI. A bank switch renders silence/clicks
    // while the main task owns the bank mutex and replaces its PSRAM buffer.
    synth.render(instrument < 3 ? midi : -1, volume, samples, kFrames, instrument < 3 ? instrument : 0, click, trigger);
    if (xSemaphoreTake(sampleMutex_, 0) == pdTRUE) {
      if (instrument >= 3 && sampleBank_.instrument == instrument)
        sampleVoice.mix(sampleBank_, midi, volume, samples, kFrames, board::kPeakAmplitude, click, trigger);
      else sampleVoice.reset();
      xSemaphoreGive(sampleMutex_);
    } else sampleVoice.reset();
    const uint32_t now = millis();
    portENTER_CRITICAL(&toneMux); audioOutput.silence(samples, kFrames * 2, now); portEXIT_CRITICAL(&toneMux);
    size_t offset = 0;
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
  if (requested == attemptedInstrument_) return;
  const uint32_t loadStarted=millis();
  attemptedInstrument_ = requested;
  xSemaphoreTake(sampleMutex_, portMAX_DELAY);
  sampleBank_ = piano::SampleBank{};
  free(sampleBuffer_); sampleBuffer_ = nullptr;
  sampleMessage_ = requested < 3 ? "synth" : "sample load failed";
  if (requested >= 3) {
    const String path = "/piano-samples/" + String(requested) + ".hps";
    File file;
    if (sdMounted_) file = SD.open(path, FILE_READ);
    if (!file) sampleMessage_ = "SD sample file missing: " + path;
    else if (file.size() != piano::SampleBank::kBytes) sampleMessage_ = "Invalid sample file size: " + path;
    else {
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
    }
    if (file) file.close();
    Serial.printf("Sample bank %d: %s; load %lu ms\n", requested, sampleMessage_.c_str(), static_cast<unsigned long>(millis()-loadStarted));
  }
  xSemaphoreGive(sampleMutex_);
  displayPresented=false;
}
void DeviceRuntime::serviceDisplay(const String& ip, bool flashMounted) {
  if (!mutex_) return;
  static uint32_t last = 0;
  const uint32_t now = millis();
  if (static_cast<uint32_t>(now - last) < 33) return;
  last = now;
  xSemaphoreTake(mutex_, portMAX_DELAY);
  const auto v = engine_.view();
  const bool menu = local_.menu();
  const auto page = menu ? piano::ScorePage{} : notation_.page(engine_.song(), v);
  xSemaphoreGive(mutex_);
  static bool previousMenu = false;
  if (!menu) {
    previousMenu=false; menuScreen.reset();
    if(displayFrame) { lcd::FrameSurface frame(displayFrame); scoreScreen.draw(frame,v,page,now); lcd::presentFrame(tft,displayFrame,displayPrevious,!displayPresented); displayPresented=true; }
    else scoreScreen.draw(tft,v,page,now);
    return;
  }
  scoreScreen.reset();
  if(displayFrame) { lcd::FrameSurface frame(displayFrame); menuScreen.draw(frame,menuState_,v,now); lcd::presentFrame(tft,displayFrame,displayPrevious,!displayPresented); displayPresented=true; }
  else menuScreen.draw(tft,menuState_,v,now);
  if (!previousMenu) Serial.printf("Display menu: Wi-Fi=%s storage=%s audio=%s\n", ip.c_str(), lcd::kStorageNames[(sdMounted_ ? 2 : 0) + (flashMounted ? 1 : 0)], audioReady_ ? "ok" : "failed");
  previousMenu = true;
}
