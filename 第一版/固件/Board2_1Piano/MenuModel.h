#pragma once
#include "PianoCore.h"
#include <algorithm>
#include <array>
#include <string>
#include <vector>

namespace piano {
enum class MenuPage { Home, Songs, Sound, Rhythm, Connection, Device, SongList, Diagnostics, Performance, DeleteSong };
enum class MenuItem { Song, Playback, Teach, Stop, Repeat, Instrument, Volume, Tempo, Metronome, Transpose, Diagnostics, Lyrics, Output };
enum class MenuCommand { None, Manual, Exit, LoadSongs, SongPrevious, SongNext, SongSelect, SongCancel,
  PlayPause, Teach, Stop, BeginEdit, CommitEdit, CancelEdit, Decrease, Increase, DecreaseTen, IncreaseTen,
  VolumeDown, VolumeUp, Metronome, Repeat, Lyrics, FreeScore, Simple, AskDelete, Delete, RhythmPractice };
struct MenuState {
  MenuPage page = MenuPage::Home;
  int home = 0, row = 0, infoOffset = 0;
  bool editing = false;
  int previewInstrument = -1;
  int previewOutput = -1;
  bool phoneReady = false, phoneOutput = false;
  bool builtinSelected = true;
  std::string songTitle = "内置音阶", songPosition = "1/1", playingTitle, notice;
  std::array<std::string, 4> songRows;
  int songIndex = 0, songCount = 1, songTop = 0, songBpm = 100;
  bool songBpmProvided = true;
  std::vector<std::string> networkLines, deviceLines;
  uint16_t keyMask = 0;
  bool operator==(const MenuState& b) const {
    return page==b.page && home==b.home && row==b.row && infoOffset==b.infoOffset && editing==b.editing && previewInstrument==b.previewInstrument &&
      previewOutput==b.previewOutput && phoneReady==b.phoneReady && phoneOutput==b.phoneOutput && builtinSelected==b.builtinSelected &&
      songTitle==b.songTitle && songPosition==b.songPosition && playingTitle==b.playingTitle && notice==b.notice &&
      songRows==b.songRows && songIndex==b.songIndex && songCount==b.songCount && songTop==b.songTop &&
      songBpm==b.songBpm && songBpmProvided==b.songBpmProvided && networkLines==b.networkLines &&
      deviceLines==b.deviceLines && keyMask==b.keyMask;
  }
};
struct MenuSongSummary {
  std::string id, title;
  int bpm = 120;
  bool bpmProvided = false;
};
// Browsing uses metadata only; storage is read when the catalog is refreshed.
inline void showMenuSong(MenuState& state,const std::vector<MenuSongSummary>& songs,size_t index,bool browsing) {
  const auto& song=songs[index];
  state.songIndex=static_cast<int>(index);state.songCount=static_cast<int>(songs.size());
  state.songPosition=std::to_string(index+1)+"/"+std::to_string(songs.size());
  state.songTitle=song.title;state.songBpm=song.bpm;state.songBpmProvided=song.bpmProvided;
  state.builtinSelected=song.id=="demo-scale";
  if(!browsing) return;
  state.songTop=std::max(0,std::min(state.songIndex-1,std::max(0,state.songCount-4)));
  for(int row=0;row<4;++row) {
    const size_t n=static_cast<size_t>(state.songTop+row);
    state.songRows[row]=n<songs.size()?songs[n].title:"";
  }
}
inline int cycleIndex(int index,int delta,int count) { return count>0 ? ((index+delta)%count+count)%count : 0; }
inline int menuRows(MenuPage page) { return page==MenuPage::Songs ? 3 : page==MenuPage::Sound ? 3 : page==MenuPage::Rhythm ? 3 : page==MenuPage::Device ? 1 : 0; }
inline MenuItem menuItem(const MenuState& s) {
  static constexpr MenuItem songs[] = {MenuItem::Song, MenuItem::Playback, MenuItem::Lyrics};
  static constexpr MenuItem sound[] = {MenuItem::Instrument, MenuItem::Volume, MenuItem::Output};
  static constexpr MenuItem rhythm[] = {MenuItem::Tempo, MenuItem::Metronome, MenuItem::Transpose};
  if (s.page==MenuPage::Songs) return songs[std::max(0,std::min(2,s.row))];
  if (s.page==MenuPage::Sound) return sound[std::max(0,std::min(2,s.row))];
  if (s.page==MenuPage::Rhythm) return rhythm[std::max(0,std::min(2,s.row))];
  return MenuItem::Diagnostics;
}
// A single-field transaction: remote writes to that same field invalidate cancellation.
// Other settings are read fresh on each write, so cancelling never rolls them back.
class MenuEdit {
 public:
  bool active = false;
  MenuItem item = MenuItem::Volume;
  int original = 0;
  int pendingInstrument = 0;
  int pendingOutput = 0;
  void begin(MenuItem field, const View& v) { item=field; original=value(v); pendingInstrument=v.settings.instrument; active=true; }
  int value(const View& v) const {
    return item==MenuItem::Volume ? v.volume : item==MenuItem::Instrument ? v.settings.instrument :
      item==MenuItem::Tempo ? v.settings.bpm : v.settings.transpose;
  }
  void browseInstrument(int delta) { pendingInstrument=cycleIndex(pendingInstrument,delta,11); }
  void remote(MenuItem field) { if (active && item==field) active=false; }
  void finish() { active=false; }
};
class MenuController {
 public:
  MenuState state;
  MenuCommand input(int key) {
    using C=MenuCommand; using P=MenuPage;
    if (key==7) {
      const bool edit=state.editing; state.editing=false; state.page=P::Home; state.row=0; state.infoOffset=0; state.notice.clear();
      return edit ? C::CancelEdit : C::None;
    }
    if (key<0 || key==6 || key>=8) return C::None;
    if (state.page==P::Diagnostics) {
      if(key==3) { state.page=P::Device; state.row=0; }
      return C::None;
    }
    if(state.page==P::DeleteSong) {
      if(key==3) { state.page=P::SongList; return C::None; }
      if(key==2) { state.page=P::SongList; return C::Delete; }
      return C::None;
    }
    if(state.page==P::Performance) {
      if(key==3) { state.page=P::Home; return C::None; }
      if(key==0 || key==1) state.row=cycleIndex(state.row,key==0?-1:1,3);
      return key==2 ? (state.row==0?C::FreeScore:state.row==1?C::Simple:C::RhythmPractice) : C::None;
    }
    if (state.page==P::SongList && key==4) { state.page=P::DeleteSong; return C::AskDelete; }
    if (key==4 || key==5) {
      if (!state.editing) return key==4 ? C::VolumeDown : C::VolumeUp;
      if (menuItem(state)==MenuItem::Tempo) return key==4 ? C::DecreaseTen : C::IncreaseTen;
      return C::None;
    }
    if (state.editing) {
      if (key==3 || key==2) { state.editing=false; return key==3 ? C::CancelEdit : C::CommitEdit; }
      if (menuItem(state)==MenuItem::Volume) return key==0 ? C::Increase : key==1 ? C::Decrease : C::None;
      return key==0 ? C::Decrease : key==1 ? C::Increase : C::None;
    }
    if (state.page==P::SongList) {
      if(key==3) { state.page=P::Songs; return C::SongCancel; }
      if(key==2 && state.songCount>0) { state.page=P::Performance; state.row=0; return C::SongSelect; }
      return key==0 ? C::SongPrevious : key==1 ? C::SongNext : C::None;
    }
    if (key==3) {
      if(state.page==P::Home) return C::Exit;
      state.page=P::Home; state.row=0; state.infoOffset=0; return C::None;
    }
    if (state.page==P::Home) {
      if(key==0 || key==1) state.home=cycleIndex(state.home,key==0?-1:1,6);
      if(key!=2) return C::None;
      if(state.home==0) { state.page=P::Performance; state.row=0; return C::None; }
      static constexpr P pages[]={P::Home,P::Songs,P::Sound,P::Rhythm,P::Connection,P::Device};
      state.page=pages[state.home]; state.row=0; state.infoOffset=0; return C::None;
    }
    if(state.page==P::Connection) {
      if(key==0 || key==1) state.infoOffset=cycleIndex(state.infoOffset,key==0?-1:1,std::max(1,static_cast<int>(state.networkLines.size())-6));
      return C::None;
    }
    if(key==0 || key==1) { state.row=cycleIndex(state.row,key==0?-1:1,menuRows(state.page)); return C::None; }
    if(key!=2) return C::None;
    switch(menuItem(state)) {
      case MenuItem::Song: state.page=P::SongList; return C::LoadSongs;
      case MenuItem::Playback: return C::PlayPause;
      case MenuItem::Teach: return C::Teach;
      case MenuItem::Stop: return C::Stop;
      case MenuItem::Repeat: return C::Repeat;
      case MenuItem::Lyrics: return C::Lyrics;
      case MenuItem::Metronome: return C::Metronome;
      case MenuItem::Diagnostics: state.page=P::Diagnostics; return C::None;
      default: state.editing=true; return C::BeginEdit;
    }
  }
};
}
