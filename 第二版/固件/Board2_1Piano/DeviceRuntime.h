#pragma once
#include <Arduino.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include "PianoCore.h"
#include "LocalControls.h"
#include "NotationLayout.h"
#include "GameScreen.h"
#include "GameAssist.h"
#include "GameFlow.h"
#include "GameHoldBonus.h"
#include "ChordPractice.h"
#include "GuidedPerformance.h"
#include "MenuModel.h"
#include "AudioEvents.h"
#include "SampleVoice.h"

class DeviceRuntime {
 public:
  void begin(int volume);
  bool start(piano::Song song, piano::Mode mode, int gameMode=0,
             piano::PlaybackArrangement arrangement=piano::PlaybackArrangement::Original);
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
  bool usesPhoneOutput(bool effective=true);
  uint16_t keyMask();
  void serviceDisplay(const String& ip, bool flashMounted);
  void gameDisplay(bool enabled);
  void serviceSamples();
  void reloadSamples() { attemptedInstrument_ = -1; }
  String sampleStatus() const { return sampleMessage_; }
  bool prepareInstrument(int instrument, String& error);
  bool audioReady() const { return audioReady_; }
  bool controlReady() const { return controlReady_; }
  bool sdMounted() const { return sdMounted_; }
 private:
  static void audioEntry(void* arg);
  static void controlEntry(void* arg);
  void controlLoop();
  void audioLoop();
  bool loadSampleBank(int instrument);
  piano::Engine engine_;
  piano::AudioEvents audioEvents_;
  piano::NotationLayout notation_;
  uint16_t rawKeyMask_ = 0;
  bool gameDisplay_ = true; unsigned gameScore_ = 0, gameCombo_ = 0; uint8_t gameHits_[piano::kMaxNotes] = {};
  bool simplePractice_ = false;
  uint32_t gameHitSequence_[7] = {}, gameHitTime_[7] = {}; uint8_t gameHitStyle_[7] = {};
  uint16_t gameAcceptedHeld_ = 0;
  int gameMode_=0; piano::GameAssist gameAssist_; piano::GameFlow gameFlow_; piano::GameHoldBonus gameBonus_;
  piano::GuidedPerformance guidedPerformance_;
  piano::ChordPractice chordPractice_;
  int guidedNote_=-1; uint32_t guidedNoteStart_=0;
  piano::LocalControls local_;
  int pendingAction_ = -1;
  piano::MenuState menuState_;
  bool waitRelease_ = false, volumeEditing_ = false;
  SemaphoreHandle_t mutex_ = nullptr, sampleMutex_ = nullptr;
  piano::SampleBank sampleBank_;
  uint8_t* sampleBuffer_ = nullptr;
  int attemptedInstrument_ = -1;
  String sampleMessage_ = "synth";
  volatile bool audioReady_ = false, controlReady_ = false;
  bool sdMounted_ = false;
};
