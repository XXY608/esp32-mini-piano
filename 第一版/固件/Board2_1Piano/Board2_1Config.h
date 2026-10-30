#pragma once
#include <stdint.h>

// Board2_1, original module orientation and pad nets. No new controls.
namespace board {
constexpr int kKeys[] = {4, 5, 6, 7, 38, 39, 40, 41, 42, 47};
constexpr int kBclk = 17, kLrc = 18, kDin = 21;
constexpr int kSck = 12, kMosi = 11, kMiso = 13;
constexpr int kTftCs = 10, kTftDc = 14, kTftReset = 15, kBacklight = 16;
constexpr int kSdCs = 2;
constexpr uint32_t kSpiHz = 8000000;
constexpr uint32_t kSampleRate = 22050;
// 100%: near the 5V MAX98357A / 4-ohm speaker's clean output ceiling.
// Open GAIN = 9dB: 28672/32767 -> about 3.14Vrms / 2.47W for a sine.
// The speaker is rated 3W (5W maximum); actual board output needs measurement.
// Pure-tone notes above C3 are attenuated for register loudness balance.
constexpr int kPeakAmplitude = 28672;
constexpr unsigned kDebounceMs = 25;
constexpr uint8_t kDisplayRotation = 3; // horizontal display facing the player at the key row
constexpr unsigned kMaxPayloadBytes = 120 * 1024;
constexpr char kVersion[] = "0.6.16-board2_1";
}

// Optional private header, excluded from delivery archives. Never print credentials.
#if __has_include("Board2_1Secrets.h")
#include "Board2_1Secrets.h"
#else
constexpr char HP_WIFI_SSID[] = "";
constexpr char HP_WIFI_PASSWORD[] = "";
constexpr char HP_AP_SSID[] = "HarmonicaPlayer-ESP32";
constexpr char HP_AP_PASSWORD[] = "harmonica";
#endif
