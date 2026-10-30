#pragma once
#include "MenuModel.h"
#include "ChineseText.h"
#include "DisplayText.h"
#include "Board2_1Config.h"
#include <cstdio>
#include <cmath>
#include <cstring>

namespace lcd {
// Shared production TFT/native renderer. Redraw on state changes, idle does no SPI work.
class MenuScreen {
 public:
  static constexpr uint16_t kBackground=0x0022, kPanel=0x0864, kFocus=0x0947;
  static constexpr uint16_t kWhite=0xe7ff, kMuted=0x8cf4, kRule=0x194b;
  static constexpr uint16_t kMint=0x3ff9, kBlue=0x4dff, kViolet=0xb49f, kAmber=0xfe10, kCoral=0xfb8f, kCyan=0x3efb;
  void reset() { valid_=false; }
  template<class Display> void draw(Display& d, const piano::MenuState& s, const piano::View& v, uint32_t now) {
    const auto titleLines=wrap(s.songTitle,296);
    const int titlePart=s.page==piano::MenuPage::SongList && titleLines.size()>2 ? (now/2400)%((titleLines.size()+1)/2) : 0;
    if(valid_ && titlePart==titlePart_ && s==previous_ && volume_==v.volume && bpm_==v.settings.bpm && instrument_==v.settings.instrument &&
       transpose_==v.settings.transpose && repeat_==v.settings.repeat && lyrics_==v.settings.showLyrics && metronome_==v.settings.metronome && paused_==v.paused && mode_==v.mode) return;
    const bool full=!valid_ || s.page!=previous_.page;
    if(full) {
      d.fillRect(0,0,320,240,kBackground);
      d.fillRect(0,0,320,30,kBackground);
      tileIcon(d,10,7,s.page==piano::MenuPage::Home?0:s.page==piano::MenuPage::Sound?2:s.page==piano::MenuPage::Rhythm?3:1,pageColor(s.page));
      text(d,34,7,title(s.page),kWhite,212);
      char version[8]; std::snprintf(version,sizeof(version),"%.*s",static_cast<int>(std::strcspn(board::kVersion,"-")),board::kVersion);
      d.fillRoundRect(252,4,56,22,6,kBackground);
      text(d,260,7,version,pageColor(s.page),48);
      for(int i=0;i<3;++i) d.drawFastHLine(8,28+i,304,mix(kBackground,pageColor(s.page),22-i*7));
      d.drawFastHLine(8,197,304,kRule);
    }
    d.fillRect(0,32,320,164,kBackground);
    using P=piano::MenuPage;
    if(s.page==P::Home) home(d,s,v);
    else if(s.page==P::Performance) {
      for(int i=0;i<3;++i) {
        glass(d,8,36+i*53,304,49,kMint,s.row==i);
        const uint16_t paper=s.row==i?kFocus:kPanel;
        text(d,22,40+i*53,i==0?"自由演奏":i==1?"简单练习":"节奏练习",kWhite,260,paper);
        text(d,22,61+i*53,i==0?"显示所选曲谱，自由弹奏":i==1?"可提前按键，按谱依次发声":"前音结束后按键，不接受抢按",kMuted,280,paper);
      }
    }
    else if(s.page==P::DeleteSong) {
      glass(d,8,44,304,130,kCoral,false);
      text(d,24,56,"删除这首歌曲？",kCoral,272,kPanel);
      clipped(d,24,85,s.songTitle.c_str(),272,kWhite,kPanel);
      text(d,24,118,"3确认删除  4取消",kWhite,272,kPanel);
      text(d,24,145,"内置音阶保留，不可删除",kMuted,272,kPanel);
    }
    else if(s.page==P::SongList) songs(d,s,titlePart);
    else if(s.page==P::Connection) info(d,s.networkLines,s.infoOffset,39);
    else if(s.page==P::Diagnostics) diagnostics(d,s);
    else {
      int y=40;
      if(s.page==P::Device) { info(d,s.deviceLines,0,36); y=161; }
      const int count=piano::menuRows(s.page);
      if(s.page==P::Songs) {
        clipped(d,12,34,(std::string("已选：")+s.songTitle).c_str(),296,kMuted);
        clipped(d,12,53,(std::string(kPlaying)+(v.mode==piano::Mode::Manual ? "无" : s.playingTitle)).c_str(),296,kMuted); y=74;
      }
      for(int i=0;i<count;++i) {
        const int rowY=y+i*(s.page==P::Songs?39:43);
        const bool selected=i==s.row;
        const uint16_t paper=selected && !s.editing?kFocus:kPanel;
        glass(d,8,rowY-3,304,36,pageColor(s.page),selected && !s.editing);
        piano::MenuState row=s; row.row=i;
        const auto item=piano::menuItem(row);
        text(d,18,rowY,label(item,v),selected?kWhite:kMuted,176,paper);
        char value[36]; rowValue(value,sizeof(value),item,v,s);
        const bool toggle=item==piano::MenuItem::Metronome || item==piano::MenuItem::Repeat || item==piano::MenuItem::Lyrics;
        const bool on=item==piano::MenuItem::Metronome?v.settings.metronome:item==piano::MenuItem::Lyrics?v.settings.showLyrics:v.settings.repeat;
        if(toggle) {
          d.fillRoundRect(270,rowY+1,32,16,8,on?mix(kPanel,kMint,30):kRule);
          d.fillRoundRect(on?287:273,rowY+4,11,10,5,on?kMint:kMuted);
        } else text(d,300-textWidth(value),rowY,value,selected?pageColor(s.page):kMuted,112,paper);
      }
      if(s.editing) popup(d,s,v);
    }
    d.fillRect(0,199,320,41,kBackground);
    const char* hint=s.page==P::Diagnostics?kDiagHint:s.page==P::Connection?kConnectScroll:s.editing?(piano::menuItem(s)==piano::MenuItem::Volume?kVolumeEditKeys:kMenuEditKeys):kMenuNavKeys;
    text(d,10,201,hint,kBlue,300);
    const char* lower=s.editing ? (piano::menuItem(s)==piano::MenuItem::Tempo?kTempoShortcuts:kEditHint) : s.page==P::SongList?kSongHint:kMenuShortcuts;
    clipped(d,10,221,s.notice.empty()?lower:s.notice.c_str(),300,s.notice.empty()?kMuted:kAmber);
    valid_=true; titlePart_=titlePart; previous_=s; volume_=v.volume; bpm_=v.settings.bpm; instrument_=v.settings.instrument; transpose_=v.settings.transpose;
    repeat_=v.settings.repeat; lyrics_=v.settings.showLyrics; metronome_=v.settings.metronome; paused_=v.paused; mode_=v.mode;
  }
 private:
  static const char* title(piano::MenuPage p) {
    using P=piano::MenuPage;
    switch(p) { case P::Home:return "首页"; case P::Songs:return kHomeLabels[1]; case P::Sound:return kHomeLabels[2];
      case P::Rhythm:return kHomeLabels[3]; case P::Connection:return kHomeLabels[4]; case P::Device:return kHomeLabels[5];
      case P::Performance:return "演奏模式"; case P::DeleteSong:return "删除歌曲"; case P::SongList:return kMenuLabels[0]; case P::Diagnostics:return kMenuLabels[10]; }
    return kMenu;
  }
  static const char* label(piano::MenuItem item,const piano::View& v) {
    if(item==piano::MenuItem::Playback) return v.mode!=piano::Mode::Play?"播放歌曲":v.paused?"继续 / 播放":"暂停 / 播放";
    return kMenuLabels[static_cast<int>(item)];
  }
  template<class D> static void text(D& d,int x,int y,const char* value,uint16_t ink,int width=304,uint16_t paper=kBackground) { drawText(d,x,y,value,ink,width,paper); }
  template<class D> static void clipped(D& d,int x,int y,const char* value,int width,uint16_t ink,uint16_t paper=kBackground) {
    const bool longText=textWidth(value)>width;
    const int used=drawText(d,x,y,value,ink,longText?width-24:width,paper);
    if(longText) text(d,x+used,y,"...",ink,24,paper);
  }
  static uint16_t pageColor(piano::MenuPage page) {
    using P=piano::MenuPage;
    return page==P::Songs || page==P::SongList?kBlue:page==P::Sound?kViolet:
      page==P::Rhythm?kAmber:page==P::Connection?kCyan:page==P::Device || page==P::Diagnostics?kMuted:kMint;
  }
  static uint16_t mix(uint16_t a,uint16_t b,int percent) {
    const int r=((a>>11)*(100-percent)+(b>>11)*percent)/100;
    const int g=(((a>>5)&63)*(100-percent)+((b>>5)&63)*percent)/100;
    const int blue=((a&31)*(100-percent)+(b&31)*percent)/100;
    return static_cast<uint16_t>((r<<11)|(g<<5)|blue);
  }
  template<class D> static void glass(D& d,int x,int y,int w,int h,uint16_t color,bool selected) {
    // Three thin halos and a lit rim suggest glass without blur/framebuffer RAM.
    d.fillRoundRect(x,y,w,h,7,mix(kBackground,color,selected?22:8));
    d.drawRoundRect(x+1,y+1,w-2,h-2,6,mix(kBackground,color,selected?48:18));
    d.fillRoundRect(x+2,y+2,w-4,h-4,5,selected?kFocus:kPanel);
    d.drawRoundRect(x+2,y+2,w-4,h-4,5,mix(kPanel,color,selected?85:26));
    d.drawFastHLine(x+9,y+2,w-18,mix(kPanel,color,selected?100:42));
  }
  template<class D> static void wave(D& d,int x,int y,int width,uint16_t color,uint16_t paper) {
    int previous=0;
    for(int col=0;col<width;++col) {
      const int point=static_cast<int>(std::sin(col*.20)*4 + std::sin(col*.07)*3);
      d.drawFastVLine(x+col,y+std::min(previous,point),std::abs(point-previous)+1,mix(paper,color,90));
      if(col%7==0) d.drawFastVLine(x+col,y+9,2,mix(paper,color,30));
      previous=point;
    }
  }
  template<class D> static void tileIcon(D& d,int x,int y,int index,uint16_t color) {
    switch(index) {
      case 0: case 2:
        d.drawRoundRect(x,y+2,16,12,2,color);
        for(int i=1;i<4;++i) d.drawFastVLine(x+i*4,y+2,12,color);
        for(int i=0;i<3;++i) d.fillRect(x+3+i*4,y+2,2,6,color);
        break;
      case 1:
        d.fillRect(x+5,y+1,9,2,color); d.fillRect(x+5,y+1,2,11,color); d.fillRect(x+12,y+1,2,9,color);
        d.fillRoundRect(x,y+10,7,5,2,color); d.fillRoundRect(x+7,y+8,7,5,2,color);
        break;
      case 3:
        for(int i=0;i<4;++i) d.fillRoundRect(x+i*4,y+10-i*3,2,5+i*3,1,color);
        break;
      case 4:
        d.drawRoundRect(x+4,y,9,16,2,color); d.drawFastHLine(x+6,y+3,5,color); d.fillRect(x+7,y+12,3,2,color);
        break;
      default:
        d.drawRoundRect(x+1,y+1,14,14,4,color); d.fillRoundRect(x+6,y+6,4,4,2,color);
        break;
    }
  }
  template<class D> static void home(D& d,const piano::MenuState& s,const piano::View& v) {
    // Hero + four feature cards + device strip, scaled to 320x240 keys-only UI.
    const uint16_t colors[]={kMint,kCyan,kViolet,kAmber,kBlue,kMint};
    for(int i=0;i<6;++i) {
      const bool selected=i==s.home;
      const uint16_t paper=selected?kFocus:kPanel;
      if(i==0) {
        glass(d,8,34,304,45,colors[i],selected);
        tileIcon(d,19,47,0,kMint);
        text(d,45,40,"演奏模式",kWhite,128,paper);
        text(d,45,59,kModeNames[static_cast<int>(v.mode)],kMuted,128,paper);
        text(d,188,40,s.phoneOutput?"手机发声":"本机发声",kMint,112,paper);
        wave(d,192,66,100,kCyan,paper);
        continue;
      }
      if(i==5) {
        glass(d,8,176,304,20,kMint,selected);
        text(d,20,178,kHomeLabels[5],kWhite,80,paper);
        text(d,106,178,"逐键检查 / 状态",kMuted,160,paper);
        text(d,291,178,">",kMint,8,paper);
        continue;
      }
      const int x=8+((i-1)%2)*156,y=83+((i-1)/2)*46;
      glass(d,x,y,148,43,colors[i],selected);
      tileIcon(d,x+11,y+9,i,colors[i]);
      text(d,x+34,y+5,kHomeLabels[i],kWhite,108,paper);
      char summary[40]; summary[0]=0;
      if(i==2) std::snprintf(summary,sizeof(summary),"%s %d%%",kInstrumentNames[v.settings.instrument],v.volume);
      if(i==3) std::snprintf(summary,sizeof(summary),"BPM %d  %+d",v.settings.bpm,v.settings.transpose);
      if(i==4) std::snprintf(summary,sizeof(summary),"Wi-Fi / App");
      clipped(d,x+12,y+24,i==1?(s.builtinSelected?"选曲播放或练习":s.songTitle.c_str()):summary,124,selected?colors[i]:kMuted,paper);
    }
  }
  template<class D> static void songs(D& d,const piano::MenuState& s,int part) {
    if(s.songCount==0) { text(d,12,70,kNoSong,kMuted); return; }
    char detail[60]; std::snprintf(detail,sizeof(detail),"%s / %s%d",s.songPosition.c_str(),s.songBpmProvided?kBuiltinBpm:kUnknownBpm,s.songBpm);
    clipped(d,12,34,detail,296,kMuted);
    for(int i=0;i<4;++i) {
      const int index=s.songTop+i; if(index>=s.songCount) break;
      const int y=57+i*25;
      const bool selected=index==s.songIndex;
      const uint16_t paper=selected?kFocus:kPanel;
      glass(d,8,y-2,304,24,kCyan,selected);
      tileIcon(d,17,y+2,1,selected?kCyan:kRule);
      clipped(d,40,y,s.songRows[i].c_str(),264,selected?kWhite:kMuted,paper);
    }
    // Two lines expose longer selected titles instead of only a sequence number.
    const auto parts=wrap(s.songTitle,296);
    if(static_cast<size_t>(part*2)<parts.size()) text(d,12,159,parts[part*2].c_str(),kWhite);
    if(static_cast<size_t>(part*2+1)<parts.size()) text(d,12,178,parts[part*2+1].c_str(),kMuted);
  }
  template<class D> static void info(D& d,const std::vector<std::string>& lines,int offset,int y) {
    for(int i=0;i<7 && i+offset<static_cast<int>(lines.size());++i) {
      const auto& line=lines[i+offset];
      const bool error=line.find("失败")!=std::string::npos || line.find("异常")!=std::string::npos;
      text(d,12,y+i*21,line.c_str(),error?kCoral:i==0?kMint:kMuted,296);
    }
  }
  template<class D> static void diagnostics(D& d,const piano::MenuState& s) {
    for(int i=0;i<10;++i) {
      const int x=10+(i%5)*62,y=60+(i/5)*57; const bool on=s.keyMask&(1u<<i);
      d.fillRoundRect(x,y,52,44,6,on?kFocus:kPanel);
      d.drawRoundRect(x,y,52,44,6,on?kMint:kRule);
      char name[8]; if(i<7) std::snprintf(name,sizeof(name),"%d",i+1); else std::snprintf(name,sizeof(name),"%s",i==7?"LOW":i==8?"#":"HIGH");
      text(d,x+6,y+14,name,on?kMint:kMuted,44,on?kFocus:kPanel);
    }
    text(d,12,173,"此页按键不发声，4返回",kMuted);
  }
  static void rowValue(char* out,size_t n,piano::MenuItem item,const piano::View& v,const piano::MenuState& s) {
    using I=piano::MenuItem; out[0]=0;
    if(item==I::Instrument) std::snprintf(out,n,"%s",kInstrumentNames[v.settings.instrument]);
    else if(item==I::Output) std::snprintf(out,n,"%s",kOutputNames[s.phoneOutput?1:0]);
    else if(item==I::Volume) std::snprintf(out,n,"%d%%",v.volume);
    else if(item==I::Tempo) std::snprintf(out,n,"%d",v.settings.bpm);
    else if(item==I::Transpose) std::snprintf(out,n,"%+d",v.settings.transpose);
    else if(item==I::Metronome || item==I::Repeat || item==I::Lyrics) std::snprintf(out,n,"%s",(item==I::Metronome?v.settings.metronome:item==I::Lyrics?v.settings.showLyrics:v.settings.repeat)?kOn:kOff);
    else std::snprintf(out,n,">");
  }
  template<class D> static void popup(D& d,const piano::MenuState& s,const piano::View& v) {
    const auto item=piano::menuItem(s); const char* name=label(item,v);
    const uint16_t color=item==piano::MenuItem::Volume?kMint:item==piano::MenuItem::Instrument?kViolet:item==piano::MenuItem::Tempo?kAmber:kBlue;
    d.fillRoundRect(27,62,272,132,10,kBackground);
    glass(d,24,58,272,132,color,false);
    d.fillRoundRect(148,61,24,2,1,color);
    text(d,160-textWidth(name)/2,68,name,kMuted,224,kPanel);
    if(item==piano::MenuItem::Output) {
      const int selected=s.previewOutput>=0?s.previewOutput:(s.phoneOutput?1:0);
      for(int i=0;i<2;++i) {
        const int y=94+i*28;
        if(i==selected) d.fillRoundRect(38,y,244,24,4,kFocus);
        text(d,48,y+4,kOutputNames[i],i==selected?kWhite:kMuted,212,i==selected?kFocus:kPanel);
      }
      text(d,48,158,s.phoneReady?"手机已就绪，可切换":"手机端需先启用发声",kMuted,224,kPanel);
    } else if(item==piano::MenuItem::Instrument) {
      const int instrument=s.previewInstrument>=0 ? s.previewInstrument : v.settings.instrument;
      for(int row=0;row<3;++row) {
        const int i=std::max(0,std::min(8,instrument-1))+row;
        const bool selected=i==instrument;
        const int y=94+row*23;
        if(selected) d.fillRoundRect(38,y,244,21,4,kFocus);
        text(d,48,y+2,kInstrumentNames[i],selected?kWhite:kMuted,212,selected?kFocus:kPanel);
      }
    } else {
      char value[36]; rowValue(value,sizeof(value),item,v,s);
      const int position=item==piano::MenuItem::Volume?v.volume:item==piano::MenuItem::Tempo?(v.settings.bpm-20)*100/380:(v.settings.transpose+12)*100/24;
      const bool tempo=item==piano::MenuItem::Tempo;
      if(tempo) for(int tick=0;tick<60;++tick) {
        const double angle=(tick*6-90)*3.141592653589793/180;
        const int x=160+static_cast<int>(std::cos(angle)*36),y=118+static_cast<int>(std::sin(angle)*36);
        d.fillRect(x,y,2,2,tick*100<position*60?color:kRule);
      }
      drawDoubleText(d,160-textWidth(value),103,value,color,224,kPanel);
      const int trackY=tempo?169:151;
      d.fillRoundRect(54,trackY,212,5,2,kRule);
      if(position>0) d.fillRoundRect(54,trackY,212*position/100,5,2,color);
      d.fillRoundRect(51+212*position/100,trackY-4,9,13,4,color);
    }
  }
 public:
  static std::vector<std::string> wrap(const std::string& value,int width=296) {
    std::vector<std::string> lines; std::string line; int used=0; const char* cursor=value.c_str();
    while(*cursor) { const char* start=cursor; const auto code=nextCodepoint(cursor); const int w=code<128?8:16;
      if(used+w>width) { lines.push_back(line); line.clear(); used=0; }
      line.append(start,static_cast<size_t>(cursor-start)); used+=w;
    }
    lines.push_back(line); return lines;
  }
 private:
  bool valid_=false,repeat_=false,lyrics_=false,metronome_=false,paused_=false;
  int titlePart_=0,volume_=-1,bpm_=0,instrument_=0,transpose_=0;
  piano::Mode mode_=piano::Mode::Manual;
  piano::MenuState previous_;
};
}
