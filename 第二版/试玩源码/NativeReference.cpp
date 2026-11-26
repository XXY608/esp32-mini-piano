#include <array>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <string>
#include <utility>
#include <vector>
#include "ManualMixer.h"
#include "PerformanceMixer.h"
#include "Board2_1Config.h"

int main(int argc, char** argv) {
  if (argc != 4) return 2;
  const int instrument = std::atoi(argv[1]);
  std::ifstream asset(argv[2], std::ios::binary);
  std::vector<uint8_t> data(piano::SampleBank::kBytes);
  if (!asset.read(reinterpret_cast<char*>(data.data()), data.size())) return 3;
  piano::SampleBank bank;
  if (!bank.bind(data.data(), data.size(), instrument)) return 4;
  std::ofstream output(argv[3], std::ios::binary);
  if (!output) return 5;

  std::array<int16_t, 256> pcm{};
  piano::ManualMixer manual(board::kSampleRate, board::kPeakAmplitude);
  piano::PerformanceMixer practice(board::kSampleRate, board::kPeakAmplitude);
  piano::Engine engine;
  piano::Settings settings;
  settings.instrument = instrument;
  engine.settings(settings, 0);
  engine.setVolume(60);
  uint16_t mask = 0;
  for (unsigned block = 0; block < 1660; ++block) {
    const unsigned ms = static_cast<uint64_t>(block) * 128 * 1000 / board::kSampleRate;
    switch (block) {
      case 0: mask = 1; break;
      case 36: mask = 0x15; break;
      case 72: mask = 1; break;
      case 96: mask = 0x201; break; // HIGH
      case 133: mask = 0x181; break; // LOW + SHARP
      case 167: mask = 0; break;
      case 205: mask = 2; break;
      case 1205: mask = 0; break;
      case 1260: {
        engine.keys(0, ms);
        engine.stop();
        piano::Song song;
        song.id = "try-scale"; song.title = "音阶练习";
        song.bpm = 120; song.bpmProvided = true;
        for (int degree : {1,2,3,4,5,6,7,6,5,4,3,2,1}) {
          piano::Note note(piano::noteMidi(degree, 0, false), .5);
          note.pitch = degree; song.notes.push_back(note);
        }
        if (!engine.start(std::move(song), piano::Mode::Simple, ms)) return 6;
        mask = 1; break;
      }
      case 1270: mask = 0; break;
      case 1275: mask = 2; break;
      case 1280: mask = 0; break;
      case 1285: mask = 4; break;
      case 1290: mask = 0; break;
      default: break;
    }
    if (block == 0 || block == 36 || block == 72 || block == 96 || block == 133 ||
        block == 167 || block == 205 || block == 1205 || block == 1260 ||
        block == 1270 || block == 1275 || block == 1280 || block == 1285 || block == 1290)
      engine.keys(mask, ms);
    engine.tick(ms);
    const auto view = engine.view(false);
    if (view.mode == piano::Mode::Manual)
      manual.render(view, bank, pcm.data(), 128);
    else
      practice.render(piano::melodyAudio(view), bank, instrument, view.volume, view.click, pcm.data(), 128);
    output.write(reinterpret_cast<const char*>(pcm.data()), sizeof(pcm));
  }
  return output ? 0 : 7;
}
