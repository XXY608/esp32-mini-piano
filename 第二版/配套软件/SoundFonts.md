# Windows 社区音源

本机播放、练习模式使用 FluidSynth 2.4.8（官方 Windows x64 二进制）。
七种原有音色改用 GeneralUser GS 2.0.3；另有 Salamander 三角钢琴和原合成钢琴供比较。
Android/ESP32没有接入采样库，移动端仍使用既有合成方式。

资源在工作区 work/soundfonts，不嵌入 EXE。离线使用；移动程序时须保留此目录。
- GeneralUser-GS.sf2：S. Christian Collins，https://github.com/mrbumpy409/GeneralUser-GS
  授权原文 GeneralUser-LICENSE.txt；作者允许软件项目使用和重新打包，原始采样来源说明以原文为准。
- Salamander.sf2：Alexander Holm，Yamaha C5；FreePats SF2 V3+20200602。
  https://freepats.zenvoid.org/Piano/acoustic-grand-piano.html
  Creative Commons Attribution 3.0：https://creativecommons.org/licenses/by/3.0/
  音源仅重命名，保留解包后的 readme；没有修改采样。
- fluidsynth/bin：来自 https://github.com/FluidSynth/fluidsynth/releases/tag/v2.4.8
  FluidSynth LGPL-2.1；原文 FluidSynth-LICENSE.txt 和 AUTHORS 文件在资源目录。
  DLL 使用官方包内的依赖（GLib、libinstpatch、libsndfile、libintl、SDL3），保留动态链接形式。

导入 CCMZ、MIDI 或 MusicXML 并保留原始谱面时，本机发声按原谱多音播放，使用 44.1 kHz、16 bit 双声道。多音播放使用每个音符的起止时间与力度，CCMZ 支持变速和延音踏板。编辑简谱或导入 TXT 后，本机发声按简谱播放，简谱使用固定力度。

「钢琴 · 音乐厅」以 Salamander 三角钢琴采样为基础，使用 `work/soundfonts/Piano-Hall.json` 配置均衡、立体声宽度和混响。移动程序时，音色配置需和 SoundFont、FluidSynth 保存在同一资源目录。

验证并更新根电子琴：执行 Build-Release.ps1 -NoPackage。
当前用户要求暂不生成新安装包。旧安装包不包含本次音源功能。
