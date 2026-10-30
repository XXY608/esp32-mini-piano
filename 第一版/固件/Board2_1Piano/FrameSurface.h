#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
namespace lcd {
// External PSRAM backing. Compose complete pixels before sending any TFT writes.
class FrameSurface {
 public:
  explicit FrameSurface(uint16_t* buffer): pixels(buffer) {}
  uint16_t* pixels;
  void fillRect(int x,int y,int w,int h,uint16_t color) {
    for(int yy=std::max(0,y);yy<std::min(240,y+h);++yy)
      for(int xx=std::max(0,x);xx<std::min(320,x+w);++xx) pixels[yy*320+xx]=color;
  }
  void drawFastHLine(int x,int y,int w,uint16_t c) { fillRect(x,y,w,1,c); }
  void drawFastVLine(int x,int y,int h,uint16_t c) { fillRect(x,y,1,h,c); }
  static bool rounded(int x,int y,int w,int h,int r) {
    if(x<0 || y<0 || x>=w || y>=h) return false;
    const int dx=x<r?r-x:x>=w-r?x-(w-r-1):0;
    const int dy=y<r?r-y:y>=h-r?y-(h-r-1):0;
    return dx*dx+dy*dy<=r*r;
  }
  void fillRoundRect(int x,int y,int w,int h,int r,uint16_t c) {
    r=std::min(r,std::min(w,h)/2);
    for(int yy=0;yy<h;++yy) for(int xx=0;xx<w;++xx) if(rounded(xx,yy,w,h,r)) fillRect(x+xx,y+yy,1,1,c);
  }
  void drawRoundRect(int x,int y,int w,int h,int r,uint16_t c) {
    for(int yy=0;yy<h;++yy) for(int xx=0;xx<w;++xx)
      if(rounded(xx,yy,w,h,r) && !rounded(xx-1,yy-1,w-2,h-2,r-1)) fillRect(x+xx,y+yy,1,1,c);
  }
  void drawRGBBitmap(int x,int y,uint16_t* bitmap,int w,int h) {
    for(int yy=0;yy<h;++yy) for(int xx=0;xx<w;++xx)
      if(x+xx>=0 && x+xx<320 && y+yy>=0 && y+yy<240) pixels[(y+yy)*320+x+xx]=bitmap[yy*w+xx];
  }
};
template<class Display> unsigned presentFrame(Display& d,uint16_t* current,uint16_t* previous,bool force=false) {
  unsigned changed=0;
  for(int y=0;y<240;++y) {
    if(force) {
      d.drawRGBBitmap(0,y,current+y*320,320,1);
      std::memcpy(previous+y*320,current+y*320,320*sizeof(uint16_t)); changed+=320; continue;
    }
    int x=0;
    while(x<320) {
      while(x<320 && current[y*320+x]==previous[y*320+x]) ++x;
      const int first=x;
      while(x<320 && current[y*320+x]!=previous[y*320+x]) ++x;
      if(x>first) {
        d.drawRGBBitmap(first,y,current+y*320+first,x-first,1);
        std::memcpy(previous+y*320+first,current+y*320+first,(x-first)*sizeof(uint16_t)); changed+=x-first;
      }
    }
  }
  return changed;
}
}
