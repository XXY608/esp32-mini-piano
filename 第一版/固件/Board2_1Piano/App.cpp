#include <Arduino.h>
#include <FS.h>
#include <LittleFS.h>
#include <Preferences.h>
#include <WebServer.h>
#include <WiFi.h>

#include "App.h"
#include "Board2_1Config.h"
#include "DeviceRuntime.h"
#include "StorageTransaction.h"
#include "SampleUpload.h"
#include "BuiltinSongs.h"
#include "WebUi.h"
#include "DisplayText.h"
#include <SD.h>
#include <esp_heap_caps.h>
#include "cJSON.h"
#include <algorithm>
#include <cstdlib>
#include <cctype>

namespace {

constexpr uint16_t HTTP_PORT = 80;
constexpr size_t MAX_UPLOAD_BYTES = 120 * 1024;
constexpr char SONG_DIRECTORY[] = "/songs";
constexpr char API_VERSION[] = "1";
constexpr const char* FIRMWARE_VERSION = board::kVersion;

WebServer server(HTTP_PORT);
Preferences preferences;
String deviceId;
String lastMessage = "ready";
String activeSongId;
int outputVolumePercent = 80;
unsigned long lastStaAttempt = 0;
DeviceRuntime runtime;
bool flashMounted = false, preferencesReady = false, apReady = false;
std::vector<piano::MenuSongSummary> menuSongs;
bool localSongsDirty = true;
size_t menuIndex = 0, browseIndex = 0;
piano::MenuEdit menuEdit;
std::string selectedSongId = "demo-scale";
piano::MenuController menuController;
uint32_t menuNoticeAt = 0;
String incomingBody;
bool bodyTooLarge = false;
piano::SampleUpload sampleUpload;
File sampleUploadFile;
String sampleUploadPath;
void cancelSampleUpload() {
  if(sampleUploadFile) sampleUploadFile.close();
  if(sampleUpload.active) SD.remove(sampleUploadPath+".tmp");
  sampleUpload.active=false;
}


String jsonEscape(const String& value) {
  String result;
  result.reserve(value.length() + 8);
  for (size_t i = 0; i < value.length(); ++i) {
    const char c = value.charAt(i);
    switch (c) {
      case '"': result += "\\\""; break;
      case '\\': result += "\\\\"; break;
      case '\b': result += "\\b"; break;
      case '\f': result += "\\f"; break;
      case '\n': result += "\\n"; break;
      case '\r': result += "\\r"; break;
      case '\t': result += "\\t"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20) {
          result += ' ';
        } else {
          result += c;
        }
        break;
    }
  }
  return result;
}



bool isSafeSongId(const String& id) { return piano::safeId(id.c_str()); }

String songPath(const String& id) {
  return String(SONG_DIRECTORY) + "/" + id + ".json";
}

void sendJson(int statusCode, const String& body) {
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.sendHeader("Access-Control-Allow-Headers", "Content-Type, X-Sample-Session, X-Sample-Offset");
  server.sendHeader("Access-Control-Allow-Methods", "GET,POST,DELETE,OPTIONS");
  server.send(statusCode, "application/json; charset=utf-8", body);
}

void sendResponse(bool ok, const String& message, const String& songId = "") {
  String body = "{\"ok\":";
  body += ok ? "true" : "false";
  body += ",\"message\":\"";
  body += jsonEscape(message);
  body += "\"";
  if (!songId.isEmpty()) {
    body += ",\"songId\":\"";
    body += jsonEscape(songId);
    body += "\"";
  }
  body += "}";
  sendJson(ok ? 200 : 400, body);
}


bool readStored(fs::FS& fs, const String& path, String& text) {
  File f = fs.open(path, FILE_READ);
  if (!f || f.isDirectory() || f.size() == 0 || f.size() > MAX_UPLOAD_BYTES) { if (f) f.close(); return false; }
  const size_t expected = f.size();
  if (!text.reserve(expected + 1)) { f.close(); return false; }
  text = f.readString(); f.close();
  return text.length() == expected;
}
fs::FS* findStore(const String& id) {
  const String path = songPath(id);
  if (runtime.sdMounted() && SD.exists(path)) return &SD;
  if (flashMounted && LittleFS.exists(path)) return &LittleFS;
  return nullptr;
}
fs::FS* primaryStore() {
  if (runtime.sdMounted()) return &SD;
  return flashMounted ? &LittleFS : nullptr;
}
bool loadSong(const String& id, piano::Song& song, std::string& error) {
  if (!isSafeSongId(id)) { error = "invalid song id"; return false; }
  if (piano::builtinSong(id.c_str(), song)) return true;
  fs::FS* fs = findStore(id); String text;
  if (!fs || !readStored(*fs, songPath(id), text)) { error = "song missing or unreadable"; return false; }
  if (!piano::parseSong(text.c_str(), text.length(), song, error)) return false;
  if (song.id != id.c_str()) { error = "song file id mismatch"; return false; }
  return true;
}
struct StoreIO {
  fs::FS& fs;
  bool exists(const String& path) { return fs.exists(path); }
  bool remove(const String& path) { return fs.remove(path); }
  bool rename(const String& from, const String& to) { return fs.rename(from, to); }
  bool read(const String& path, String& text) { return readStored(fs, path, text); }
  bool write(const String& path, const String& text) {
    File f = fs.open(path, FILE_WRITE); if (!f) return false;
    const size_t written = f.print(text); f.flush(); f.close(); return written == text.length();
  }
};
bool writeSong(const piano::Song& song, const String& body, String& error) {
  fs::FS* fs = primaryStore();
  if (!fs) { error = "no mounted song storage; no automatic format"; return false; }
  if (!fs->exists(SONG_DIRECTORY) && !fs->mkdir(SONG_DIRECTORY)) { error = "cannot create song directory"; return false; }
  StoreIO store{*fs}; return piano::commitSong(store, songPath(song.id.c_str()), body, error);
}
void recoverBackups(fs::FS& fs) {
  File directory = fs.open(SONG_DIRECTORY, FILE_READ);
  if (!directory) { fs.mkdir(SONG_DIRECTORY); return; }
  std::vector<String> backups;
  for (File f = directory.openNextFile(); f; f = directory.openNextFile()) {
    const String path = f.path(); f.close();
    if (path.endsWith(".json.bak")) backups.push_back(path);
  }
  directory.close();
  for (const auto& backup : backups) {
    const String target = backup.substring(0, backup.length() - 4);
    const String id = target.substring(String(SONG_DIRECTORY).length() + 1, target.length() - 5);
    if (isSafeSongId(id) && !fs.exists(target)) fs.rename(backup, target);
  }
}
String storageLabel() {
  if (runtime.sdMounted()) return flashMounted ? "SD + flash fallback" : "SD";
  return flashMounted ? "Flash (SD unavailable)" : "NO SONG STORAGE";
}
void* jsonAllocate(size_t bytes) {
  void* memory = heap_caps_malloc(bytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  return memory ? memory : std::malloc(bytes);
}

String currentMode() {
  const bool sta = WiFi.status() == WL_CONNECTED;
  if (sta) return "sta+ap";
  return "ap";
}

String ipString() {
  if (WiFi.status() == WL_CONNECTED) return WiFi.localIP().toString();
  return WiFi.softAPIP().toString();
}

void handleStatus() {
  String body;
  body.reserve(420);
  body = "{\"ok\":true,\"supportsLyricCues\":true,\"deviceId\":\"";
  body += jsonEscape(deviceId);
  body += "\",\"name\":\"ESP32-S3 HarmonicaPlayer\",\"firmwareVersion\":\"";
  body += FIRMWARE_VERSION;
  body += "\",\"apiVersion\":\"";
  body += API_VERSION;
  body += "\",\"mode\":\"";
  body += currentMode();
  body += "\",\"ip\":\"";
  body += ipString();
  body += "\",\"apSsid\":\"";
  body += jsonEscape(HP_AP_SSID);
  body += "\",\"apIp\":\"";
  body += WiFi.softAPIP().toString();
  body += "\",\"volumePercent\":";
  body += String(outputVolumePercent);
  const auto playback = runtime.view();
  body += ",\"audioReady\":"; body += runtime.audioReady() ? "true" : "false";
  body += ",\"controlsReady\":"; body += runtime.controlReady() ? "true" : "false";
  body += ",\"playerMode\":\""; body += playback.mode == piano::Mode::Play ? "play" : playback.mode == piano::Mode::Teach ? "teach" : playback.mode == piano::Mode::Simple ? "simple" : playback.mode == piano::Mode::Rhythm ? "rhythm" : playback.mode == piano::Mode::FreeScore ? "free" : "manual";
  body += "\",\"noteIndex\":"; body += String(static_cast<unsigned>(playback.index));
  body += ",\"currentMidi\":"; body += String(playback.midi);
  body += ",\"expectedMidi\":"; body += String(playback.expected);
  body += ",\"noteCount\":"; body += String(static_cast<unsigned>(playback.count));
  body += ",\"keyMask\":"; body += String(runtime.keyMask());
  body += ",\"paused\":"; body += playback.paused ? "true" : "false";
  body += ",\"phoneAudioReady\":"; body += runtime.phoneOutputReady() ? "true" : "false";
  body += ",\"phoneOutput\":"; body += runtime.usesPhoneOutput() ? "true" : "false";
  body += ",\"finished\":"; body += playback.finished ? "true" : "false";
  body += ",\"bpm\":"; body += String(playback.settings.bpm);
  body += ",\"instrument\":"; body += String(playback.settings.instrument);
  body += ",\"sampleStatus\":\""; body += jsonEscape(runtime.sampleStatus()); body += "\"";
  body += ",\"transpose\":"; body += String(playback.settings.transpose);
  body += ",\"metronome\":"; body += playback.settings.metronome ? "true" : "false";
  body += ",\"repeat\":"; body += playback.settings.repeat ? "true" : "false";
  body += ",\"showLyrics\":"; body += playback.settings.showLyrics ? "true" : "false";
  body += ",\"songId\":\""; body += jsonEscape(runtime.songId()); body += "\"";
  body += ",\"freeHeap\":"; body += String(ESP.getFreeHeap());
  body += ",\"psramBytes\":"; body += String(ESP.getPsramSize());
  body += ",\"uptimeMs\":"; body += String(millis());
  body += ",\"flashMounted\":"; body += flashMounted ? "true" : "false";
  body += ",\"sdMounted\":"; body += runtime.sdMounted() ? "true" : "false";
  body += ",\"storage\":\""; body += jsonEscape(storageLabel());
  body += "\",\"message\":\"";
  body += jsonEscape(lastMessage);
  body += "\"}";
  sendJson(200, body);
}


void appendSongSummary(String& body, const piano::Song& song, bool& first) {
  if (!first) body += ','; first = false;
  body += "{\"id\":\"" + jsonEscape(song.id.c_str()) + "\",\"title\":\"" + jsonEscape(song.title.c_str());
  body += "\",\"bpm\":" + (song.bpmProvided ? String(song.bpm) : String("null")) + ",\"noteCount\":" + String(static_cast<unsigned>(song.notes.size())) + "}";
}
bool appendStore(fs::FS& fs, String& body, bool& first, std::vector<std::string>& seen) {
  File directory = fs.open(SONG_DIRECTORY, FILE_READ);
  if (!directory) return true;
  for (File f = directory.openNextFile(); f; f = directory.openNextFile()) {
    const String path = f.path(); const bool directoryEntry = f.isDirectory(); f.close();
    if (directoryEntry || !path.endsWith(".json")) continue;
    String text; piano::Song song; std::string error;
    if (!readStored(fs, path, text) || !piano::parseSong(text.c_str(), text.length(), song, error) || path != songPath(song.id.c_str())) continue;
    if (std::find(seen.begin(), seen.end(), song.id) != seen.end()) continue;
    seen.push_back(song.id); appendSongSummary(body, song, first);
    if (body.length() > 64 * 1024) { directory.close(); return false; }
  }
  directory.close(); return true;
}
void handleSongs() {
  String body = "{\"ok\":true,\"songs\":["; bool first = true; std::vector<std::string> seen;
  piano::Song demo; piano::builtinSong("demo-scale", demo); appendSongSummary(body, demo, first); seen.push_back(demo.id);
  bool ok = true;
  if (runtime.sdMounted()) ok = appendStore(SD, body, first, seen);
  if (ok && flashMounted) ok = appendStore(LittleFS, body, first, seen);
  if (!ok) { sendResponse(false, "song list exceeds 64 KiB; manage storage before retry"); return; }
  body += "],\"message\":\"ok\"}"; sendJson(200, body);
}


bool requestJsonBody(String& body) {
  if (bodyTooLarge) { sendResponse(false, "request exceeds 120 KiB"); return false; }
  if (incomingBody.isEmpty()) {
    sendResponse(false, "request body is required");
    return false;
  }
  body = incomingBody;
  if (body.length() == 0) {
    sendResponse(false, "request body is empty");
    return false;
  }
  if (body.length() > MAX_UPLOAD_BYTES) {
    sendResponse(false, "song payload is too large");
    return false;
  }
  return true;
}


void handleUpload() {
  String body; if (!requestJsonBody(body)) return;
  piano::Song song; std::string parseError;
  if (!piano::parseSong(body.c_str(), body.length(), song, parseError)) { sendResponse(false, parseError.c_str()); return; }
  if (song.id == "demo-scale") { sendResponse(false, "built-in song id is reserved"); return; }
  String error;
  if (!writeSong(song, body, error)) { sendResponse(false, error); return; }
  localSongsDirty=true;
  lastMessage = "song uploaded: " + String(song.title.c_str());
  sendResponse(true, "song uploaded", song.id.c_str());
}



String requestSongId() {
  if (bodyTooLarge) return "";
  const String& body = incomingBody; std::string id;
  return piano::readSongId(body.c_str(), body.length(), id) ? String(id.c_str()) : String("");
}
void remoteEdit(piano::MenuItem field);
void startSong(piano::Mode mode) {
  const String id = requestSongId(); piano::Song song; std::string error;
  if (!loadSong(id, song, error)) { sendResponse(false, error.c_str()); return; }
  if (!runtime.start(std::move(song), mode)) { sendResponse(false, "audio or key control initialization failed"); return; }
  remoteEdit(piano::MenuItem::Tempo);
  activeSongId = id; lastMessage = mode == piano::Mode::Play ? "playback started" : "teaching started";
  sendResponse(true, lastMessage, id);
}
void handlePlay() { startSong(piano::Mode::Play); }
void handleTeach() { startSong(piano::Mode::Teach); }
void handleStop() {
  runtime.stop(); activeSongId = ""; lastMessage = "playback stopped"; sendResponse(true, lastMessage);
}



void remoteEdit(piano::MenuItem field) {
  if (!menuEdit.active || menuEdit.item != field) return;
  menuEdit.remote(field); menuController.state.editing = false;
  menuController.state.notice = lcd::kRemoteChanged; menuNoticeAt = millis();
}

void handleVolume() {
  String body; if (!requestJsonBody(body)) return;
  int volume = 0;
  if (!piano::readVolume(body.c_str(), body.length(), volume)) { sendResponse(false, "volumePercent must be an integer from 0 to 100"); return; }
  remoteEdit(piano::MenuItem::Volume);
  outputVolumePercent = volume; runtime.setVolume(volume);
  if (!preferencesReady || preferences.putUChar("volume", static_cast<uint8_t>(volume)) != 1) {
    sendResponse(false, "volume applied, but persistence failed"); return;
  }
  lastMessage = "volume set to " + String(volume) + "%"; sendResponse(true, lastMessage);
}

bool persistSettings(const piano::Settings& s) {
  if (!preferencesReady) return false;
  bool ok = preferences.putUChar("instrument", s.instrument) == 1;
  ok = (preferences.putUShort("bpm", s.bpm) == 2) && ok;
  ok = (preferences.putChar("transpose", s.transpose) == 1) && ok;
  ok = (preferences.putBool("metronome", s.metronome) == 1) && ok;
  ok = (preferences.putBool("showLyrics", s.showLyrics) == 1) && ok;
  return (preferences.putBool("repeat", s.repeat) == 1) && ok;
}
void handleSettings() {
  String body; if (!requestJsonBody(body)) return;
  auto settings = runtime.view().settings;
  if (!piano::readSettings(body.c_str(), body.length(), settings)) { sendResponse(false, "invalid settings: instrument 0..10, bpm 20..400, transpose -12..12, metronome/repeat/showLyrics boolean"); return; }
  cJSON* changes = cJSON_Parse(body.c_str());
  if (changes) {
    if (cJSON_HasObjectItem(changes,"instrument")) remoteEdit(piano::MenuItem::Instrument);
    if (cJSON_HasObjectItem(changes,"bpm")) remoteEdit(piano::MenuItem::Tempo);
    if (cJSON_HasObjectItem(changes,"transpose")) remoteEdit(piano::MenuItem::Transpose);
    cJSON_Delete(changes);
  }
  if(settings.showLyrics && runtime.view().mode==piano::Mode::Manual) {
    piano::Song song; std::string error;
    if(loadSong(selectedSongId.c_str(),song,error)) runtime.start(std::move(song),piano::Mode::FreeScore);
  }
  runtime.settings(settings);
  sendResponse(persistSettings(settings), "settings applied (persistence requires available NVS)");
}
void handleSongDownload(const String& id) {
  if (id == "demo-scale") {
    piano::Song s; std::string error;
    if (!loadSong(id, s, error)) { sendResponse(false, error.c_str()); return; }
    sendJson(200, piano::encodeSong(s).c_str()); return;
  }
  fs::FS* fs = isSafeSongId(id) ? findStore(id) : nullptr;
  String text;
  if (!fs || !readStored(*fs, songPath(id), text)) { sendResponse(false, "song not found"); return; }
  sendJson(200, text);
}
void handleFormatFlash() {
  if (flashMounted) { sendResponse(false, "flash already mounted; format disabled to protect songs"); return; }
  if (incomingBody != "{\"confirm\":\"FORMAT\"}") { sendResponse(false, "explicit FORMAT confirmation required"); return; }
  flashMounted = LittleFS.format() && LittleFS.begin(false);
  if (flashMounted) recoverBackups(LittleFS);
  localSongsDirty=true;
  sendResponse(flashMounted, flashMounted ? "flash song storage initialized" : "flash initialization failed");
}

void refreshLocalSongs() {
  if(!localSongsDirty) return;
  menuSongs.clear();piano::MenuSongSummary demo;demo.id="demo-scale";menuSongs.push_back(demo);
  auto collect = [](fs::FS& fs) {
    File dir = fs.open(SONG_DIRECTORY, FILE_READ); if (!dir) return;
    for (File f = dir.openNextFile(); f; f = dir.openNextFile()) {
      const String path = f.path(); const bool isDir = f.isDirectory(); f.close();
      if (isDir || !path.endsWith(".json")) continue;
      const std::string id = path.substring(String(SONG_DIRECTORY).length()+1,path.length()-5).c_str();
      if (piano::safeId(id) && menuSongs.size()<512 && std::none_of(menuSongs.begin(),menuSongs.end(),[&](const piano::MenuSongSummary& s){return s.id==id;})) {
        piano::MenuSongSummary summary;summary.id=id;menuSongs.push_back(std::move(summary));
      }
    }
    dir.close();
  };
  if (runtime.sdMounted()) collect(SD);
  if (flashMounted) collect(LittleFS);
  // Read each full song once, not on each list key press. Never retain notes here.
  for(auto& summary:menuSongs) {
    piano::Song song;std::string error;
    if(loadSong(summary.id.c_str(),song,error)) { summary.title=song.title;summary.bpm=song.bpm;summary.bpmProvided=song.bpmProvided; }
    else { summary.title="曲谱读取失败";summary.bpm=120;summary.bpmProvided=false; }
  }
  const auto found=std::find_if(menuSongs.begin(),menuSongs.end(),[](const piano::MenuSongSummary& s){return s.id==selectedSongId;});
  menuIndex=found==menuSongs.end()?0:static_cast<size_t>(found-menuSongs.begin());
  selectedSongId=menuSongs[menuIndex].id;
  browseIndex=menuIndex;localSongsDirty=false;
}
void updateMenuSong(bool browsing=false) {
  piano::showMenuSong(menuController.state,menuSongs,browsing?browseIndex:menuIndex,browsing);
}
void menuNotice(const char* text) { menuController.state.notice=text; menuNoticeAt=millis(); }
bool persistVolume() { return preferencesReady && preferences.putUChar("volume",static_cast<uint8_t>(outputVolumePercent))==1; }
void applyMenuValue(piano::MenuItem item,int value) {
  auto settings=runtime.view().settings;
  using I=piano::MenuItem;
  if(item==I::Volume) { outputVolumePercent=std::max(0,std::min(100,value)); runtime.setVolume(outputVolumePercent); return; }
  if(item==I::Instrument) settings.instrument=std::max(0,std::min(10,value));
  if(item==I::Tempo) settings.bpm=std::max(20,std::min(400,value));
  if(item==I::Transpose) settings.transpose=std::max(-12,std::min(12,value));
  runtime.settings(settings);
}
void finishEdit(bool cancel) {
  if(!menuEdit.active) return;
  if(menuEdit.item==piano::MenuItem::Output) {
    if(!cancel && !runtime.selectPhoneOutput(menuEdit.pendingOutput==1)) menuNotice("请先在手机网页启用发声");
    menuEdit.finish(); return;
  }
  if(menuEdit.item==piano::MenuItem::Instrument) {
    if(cancel) { menuEdit.finish(); return; }
    applyMenuValue(menuEdit.item,menuEdit.pendingInstrument);
  } else if(cancel) applyMenuValue(menuEdit.item,menuEdit.original);
  const bool ok=menuEdit.item==piano::MenuItem::Volume?persistVolume():persistSettings(runtime.view().settings);
  menuEdit.finish();
  if(!ok) menuNotice(lcd::kStoredFailed);
}
void appendInfo(std::vector<std::string>& lines,const String& value) {
  std::string line; int width=0;
  for(size_t i=0;i<value.length();) {
    const unsigned char c=value[i]; const size_t bytes=c<128?1:(c&0xe0)==0xc0?2:(c&0xf0)==0xe0?3:4;
    const int size=c<128?8:16;
    if(width+size>296) { lines.push_back(line); line.clear(); width=0; }
    const size_t count=std::min(bytes,value.length()-i); line.append(value.c_str()+i,count); i+=count; width+=size;
  }
  lines.push_back(line);
}
void updateMenuInfo() {
  auto& s=menuController.state;
  s.previewInstrument=menuEdit.active && menuEdit.item==piano::MenuItem::Instrument ? menuEdit.pendingInstrument : -1;
  s.previewOutput=menuEdit.active && menuEdit.item==piano::MenuItem::Output ? menuEdit.pendingOutput : -1;
  s.phoneReady=runtime.phoneOutputReady(); s.phoneOutput=runtime.usesPhoneOutput();
  if(s.page==piano::MenuPage::Diagnostics) s.keyMask=runtime.keyMask();
  static uint32_t last=0;
  if(millis()-last<250) { runtime.menuUi(s); return; } last=millis();
  if(s.page==piano::MenuPage::Connection) {
    s.networkLines.clear();
    appendInfo(s.networkLines,apReady?lcd::kApReady:lcd::kApFailed);
    appendInfo(s.networkLines,lcd::kApName); appendInfo(s.networkLines,HP_AP_SSID);
    appendInfo(s.networkLines,lcd::kApPassword); appendInfo(s.networkLines,strlen(HP_AP_PASSWORD)?HP_AP_PASSWORD:lcd::kOpenAp);
    appendInfo(s.networkLines,String(lcd::kWifiCount)+String(WiFi.softAPgetStationNum()));
    appendInfo(s.networkLines,"手机扬声器：浏览器打开");
    appendInfo(s.networkLines,String("http://")+WiFi.softAPIP().toString()+"/");
    appendInfo(s.networkLines,"点启用手机扬声器，保持前台");
    appendInfo(s.networkLines,"1 手机Wi-Fi连接上述热点");
    appendInfo(s.networkLines,"提示无互联网时保持连接");
    appendInfo(s.networkLines,"2 打开App的设备页面");
    appendInfo(s.networkLines,"关闭Mock模式，填设备地址");
    appendInfo(s.networkLines,"点击连接测试");
    appendInfo(s.networkLines,"3 导入页选择TXT或JSON");
    appendInfo(s.networkLines,"预览后点发送到设备");
    appendInfo(s.networkLines,"4 设备页点击刷新歌曲");
    appendInfo(s.networkLines,"或浏览器打开以下地址");
    appendInfo(s.networkLines,"网页选择文件，上传歌曲");
    appendInfo(s.networkLines,String("http://")+WiFi.softAPIP().toString()+"/");
    if(WiFi.status()==WL_CONNECTED) { appendInfo(s.networkLines,lcd::kStaAddress); appendInfo(s.networkLines,String("http://")+WiFi.localIP().toString()+"/"); }
    s.infoOffset=std::min(s.infoOffset,std::max(0,static_cast<int>(s.networkLines.size())-7));
  }
  if(s.page==piano::MenuPage::Device) {
    s.deviceLines={std::string(lcd::kFirmware)+board::kVersion,
      std::string(lcd::kStorage)+lcd::kStorageNames[(runtime.sdMounted()?2:0)+(flashMounted?1:0)],
      runtime.audioReady()?lcd::kAudioReady:lcd::kAudioFault,runtime.controlReady()?lcd::kControlsOk:lcd::kControlsFailed,
      runtime.sampleStatus()=="samples ready" ? "采样音源就绪" : runtime.sampleStatus()=="synth" ? "当前为合成音色" : "音源未载入，请检查SD卡"};
  }
  s.playingTitle=runtime.view().mode==piano::Mode::Manual?"":runtime.songTitle().c_str();
  runtime.menuUi(s);
}
bool deleteStoredSong(const String& id);
void serviceLocal() {
  if(!menuController.state.notice.empty() && static_cast<uint32_t>(millis()-menuNoticeAt)>=4000) menuController.state.notice.clear();
  const int action=runtime.takeLocalAction();
  if(action<0) { updateMenuInfo(); return; }
  if(action==7 || localSongsDirty) { refreshLocalSongs(); updateMenuSong(action!=7 && menuController.state.page==piano::MenuPage::SongList); }
  const auto command=menuController.input(action);
  using C=piano::MenuCommand; using I=piano::MenuItem;
  if(command==C::BeginEdit) {
    menuEdit.begin(piano::menuItem(menuController.state),runtime.view());
    if(menuEdit.item==I::Output) menuEdit.pendingOutput=runtime.usesPhoneOutput()?1:0;
  }
  if(command==C::CommitEdit || command==C::CancelEdit) finishEdit(command==C::CancelEdit);
  if(command==C::LoadSongs) { refreshLocalSongs(); browseIndex=menuIndex; updateMenuSong(true); }
  if(command==C::SongPrevious || command==C::SongNext) {
    browseIndex=static_cast<size_t>(piano::cycleIndex(static_cast<int>(browseIndex),command==C::SongPrevious?-1:1,static_cast<int>(menuSongs.size())));
    updateMenuSong(true);
  }
  if(command==C::Delete) {
    const String id=menuSongs[browseIndex].id.c_str();
    if(id=="demo-scale") menuNotice("内置音阶不能删除");
    else if(deleteStoredSong(id)) {
      menuNotice("歌曲已删除"); refreshLocalSongs(); browseIndex=std::min(browseIndex,menuSongs.size()-1);
    } else menuNotice("删除未完成，请重试");
    updateMenuSong(true);
  }
  if(command==C::SongSelect) { menuIndex=browseIndex; selectedSongId=menuSongs[menuIndex].id; updateMenuSong(); }
  if(command==C::SongCancel) updateMenuSong();
  if(command==C::PlayPause || command==C::Teach || command==C::FreeScore || command==C::Simple || command==C::RhythmPractice) {
    const auto v=runtime.view(); bool ok=false;
    const String id=selectedSongId.c_str();
    if(command==C::PlayPause && v.mode==piano::Mode::Play && runtime.songId()==id) ok=runtime.pause(!v.paused);
    else {
      piano::Song song; std::string error;
      if(loadSong(id,song,error)) { ok=runtime.start(std::move(song),command==C::Simple?piano::Mode::Simple:command==C::RhythmPractice?piano::Mode::Rhythm:command==C::FreeScore?piano::Mode::FreeScore:command==C::Teach?piano::Mode::Teach:piano::Mode::Play); if(ok) activeSongId=id; }
      else menuNotice(lcd::kSongReadFailed);
      if(!ok && menuController.state.notice.empty()) menuNotice(lcd::kAudioNotReady);
    }
    if(ok) runtime.closeMenu();
  }
  if(command==C::VolumeDown || command==C::VolumeUp) {
    outputVolumePercent=std::max(0,std::min(100,outputVolumePercent+(command==C::VolumeDown?-5:5)));
    runtime.setVolume(outputVolumePercent); if(!persistVolume()) menuNotice(lcd::kStoredFailed);
  }
  if(menuEdit.active && (command==C::Decrease || command==C::Increase || command==C::DecreaseTen || command==C::IncreaseTen)) {
    const int step=(command==C::DecreaseTen || command==C::IncreaseTen)?10:menuEdit.item==I::Volume?5:1;
    const int delta=(command==C::Decrease || command==C::DecreaseTen)?-step:step;
    if(menuEdit.item==I::Instrument) menuEdit.browseInstrument(delta);
    else if(menuEdit.item==I::Output) menuEdit.pendingOutput=piano::cycleIndex(menuEdit.pendingOutput,delta,2);
    else applyMenuValue(menuEdit.item,menuEdit.value(runtime.view())+delta);
  }
  if(command==C::Lyrics && !runtime.view().settings.showLyrics) {
    piano::Song selected; std::string error;
    if(loadSong(selectedSongId.c_str(),selected,error)) {
      const bool has=std::any_of(selected.notes.begin(),selected.notes.end(),[](const piano::Note& n){return !n.lyric.empty();});
      if(runtime.view().mode==piano::Mode::Manual) runtime.start(selected,piano::Mode::FreeScore);
      if(!has) menuNotice("此曲无歌词，请上传含词曲谱");
    } else menuNotice(lcd::kSongReadFailed);
  }
  if(command==C::Metronome || command==C::Repeat || command==C::Lyrics) {
    auto settings=runtime.view().settings;
    if(command==C::Metronome) settings.metronome=!settings.metronome;
    else if(command==C::Lyrics) settings.showLyrics=!settings.showLyrics;
    else settings.repeat=!settings.repeat;
    runtime.settings(settings); if(!persistSettings(settings)) menuNotice(lcd::kStoredFailed);
  }
  if(command==C::Manual || command==C::Stop) { runtime.stop(); activeSongId=""; runtime.closeMenu(); }
  if(command==C::Exit) runtime.closeMenu();
  updateMenuInfo();
}

String decodePathComponent(const String& value) {
  String result;
  result.reserve(value.length());
  for (int i = 0; i < static_cast<int>(value.length()); ++i) {
    if (value.charAt(i) == '%' && i + 2 < static_cast<int>(value.length())) {
      const char high = value.charAt(i + 1);
      const char low = value.charAt(i + 2);
      auto hexValue = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
      };
      const int highValue = hexValue(high);
      const int lowValue = hexValue(low);
      if (highValue >= 0 && lowValue >= 0) {
        result += static_cast<char>((highValue << 4) | lowValue);
        i += 2;
        continue;
      }
    }
    result += value.charAt(i);
  }
  return result;
}


bool deleteStoredSong(const String& id) {
  if(id=="demo-scale" || !isSafeSongId(id) || !findStore(id)) return false;
  localSongsDirty=true;
  bool ok=true; const String path=songPath(id);
  if(runtime.sdMounted()) { StoreIO store{SD}; ok=piano::removeSong(store,path); }
  if(flashMounted) { StoreIO store{LittleFS}; ok=piano::removeSong(store,path) && ok; }
  if(runtime.songId()==id) { runtime.stop(); activeSongId=""; }
  return ok;
}
void handleDeleteSong(const String& id) {
  if(id=="demo-scale") { sendResponse(false,"built-in demo cannot be deleted"); return; }
  if(!deleteStoredSong(id)) { sendResponse(false,"song deletion incomplete or not found; refresh before retry"); return; }
  lastMessage="song deleted"; sendResponse(true,lastMessage,id);
}


void handleNotFound() {
  if (server.method() == HTTP_OPTIONS) {
    sendJson(204, "");
    return;
  }
  const String uri = server.uri();
  const String prefix = "/api/songs/";
  if (server.method() == HTTP_DELETE && uri.startsWith(prefix)) {
    handleDeleteSong(decodePathComponent(uri.substring(prefix.length())));
    return;
  }
  if (server.method() == HTTP_GET && uri.startsWith(prefix)) { handleSongDownload(decodePathComponent(uri.substring(prefix.length()))); return; }
  sendResponse(false, "endpoint not found");
}

void makeDeviceId() {
  const uint64_t chip = ESP.getEfuseMac();
  char buffer[24];
  snprintf(buffer, sizeof(buffer), "esp32-s3-%04lx%08lx",
           static_cast<unsigned long>(chip >> 32),
           static_cast<unsigned long>(chip & 0xFFFFFFFFULL));
  deviceId = buffer;
}

void startWifi() {
  WiFi.persistent(false);
  WiFi.mode(WIFI_AP_STA);
  WiFi.setSleep(false);

  IPAddress apIp(192, 168, 4, 1);
  IPAddress apGateway(192, 168, 4, 1);
  IPAddress apMask(255, 255, 255, 0);
  const bool configured = WiFi.softAPConfig(apIp, apGateway, apMask);
  apReady = WiFi.softAP(HP_AP_SSID, HP_AP_PASSWORD) && configured;

  if (strlen(HP_WIFI_SSID) > 0) {
    Serial.printf("Connecting STA to %s...\n", HP_WIFI_SSID);
    WiFi.begin(HP_WIFI_SSID, HP_WIFI_PASSWORD);
    const unsigned long deadline = millis() + 8000;
    while (WiFi.status() != WL_CONNECTED && millis() < deadline) {
      delay(200);
      Serial.print('.');
    }
    Serial.println();
  }

  Serial.printf("AP: %s / %s\n", HP_AP_SSID, WiFi.softAPIP().toString().c_str());
  if (WiFi.status() == WL_CONNECTED) {
    Serial.printf("STA: %s\n", WiFi.localIP().toString().c_str());
  } else if (strlen(HP_WIFI_SSID) > 0) {
    Serial.println("STA not connected; AP remains available.");
  }
}

void serviceWifi() {
  if (strlen(HP_WIFI_SSID) == 0 || WiFi.status() == WL_CONNECTED) return;
  if (millis() - lastStaAttempt < 10000) return;
  lastStaAttempt = millis();
  WiFi.begin(HP_WIFI_SSID, HP_WIFI_PASSWORD);
}

// Core 2.0.17 raw callbacks bound the body before WebServer can allocate an
// arbitrary Content-Length buffer. No changes to the installed SDK required.
void receiveBody() {
  auto& raw = server.raw();
  if (raw.status == RAW_START) {
    incomingBody = "";
    const auto size = server.clientContentLength();
    bodyTooLarge = size < 0 || static_cast<size_t>(size) > MAX_UPLOAD_BYTES;
    if (!bodyTooLarge && size > 0 && !incomingBody.reserve(size + 1)) bodyTooLarge = true;
  }
  if (raw.status == RAW_WRITE) {
    if (incomingBody.length() + raw.currentSize > MAX_UPLOAD_BYTES) bodyTooLarge = true;
    if (!bodyTooLarge) {
      // Pinned Core 2.0.17 String::concat copies length+1; HTTPRaw.buf
      // is not NUL terminated. Own the extra terminator for JSON and PCM.
      char terminated[HTTP_RAW_BUFLEN+1];
      std::memcpy(terminated,raw.buf,raw.currentSize);terminated[raw.currentSize]=0;
      if (!incomingBody.concat(terminated,raw.currentSize)) bodyTooLarge=true;
    }
  }
  if (bodyTooLarge) {
    sendJson(413, "{\"ok\":false,\"message\":\"request too large or out of memory\"}");
    server.client().stop();
  }
  if (raw.status == RAW_ABORTED) incomingBody = "";
}
class BoundedPostHandler : public RequestHandler {
 public:
  BoundedPostHandler(const char* path, WebServer::THandlerFunction fn) : path_(path), fn_(fn) {}
  bool canHandle(HTTPMethod method, String uri) override { return method == HTTP_POST && (path_ == "*" || uri == path_); }
  bool canRaw(String uri) override { return path_ == "*" || uri == path_; }
  void raw(WebServer&, String, HTTPRaw&) override { receiveBody(); }
  bool handle(WebServer&, HTTPMethod method, String uri) override {
    if (!canHandle(method, uri)) return false;
    // Multipart is not the JSON API and must not reuse a previous raw body.
    if (bodyTooLarge) sendResponse(false, "request too large or out of memory");
    else if (server.header("Content-Type").startsWith("multipart/")) sendResponse(false, "send a JSON request, not multipart form data");
    else fn_();
    incomingBody = String(); return true;
  }
 private:
  String path_;
  WebServer::THandlerFunction fn_;
};
void post(const char* path, WebServer::THandlerFunction handler) { server.addHandler(new BoundedPostHandler(path, handler)); }
bool sampleToken(uint32_t& token, const String& text) {
  if(!text.length() || text.length()>10) return false;
  for(unsigned i=0;i<text.length();i++) if(text[i]<'0'||text[i]>'9') return false;
  const uint64_t number=strtoull(text.c_str(),nullptr,10);
  if(!number || number>UINT32_MAX) return false;
  token=number;return true;
}
void beginSampleUpload() {
  String body; if(!requestJsonBody(body)) return;
  if(sampleUpload.expired(millis())) cancelSampleUpload();
  if(sampleUpload.active) { sendJson(409,"{\"ok\":false,\"message\":\"another sample upload is active\"}");return; }
  cJSON* json=cJSON_Parse(body.c_str());
  const cJSON* id=cJSON_GetObjectItemCaseSensitive(json,"instrument");
  const cJSON* size=cJSON_GetObjectItemCaseSensitive(json,"bytes");
  const cJSON* checksum=cJSON_GetObjectItemCaseSensitive(json,"crc32");
  const char* crcText=cJSON_IsString(checksum)?checksum->valuestring:"";
  bool valid=cJSON_IsNumber(id) && id->valuedouble>=3 && id->valuedouble<=10 && id->valuedouble==int(id->valuedouble) &&
    cJSON_IsNumber(size) && size->valuedouble==piano::SampleBank::kBytes && std::strlen(crcText)==8;
  for(size_t i=0;valid && i<8;i++) if(!std::isxdigit(static_cast<unsigned char>(crcText[i]))) valid=false;
  if(!valid) { cJSON_Delete(json);sendJson(400,"{\"ok\":false,\"message\":\"invalid HPS upload metadata\"}");return; }
  const int instrument=id->valuedouble;const uint32_t crc=strtoul(crcText,nullptr,16);cJSON_Delete(json);
  if(!runtime.sdMounted()) { sendJson(503,"{\"ok\":false,\"message\":\"SD card is not mounted\"}");return; }
  if(SD.totalBytes()-SD.usedBytes()<piano::SampleBank::kBytes+65536) { sendJson(507,"{\"ok\":false,\"message\":\"not enough free SD space\"}");return; }
  if(!SD.exists("/piano-samples") && !SD.mkdir("/piano-samples")) {sendResponse(false,"cannot create sample directory");return;}
  sampleUploadPath="/piano-samples/"+String(instrument)+".hps";
  if(!piano::recoverSample(SD,sampleUploadPath)) {sendResponse(false,"cannot recover previous sample backup");return;}
  const String temp=sampleUploadPath+".tmp";
  if(SD.exists(temp) && !SD.remove(temp)) {sendResponse(false,"cannot clear unfinished sample upload");return;}
  sampleUploadFile=SD.open(temp,FILE_WRITE);
  if(!sampleUploadFile) {sendResponse(false,"cannot create temporary sample file");return;}
  uint32_t token=esp_random();if(!token) token=1;
  sampleUpload.begin(instrument,piano::SampleBank::kBytes,token,crc,millis());
  sendJson(200,"{\"ok\":true,\"session\":"+String(token)+",\"offset\":0}");
}
void appendSampleUpload() {
  // Core 2.0.17 skips query argument parsing for canRaw() POST requests.
  // Collected headers remain available after the binary body callback.
  uint32_t token=0;const String offsetText=server.header("X-Sample-Offset");
  bool offsetValid=offsetText.length()>0 && offsetText.length()<=7;
  for(unsigned i=0;i<offsetText.length();i++) if(offsetText[i]<'0'||offsetText[i]>'9') offsetValid=false;
  if(!sampleToken(token,server.header("X-Sample-Session")) || !sampleUpload.owns(token) || !offsetValid || bodyTooLarge ||
      !server.header("Content-Type").startsWith("application/octet-stream") || incomingBody.length()>65536) {
    sendJson(400,"{\"ok\":false,\"message\":\"invalid sample chunk or session\"}");return;
  }
  const size_t offset=strtoul(offsetText.c_str(),nullptr,10);
  if(!sampleUpload.append(token,offset,reinterpret_cast<const uint8_t*>(incomingBody.c_str()),incomingBody.length(),millis())) {
    sendJson(409,"{\"ok\":false,\"message\":\"sample offset or chunk size mismatch\"}");return;
  }
  if(sampleUpload.received>=32 && !piano::SampleBank::validHeader(sampleUpload.header,sampleUpload.instrument)) {
    cancelSampleUpload();sendJson(400,"{\"ok\":false,\"message\":\"invalid HPS sample header\"}");return;
  }
  if(sampleUploadFile.write(reinterpret_cast<const uint8_t*>(incomingBody.c_str()),incomingBody.length())!=incomingBody.length()) {
    cancelSampleUpload();sendResponse(false,"SD sample write failed; previous sample retained");return;
  }
  sendJson(200,"{\"ok\":true,\"offset\":"+String(sampleUpload.received)+"}");
}
void finishSampleUpload(bool cancel) {
  if(sampleUpload.expired(millis())) cancelSampleUpload();
  String body;if(!requestJsonBody(body)) return;
  cJSON* json=cJSON_Parse(body.c_str());const cJSON* session=cJSON_GetObjectItemCaseSensitive(json,"session");
  const bool own=cJSON_IsNumber(session) && session->valuedouble>=1 && session->valuedouble<=UINT32_MAX &&
    session->valuedouble==uint32_t(session->valuedouble) && sampleUpload.owns(uint32_t(session->valuedouble));
  cJSON_Delete(json);
  if(!own) {sendJson(409,"{\"ok\":false,\"message\":\"sample session expired or unavailable\"}");return;}
  if(cancel) {cancelSampleUpload();sendResponse(true,"sample upload cancelled; previous sample retained");return;}
  if(!sampleUpload.valid()) {cancelSampleUpload();sendResponse(false,"sample incomplete or checksum mismatch; previous sample retained");return;}
  sampleUploadFile.flush();sampleUploadFile.close();
  File check=SD.open(sampleUploadPath+".tmp",FILE_READ);
  bool valid=check && check.size()==piano::SampleBank::kBytes;
  uint8_t buffer[1024];uint32_t crc=0xffffffffu;size_t received=0;
  while(valid && check.available()) {
    const size_t count=check.read(buffer,sizeof(buffer));if(!count) {valid=false;break;}
    crc=piano::sampleCrc(crc,buffer,count);received+=count;
    if(received%16384==0) delay(1);
  }
  if(check) check.close();
  valid=valid && received==piano::SampleBank::kBytes && (crc^0xffffffffu)==sampleUpload.expectedCrc;
  if(!valid || !piano::commitSample(SD,sampleUploadPath)) {
    cancelSampleUpload();sendResponse(false,"SD readback or commit failed; previous sample/backup retained");return;
  }
  const int instrument=sampleUpload.instrument;sampleUpload.active=false;
  if(runtime.view().settings.instrument==instrument) runtime.reloadSamples();
  sendResponse(true,"sample saved and SD checksum verified");
}
void startHttp() {
  const char* headers[] = {"Content-Type", "X-Sample-Session", "X-Sample-Offset"}; server.collectHeaders(headers, 3);
  server.on("/", HTTP_GET, [] { server.send_P(200, "text/html; charset=utf-8", kWebUi); });
  server.on("/api/status", HTTP_GET, handleStatus);
  server.on("/api/songs", HTTP_GET, handleSongs);
  post("/api/samples/begin", beginSampleUpload);
  post("/api/samples/chunk", appendSampleUpload);
  post("/api/samples/finish", [] {finishSampleUpload(false);});
  post("/api/samples/cancel", [] {finishSampleUpload(true);});
  post("/api/songs/upload", handleUpload);
  post("/api/song/play", handlePlay);
  post("/api/song/teach", handleTeach);
  post("/api/audio/output", [] {
    String body; if (!requestJsonBody(body)) return;
    cJSON* json = cJSON_Parse(body.c_str());
    const cJSON* session = cJSON_GetObjectItemCaseSensitive(json, "session");
    const cJSON* enabled = cJSON_GetObjectItemCaseSensitive(json, "enabled");
    if (!cJSON_IsNumber(session) || session->valuedouble < 1 || session->valuedouble > UINT32_MAX ||
        session->valuedouble != static_cast<uint32_t>(session->valuedouble) || !cJSON_IsBool(enabled)) {
      cJSON_Delete(json); sendResponse(false, "invalid phone output session"); return;
    }
    const bool ok = runtime.phoneOutput(static_cast<uint32_t>(session->valuedouble), cJSON_IsTrue(enabled));
    if(ok) remoteEdit(piano::MenuItem::Output);
    cJSON_Delete(json); sendResponse(ok, ok ? "audio output switched" : "phone output session unavailable");
  });
  server.on("/api/audio/events",HTTP_GET,[] {
    const String value=server.arg("after");
    bool valid=value.length()<=10;
    for(unsigned i=0;i<value.length();++i) if(value[i]<'0'||value[i]>'9') valid=false;
    if(!valid || strtoull(value.c_str(),nullptr,10)>UINT32_MAX) { sendResponse(false,"invalid audio cursor"); return; }
    if (server.hasArg("session")) {
      const String session = server.arg("session");
      bool sessionValid = session.length() > 0 && session.length() <= 10;
      for (unsigned i=0; i<session.length(); ++i) if (session[i]<'0' || session[i]>'9') sessionValid=false;
      if (!sessionValid || strtoull(session.c_str(),nullptr,10)>UINT32_MAX ||
          !runtime.renewPhoneOutput(static_cast<uint32_t>(strtoul(session.c_str(),nullptr,10)))) {
        sendResponse(false,"phone output session expired"); return;
      }
    }
    sendJson(200,runtime.audioEvents(static_cast<uint32_t>(strtoul(value.c_str(),nullptr,10))));
  });
  post("/api/song/stop", handleStop);
  post("/api/device/volume", handleVolume);
  post("/api/device/settings", handleSettings);
  post("/api/song/pause", [] { sendResponse(runtime.pause(true), "pause requested"); });
  post("/api/song/resume", [] { sendResponse(runtime.pause(false), "resume requested"); });
  post("/api/storage/initialize", handleFormatFlash);
  post("*", handleNotFound);
  server.onNotFound(handleNotFound);
  server.begin();
  Serial.printf("HTTP server listening on %u\n", HTTP_PORT);
}

} // namespace


void applicationSetup() {
  Serial.begin(115200); delay(200); makeDeviceId();
  cJSON_Hooks hooks = {jsonAllocate, std::free}; cJSON_InitHooks(&hooks);
  preferencesReady = preferences.begin("harmonica", false);
  outputVolumePercent = preferencesReady ? std::min(100, static_cast<int>(preferences.getUChar("volume", 80))) : 80;
  runtime.begin(outputVolumePercent);
  piano::Settings settings;
  if (preferencesReady) {
    settings.instrument = std::min(10, static_cast<int>(preferences.getUChar("instrument", 0)));
    settings.bpm = std::max(20, std::min(400, static_cast<int>(preferences.getUShort("bpm", 120))));
    settings.transpose = std::max(-12, std::min(12, static_cast<int>(preferences.getChar("transpose", 0))));
    settings.metronome = preferences.getBool("metronome", false); settings.repeat = preferences.getBool("repeat", false);
    settings.showLyrics = preferences.getBool("showLyrics", false);
  }
  runtime.settings(settings);
  // Do NOT erase a failed/unformatted filesystem. Report it and keep keyboard/AP available.
  flashMounted = LittleFS.begin(false);
  if (runtime.sdMounted()) {
    recoverBackups(SD);
    for(int id=3;id<=10;id++) piano::recoverSample(SD,String("/piano-samples/")+String(id)+".hps");
  }
  if (flashMounted) recoverBackups(LittleFS);
  refreshLocalSongs(); updateMenuSong();
  startWifi(); startHttp(); lastMessage = "ready at " + ipString();
}


void applicationLoop() {
  serviceWifi(); server.handleClient();
  if(sampleUpload.expired(millis())) cancelSampleUpload();
  serviceLocal();
  runtime.serviceSamples();
  static String previousSampleMessage;
  const String sampleMessage=runtime.sampleStatus();
  if(sampleMessage!=previousSampleMessage) {
    previousSampleMessage=sampleMessage;
    if(sampleMessage!="samples ready" && sampleMessage!="synth") menuNotice("音源未载入，请检查SD卡");
  }
  runtime.serviceDisplay(ipString(), flashMounted);
  delay(1);
}
