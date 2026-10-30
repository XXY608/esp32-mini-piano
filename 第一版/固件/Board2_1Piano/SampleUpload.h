#pragma once
#include "SampleVoice.h"
#include <string>

namespace piano {
inline uint32_t sampleCrc(uint32_t crc, const uint8_t* data, size_t count) {
  for(size_t i=0;i<count;i++) { crc^=data[i]; for(int bit=0;bit<8;bit++) crc=(crc>>1)^(0xedb88320u & (0u-(crc&1u))); }
  return crc;
}
class SampleUpload {
 public:
  bool active=false;
  int instrument=-1;
  uint32_t session=0, lastMs=0, expectedCrc=0, crc=0xffffffffu;
  size_t received=0;
  uint8_t header[32]={};
  bool begin(int id, size_t bytes, uint32_t token, uint32_t checksum, uint32_t now) {
    if(active || id<3 || id>10 || bytes!=SampleBank::kBytes || !token) return false;
    instrument=id;session=token;expectedCrc=checksum;lastMs=now;received=0;crc=0xffffffffu;active=true;
    return true;
  }
  bool owns(uint32_t token) const { return active && token && token==session; }
  bool append(uint32_t token, size_t offset, const uint8_t* bytes, size_t count, uint32_t now) {
    if(!owns(token) || expired(now) || offset!=received || !bytes || !count || count>65536 || count>SampleBank::kBytes-received) return false;
    for(size_t i=0;i<count && received+i<sizeof(header);i++) header[received+i]=bytes[i];
    crc=sampleCrc(crc,bytes,count);received+=count;lastMs=now;return true;
  }
  bool valid() const { return active && received==SampleBank::kBytes && (crc^0xffffffffu)==expectedCrc && SampleBank::validHeader(header,instrument); }
  bool expired(uint32_t now) const { return active && uint32_t(now-lastMs)>120000u; }
};
// Staged binary commit; never reads a multi-megabyte file into a String.
template<class Store, class Text>
bool recoverSample(Store& fs,const Text& target) {
  const Text backup=target+".bak";
  return !fs.exists(backup) || fs.exists(target) || fs.rename(backup,target);
}
template<class Store, class Text>
bool commitSample(Store& fs,const Text& target) {
  const Text temp=target+".tmp",backup=target+".bak";
  if(!recoverSample(fs,target) || !fs.exists(temp)) return false;
  if(fs.exists(backup) && !fs.remove(backup)) return false;
  const bool previous=fs.exists(target);
  if(previous && !fs.rename(target,backup)) return false;
  if(!fs.rename(temp,target)) { if(previous) fs.rename(backup,target);return false; }
  if(previous) fs.remove(backup);
  return true;
}
}
