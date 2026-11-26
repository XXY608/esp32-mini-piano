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
    const int left=std::max(0,x), right=std::min(320,x+w);
    const int top=std::max(0,y), bottom=std::min(240,y+h);
    if(left>=right || top>=bottom) return;
    if(left==0 && right==320) {
      std::fill_n(pixels+top*320, (bottom-top)*320, color);
      return;
    }
    for(int row=top;row<bottom;++row) std::fill_n(pixels+row*320+left,right-left,color);
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
    const int left=std::max(0,x), right=std::min(320,x+w);
    if(left>=right) return;
    for(int row=std::max(0,y);row<std::min(240,y+h);++row)
      std::memcpy(pixels+row*320+left,bitmap+(row-y)*w+(left-x),(right-left)*sizeof(uint16_t));
  }
};
template<class Display> unsigned presentFrame(Display& d,uint16_t* current,uint16_t* previous,bool force=false) {
  constexpr int width=320, height=240;
  if(force) {
    d.startWrite();
    d.setAddrWindow(0,0,width,height);
    d.writePixels(current,width*height);
    d.endWrite();
    std::memcpy(previous,current,width*height*sizeof(uint16_t));
    return width*height;
  }

  // An address window costs more bus bytes than sending four unchanged pixels.
  constexpr int padding=4;
  alignas(uint32_t) uint16_t packed[width*2];
  unsigned sent=0;
  for(int y=0;y<height;++y) {
    const int offset=y*width;
    if(std::memcmp(current+offset,previous+offset,width*sizeof(uint16_t))==0) continue;
    for(int x=0;x<width;) {
      while(x<width && current[offset+x]==previous[offset+x]) ++x;
      if(x==width) break;
      const int left=x++;
      int right=x;
      while(x<width && x-right<padding+1) {
        if(current[offset+x]!=previous[offset+x]) right=x+1;
        ++x;
      }

      const int span=right-left;
      int bottom=y+1;
      for(;bottom<height;++bottom) {
        const int row=bottom*width;
        int unchanged=0;
        for(int col=left;col<right && unchanged<=padding;++col)
          unchanged+=current[row+col]==previous[row+col];
        if(unchanged>padding || unchanged==span) break;
      }

      if(!sent) d.startWrite();
      d.setAddrWindow(left,y,span,bottom-y);
      const int rowsPerWrite=static_cast<int>(sizeof(packed)/sizeof(packed[0]))/span;
      for(int row=y;row<bottom;) {
        const int rows=std::min(rowsPerWrite,bottom-row);
        for(int line=0;line<rows;++line) {
          const int start=(row+line)*width+left;
          std::memcpy(packed+line*span,current+start,span*sizeof(uint16_t));
          std::memcpy(previous+start,current+start,span*sizeof(uint16_t));
        }
        const int count=rows*span;
        if(count%2) packed[count]=0; // The SPI driver reads the final 32-bit word.
        d.writePixels(packed,count);
        row+=rows;
      }
      sent+=span*(bottom-y);
      x=right;
    }
  }
  if(sent) d.endWrite();
  return sent;
}
}
