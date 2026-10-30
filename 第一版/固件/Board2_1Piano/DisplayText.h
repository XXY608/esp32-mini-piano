#pragma once
namespace lcd {
constexpr char kTitle[]="迷你电子琴", kMenu[]="功能菜单", kSong[]="曲目：", kVolume[]="音量：";
constexpr char kCurrent[]="当前：", kTarget[]="目标：", kProgress[]="进度：", kRest[]="休止";
constexpr char kLowNote[]="低音", kHighNote[]="高音", kLowKey[]="低八度", kSharpKey[]="升半音", kHighKey[]="高八度";
constexpr char kPaused[]="已暂停", kInstrument[]="音色：", kTempo[]="速度：", kMetronome[]="节拍器：";
constexpr char kOn[]="开", kOff[]="关", kNetwork[]="网络：", kAudioReady[]="声音就绪", kAudioFault[]="声音异常";
constexpr char kMenuHint[]="长按最右键1.2秒进入菜单", kExitHint[]="按最右键退出菜单";
constexpr char kMeterUnknown[]="拍号未标", kScoreEmpty[]="选曲后开始播放或跟弹", kFinished[]="演奏完成";
constexpr const char* kModeNames[]={"自由演奏","自动播放","跟弹练习","自由演奏","简单练习","节奏练习"};
constexpr const char* kInstrumentNames[]={"纯音","合成钢琴","合成风琴","GS钢琴","Salamander钢琴","口琴","长笛","小提琴","木吉他","GS风琴","八音盒"};
constexpr const char* kStorageNames[]={"曲库存储不可用","内置存储","存储卡","存储卡+内置备份"};
constexpr const char* kHomeLabels[]={"自由演奏","曲谱与练习","音色与音量","节奏与调性","手机连接","设备状态"};
constexpr const char* kMenuLabels[]={"选择曲谱","播放 / 暂停","从头跟弹","停止歌曲","循环播放","选择音色","音量","速度 BPM","节拍器","移调","逐键检查","显示歌词","发声设备"};
constexpr const char* kOutputNames[]={"本机扬声器","手机扬声器"};
constexpr char kMenuNavKeys[]="1上一个 2下一个 3确认 4返回";
constexpr char kMenuEditKeys[]="1上一个 2下一个 3确认 4取消";
constexpr char kVolumeEditKeys[]="1增加 2减少 3确认 4取消";
constexpr char kInstrumentEditKeys[]="1上一个 2下一个 3确认 4取消";
constexpr char kMenuShortcuts[]="按住1/2连移  最右键退出";
constexpr char kTempoShortcuts[]="5/6减加10  按最右键退出取消";
constexpr char kEditHint[]="按最右键退出并取消";
constexpr char kConnectScroll[]="1上一个 2下一个 4返回";
constexpr char kDiagHint[]="逐个按键查看亮灯 4返回";
constexpr char kSongHint[]="3选定 4取消 5删除歌曲";
constexpr char kSelected[]="已选", kPlaying[]="正在：", kNoSong[]="暂无曲谱，请用手机导入";
constexpr char kStoredFailed[]="设置已应用，但保存失败", kRemoteChanged[]="手机已更新，编辑已结束";
// Shared labels for dynamic connection/device lines; included in font validation.
constexpr char kApReady[]="热点已启动", kApFailed[]="热点启动失败", kApName[]="热点名称", kApPassword[]="热点密码", kOpenAp[]="无密码";
constexpr char kWifiCount[]="连接设备数：", kConnectStep1[]="手机先连接上述热点";
constexpr char kConnectStep2[]="App填写地址，或浏览器打开";
constexpr char kStaAddress[]="局域网地址：", kFirmware[]="固件：", kStorage[]="存储：", kControlsOk[]="按键任务就绪", kControlsFailed[]="按键任务异常";
constexpr char kSongReadFailed[]="曲谱读取失败，请重新选曲", kAudioNotReady[]="声音或按键未就绪";
constexpr char kUnknownBpm[]="默认播放 BPM ", kBuiltinBpm[]="源谱 BPM ";
}
