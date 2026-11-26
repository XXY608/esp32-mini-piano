#pragma once
#include "ChineseText.h"
#include "DisplayText.h"
#include "MidiDisplay.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>

namespace lcd {
// Shared 320x240 RGB565 renderer. It owns no playback or judgement state.
struct GameTile {
  int degree=0, octave=0; bool sharp=false, hit=false;
  float top=0, bottom=0, fill=0; bool holding=false;
  uint8_t column=0, columns=1; bool cue=false;
};
struct GameFrame {
  bool ready=false, paused=false, pauseAvailable=false; std::string title, mode;
  int volume=80, instrument=3;
  unsigned score=0, combo=0; float progress=0; uint16_t keys=0;
  uint32_t session=0, hitSequence[7]={}, hitTime[7]={}; uint8_t inputStyle[7]={2,2,2,2,2,2,2}, hitStyle[7]={};
  static constexpr unsigned MaxTiles=1024; GameTile tiles[MaxTiles]; unsigned count=0;
  bool polyphonic=false, guided=false, keySlots=false; uint16_t guideMask=0;
};
struct GameStyle {
  static constexpr uint16_t rgb(unsigned r,unsigned g,unsigned b){return static_cast<uint16_t>(((r>>3)<<11)|((g>>2)<<5)|(b>>3));}
  static constexpr uint16_t background=0x08e6, panel=0x116b, mint=0x577b, mintDark=0x04f3, violet=0xbc5e, violetDark=0x694e, white=0xefff, muted=0x8dd3, rule=0x21cc;
  static constexpr int width=320,height=240, left=6, lane=44, boardTop=42, line=182, keyTop=187,keyHeight=28, footer=219, radius=4;
  static constexpr unsigned previewMs=2700;
  static constexpr double pixelsPerMs=(line-boardTop)/static_cast<double>(previewMs);
  static int colorIndex(int octave,bool sharp){return (octave<0?0:octave>0?4:2)+(sharp?1:0);}
  static uint16_t accent(int index){static const uint16_t colors[]={rgb(0x60,0xa5,0xfa),rgb(0x8b,0x8c,0xff),rgb(0x4d,0xe0,0xc1),rgb(0xc0,0x84,0xfc),rgb(0xf5,0xc4,0x6b),rgb(0xf5,0x8a,0xb5)};return colors[std::max(0,std::min(5,index))];}
  static uint16_t blend(uint16_t a,uint16_t b,int amount){return static_cast<uint16_t>(((((a>>11)&31)*(16-amount)+((b>>11)&31)*amount)/16<<11)|(((((a>>5)&63)*(16-amount)+((b>>5)&63)*amount)/16)<<5)|(((a&31)*(16-amount)+(b&31)*amount)/16));}
};
class GameScreen {
 public:
  void reset(){valid_=false; previousKeys_=0; for(int i=0;i<7;i++){releaseActive_[i]=false;pulseActive_[i]=false;}}
  template<class D> void draw(D& d,const GameFrame& f,uint32_t now){
    using S=GameStyle;
    if(!f.ready || f.paused){reset();}
    else if(!valid_ || session_!=f.session || polyphonic_!=f.polyphonic || guided_!=f.guided){reset();session_=f.session;polyphonic_=f.polyphonic;guided_=f.guided;for(int i=0;i<7;i++) seenHit_[i]=f.hitSequence[i];valid_=true;}
    if(f.ready && !f.paused){
      for(int i=0;i<7;i++){
        const unsigned bit=1u<<i;
        if(f.keys&bit){releaseActive_[i]=false;keyStyle_[i]=f.inputStyle[i];}
        else if(previousKeys_&bit){releaseTime_[i]=now;releaseActive_[i]=true;}
        if(!f.polyphonic && !f.guided && f.hitSequence[i]!=seenHit_[i]){seenHit_[i]=f.hitSequence[i];pulseTime_[i]=f.hitTime[i];pulseStyle_[i]=f.hitStyle[i];pulseActive_[i]=true;}
      }
      previousKeys_=f.keys;
    }
    d.fillRect(0,0,S::width,S::height,S::background);
    d.fillRoundRect(8,6,3,7,1,S::mint);d.fillRoundRect(13,3,3,10,1,S::mint);d.fillRoundRect(18,6,3,7,1,S::mint);
    text(d,26,1,"音游演奏",S::white,S::background,80);
    d.drawFastHLine(0,39,320,S::rule);
    for(int i=0;i<7;i++) d.drawFastVLine(S::left+i*S::lane,S::boardTop,S::line-S::boardTop,S::rule);
    d.drawFastVLine(S::left+7*S::lane,S::boardTop,S::line-S::boardTop,S::rule);
    if(f.ready&&!f.paused) for(int i=0;i<7;i++){
      const uint32_t age=now-releaseTime_[i];
      const int strength=(f.keys&(1u<<i))?4:(!f.guided&&!f.polyphonic&&releaseActive_[i]&&age<100?4-static_cast<int>(age/25):0);
      if(!strength)continue;
      const int x=S::left+i*S::lane+1;
      for(int band=0;band<4;band++){
        const int level=(4-band)*strength;
        d.fillRect(x,S::line-4*(band+1),S::lane-1,4,S::blend(S::background,S::accent(keyStyle_[i]),level/2));
      }
    }
    if(!f.ready){
      text(d,258,1,"待选曲",S::muted,S::background,56);
      d.fillRoundRect(144,58,6,16,3,S::mint);d.fillRoundRect(156,48,6,23,3,S::mint);d.fillRoundRect(168,58,6,16,3,S::violet);d.drawFastHLine(144,78,30,S::mint);
      text(d,80,84,"准备好下一首了吗",S::white,S::background,160);
      text(d,56,104,"选择一首歌曲，开启节奏演奏",S::muted,S::background,208);
      d.fillRoundRect(60,128,200,37,6,S::panel);d.drawRoundRect(60,128,200,37,6,S::rule);
      d.fillRoundRect(67,134,35,22,4,S::mint);text(d,69,137,"HIGH",S::background,S::mint,32);
      text(d,106,131,"长按1.2秒",S::white,S::panel,150);text(d,106,148,"打开菜单",S::muted,S::panel,64);d.fillRect(174,155,2,2,S::muted);text(d,180,148,"选择歌曲",S::muted,S::panel,64);
    } else {
      char buf[40]; right(d,312,1,f.mode.c_str(),S::white,S::background,180);
      clipped(d,8,20,f.title.c_str(),108,S::muted,S::background);
      const int p=static_cast<int>(std::max(0.f,std::min(1.f,f.progress))*100.f);
      constexpr int progressWidth=78;
      d.fillRoundRect(126,26,progressWidth,4,2,S::rule);
      if(p>0)d.fillRoundRect(126,26,std::max(1,progressWidth*p/100),4,2,S::mint);
      std::snprintf(buf,sizeof(buf),"%d%%",p);text(d,210,20,buf,S::muted,S::background,32);
      if(f.pauseAvailable) right(d,312,20,f.paused?"LOW 继续":"LOW 暂停",S::mint,S::background,64);
      // Notes are ordered by score time. A minimum visible height must not
      // cover the next note on the same lane when the score is very dense.
      float nextBottom[7]={}; bool hasNext[7]={};
      for(unsigned i=std::min(f.count,GameFrame::MaxTiles);i>0;){
        auto note=f.tiles[--i]; if(note.degree<1 || note.degree>7)continue;
        const int lane=note.degree-1;
        if(!f.polyphonic || f.keySlots) {
          if(hasNext[lane]) note.top=std::max(note.top,nextBottom[lane]+1.f);
          nextBottom[lane]=note.bottom;hasNext[lane]=true;
        }
        if(note.top<note.bottom)tile(d,note);
      }
      if(f.paused){d.fillRoundRect(112,102,96,25,4,S::panel);text(d,128,106,"已暂停",S::white,S::panel,64);}
    }
    d.fillRect(S::left,S::line,308,2,S::mint);
    if(f.ready&&!f.paused&&!f.polyphonic&&!f.guided) for(int i=0;i<7;i++){
      if(pulseActive_[i] && static_cast<uint32_t>(now-pulseTime_[i])<150)
        d.fillRect(S::left+i*S::lane+3,S::line, S::lane-6,3,S::accent(pulseStyle_[i]));
    }
    if(f.ready&&!f.paused) for(unsigned i=0;i<f.count&&i<GameFrame::MaxTiles;i++){
      const auto& n=f.tiles[i];
      if(n.holding && n.degree>=1 && n.degree<=7 && n.top<S::line && n.bottom>=S::line) {
        const int w=tileWidth(n);
        const int inset=w>2?1:0;
        d.drawFastHLine(tileX(n)+inset,S::line-1,w-inset*2,S::blend(S::accent(S::colorIndex(n.octave,n.sharp)),S::white,7));
      }
    }
    for(int i=0;i<7;i++){
      const int x=S::left+i*S::lane+2; const bool down=(f.keys&(1u<<i))!=0;const auto paper=down?S::accent(f.inputStyle[i]):S::panel;
      d.fillRoundRect(x,S::keyTop,40,S::keyHeight,S::radius,paper);
      d.drawRoundRect(x,S::keyTop,40,S::keyHeight,S::radius,down?paper:(f.guideMask&(1u<<i))?S::white:S::rule);
      const char number[2]={static_cast<char>('1'+i),0};
      text(d,x+16,S::keyTop+6,number,down?S::background:S::white,paper,8);
      if(down&&(f.keys&0x380)){const char* mod=(f.keys&0x80)?"低":(f.keys&0x200)?"高":"#";text(d,x+2,S::keyTop+6,mod,down?S::background:S::muted,paper,16);}
    }
    d.drawFastHLine(0,S::footer,320,S::rule);
    char volume[8];
    std::snprintf(volume,sizeof(volume),"%d%%",std::max(0,std::min(100,f.volume)));
    text(d,8,222,"音量",S::muted,S::background,32);
    text(d,43,222,volume,S::white,S::background,32);
    d.drawFastVLine(81,225,10,S::rule);
    constexpr int instrumentCount=sizeof(kInstrumentNames)/sizeof(kInstrumentNames[0]);
    const char* instrument=f.instrument>=0 && f.instrument<instrumentCount?kInstrumentNames[f.instrument]:"--";
    text(d,89,222,instrument,S::white,S::background,112);
    right(d,312,222,"HIGH 长按菜单",S::mint,S::background,104);
  }
 private:
  bool valid_=false;
  bool polyphonic_=false, guided_=false;
  uint16_t previousKeys_=0; uint32_t session_=0,releaseTime_[7]={},pulseTime_[7]={},seenHit_[7]={};
  uint8_t keyStyle_[7]={},pulseStyle_[7]={};
  bool releaseActive_[7]={},pulseActive_[7]={};
  template<class D> static void text(D& d,int x,int y,const char* s,uint16_t ink,uint16_t paper,int width){drawText(d,x,y,s,ink,width,paper);}
  template<class D> static void right(D& d,int x,int y,const char* s,uint16_t ink,uint16_t paper,int width){int w=std::min(width,textWidth(s));text(d,x-w,y,s,ink,paper,width);}
  template<class D> static void clipped(D& d,int x,int y,const char* s,int width,uint16_t ink,uint16_t paper){if(textWidth(s)<=width){text(d,x,y,s,ink,paper,width);return;}int n=drawText(d,x,y,s,ink,width-24,paper);text(d,x+n,y,"...",ink,paper,24);}
  static uint16_t tileColor(int style,int y,int top,int bottom,int filledTop,bool hit){
    if(hit && y>=filledTop) return GameStyle::accent(style);
    const int height=std::max(1,bottom-top);
    const int p=std::max(0,std::min(15,(y-top)*16/height));
    return GameStyle::blend(GameStyle::background,GameStyle::accent(style),(hit?12:7)-p/3);
  }
  template<class D> static void tileText(D& d,int x,int y,const char* s,int height,int style,int top,int bottom,int filledTop,bool hit){
    uint16_t pixels[8*16];
    const int step=16/height;
    while(*s){
      const auto* glyph=findGlyph(nextCodepoint(s));if(!glyph)glyph=findGlyph('?');
      // The only labels here are ASCII numbers and '#'. Short tiles use
      // an 8-row glyph, preserving strokes by combining adjacent rows.
      for(int row=0;row<height;++row){
        const unsigned bits=glyph->bitmap[row*step] | (step==2?glyph->bitmap[row*step+1]:0);
        const auto paper=tileColor(style,y+row,top,bottom,filledTop,hit);
        for(int col=0;col<8;++col) pixels[row*8+col]=(bits&(0x80>>col))?GameStyle::white:paper;
      }
      d.drawRGBBitmap(x,y,pixels,8,height);x+=8;
    }
  }
  static int tileX(const GameTile& n) { return GameStyle::left+(n.degree-1)*GameStyle::lane+2+40*n.column/n.columns; }
  static int tileWidth(const GameTile& n) { return 40*(n.column+1)/n.columns-40*n.column/n.columns-(n.columns>1?1:0); }
  template<class D> static void tile(D& d,const GameTile& n){
    using S=GameStyle;if(n.degree<1||n.degree>7)return;
    int top=static_cast<int>(std::floor(n.top)),bottom=static_cast<int>(std::ceil(n.bottom));if(bottom<=S::boardTop||top>=S::line)return;
    const int x=tileX(n),w=tileWidth(n),lo=std::max(top,S::boardTop),hi=std::min(bottom,S::line);if(hi<=lo || w<=0)return;
    const int style=S::colorIndex(n.octave,n.sharp);
    const uint16_t accent=S::accent(style);
    const uint16_t border=n.hit ? accent : n.cue ? S::white : S::blend(S::background,accent,9);
    const float fill=std::max(0.f,std::min(1.f,n.fill));const int filledTop=hi-static_cast<int>((hi-lo)*fill);
    for(int y=lo;y<hi;y++){
      const int edge=std::min(y-top,bottom-1-y);
      const int inset=std::min(std::min((w-1)/2,(bottom-top)/3),edge==0?4:edge==1?2:edge<4?1:0);
      if(edge==0 || w<=2*inset+2)d.drawFastHLine(x+inset,y,w-2*inset,border);
      else{d.drawFastHLine(x+inset,y,w-2*inset,border);d.drawFastHLine(x+inset+1,y,w-2*inset-2,tileColor(style,y,top,bottom,filledTop,n.hit));}
    }
    // Keep labels inside the clipped tile and give short notes a compact label.
    if(hi-lo<10 || w<10) return;
    const bool dots=n.octave && hi-lo>=12 && 3*std::abs(n.octave)-1<=w-2;
    const int height=hi-lo>=(dots?20:18)?16:8;
    const int y=lo+(hi-lo-height)/2;
    char label[3];
    numberedPitchLabel(n.degree,n.sharp && w>=18,label);
    const int tw=textWidth(label);
    tileText(d,x+(w-tw)/2,y,label,height,style,top,bottom,filledTop,n.hit);
    if(dots) drawOctaveDots(d,x+w/2,y,n.octave,S::white,height);
  }
};
}
