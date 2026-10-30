#pragma once
#include <Arduino.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include "PianoCore.h"
#include "LocalControls.h"
#include "NotationLayout.h"
#include "MenuModel.h"
#include "AudioEvents.h"
#include "SampleVoice.h"

class DeviceRuntime {
 public:
  void begin(int volume);
  bool start(piano::Song song, piano::Mode mode);
  void stop();
  void setVolume(int value);
  bool pause(bool value);
  void settings(const piano::Settings& value);
  int takeLocalAction();
  void menuUi(const piano::MenuState& state);
  void closeMenu();
  piano::View view();
  String songId();
  String songTitle();
  String audioEvents(uint32_t after);
  bool phoneOutput(uint32_t session, bool enabled);
  bool renewPhoneOutput(uint32_t session);
  bool selectPhoneOutput(bool phone);
  bool phoneOutputReady();
  bool usesPhoneOutput();
  uint16_t keyMask();
  void serviceDisplay(const String& ip, bool flashMounted);
  void serviceSamples();
  void reloadSamples() { attemptedInstrument_ = -1; }
  String sampleStatus() const { return sampleMessage_; }
  bool audioReady() const { return audioReady_; }
  bool controlReady() const { return controlReady_; }
  bool sdMounted() const { return sdMounted_; }
 private:
  static void audioEntry(void* arg);
  static void controlEntry(void* arg);
  void controlLoop();
  void audioLoop();
  piano::Engine engine_;
  piano::AudioEvents audioEvents_;
  piano::NotationLayout notation_;
  uint16_t rawKeyMask_ = 0;
  piano::LocalControls local_;
  int pendingAction_ = -1;
  piano::MenuState menuState_;
  bool waitRelease_ = false;
  SemaphoreHandle_t mutex_ = nullptr, sampleMutex_ = nullptr;
  piano::SampleBank sampleBank_;
  uint8_t* sampleBuffer_ = nullptr;
  int attemptedInstrument_ = -1;
  String sampleMessage_ = "synth";
  volatile bool audioReady_ = false, controlReady_ = false;
  bool sdMounted_ = false;
};
