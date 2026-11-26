# 迷你电子琴网页试玩

用浏览器跟弹内置音阶，或自由弹奏七个音级键并搭配三个修饰键。键盘 `Z X C V B N M` 对应简谱 1–7，自由弹奏时可按住 `,` 降八度、`.` 升半音、`/` 升八度。网页使用 0.6.39 的 `Engine`、`ManualMixer`、`PerformanceMixer` 和 HPS 采样。AudioWorklet 按 22.05 kHz 输出固件混音后的 PCM，播放设备仍会影响最终听感。

- 网页发布目录是 [docs/try](../../docs/try/)；音色按需载入，每种约 5.5 MB。
- 默认「音乐厅钢琴」是琴端 11 号 HPS，由 Alexander Holm 的 [Salamander Grand Piano](https://freepats.zenvoid.org/Piano/acoustic-grand-piano.html) 派生，包含单声道采样、音量平衡、均衡及短环境反射，适用 [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/)。
- 4 号是同源的 Salamander 钢琴；3、5–10 号由 S. Christian Collins 的 [GeneralUser GS](https://github.com/mrbumpy409/GeneralUser-GS) 制作。采样来源、加工方式和许可原文位于 [banks](../../docs/try/banks/)。

## 构建与检查

用 WASI SDK 25 构建网页音频模块：

```powershell
pwsh -File ./Build-TryAudio.ps1 -WasiBin '<WASI SDK bin>'
```

用 Zig C++17 等宿主编译器编译 `NativeReference.cpp` 与 `PianoCore.cpp`、`cJSON.c`，加入同一固件和 cJSON 头文件路径。把所得程序传入 `node Verify-TryAudio.cjs <native-reference.exe>`，检查九个音色 SHA-256 及原生和 WebAssembly PCM。覆盖自由弹奏、同时按键、修饰键、长按、松键和简单跟弹。每个音色 212,480 帧，已测最大差为一个 16 位 PCM 量化单位。

运行 `node PackOffline.cjs ../../docs/try <output.html>` 可生成自包含的离线版，内置音乐厅钢琴；其他音色可选取琴端 SD 卡的 HPS 文件。浏览器需支持 AudioWorklet 和 22.05 kHz AudioContext。

## 完整模拟

打开 [完整版](https://xxy608.github.io/esp32-mini-piano/try/full/)，点击开机，单独长按 **/** 进入琴端菜单。页面包含实琴音阶和为网页制作的「和弦练习」，后者可分别用于简单单音、复杂多音、单键伴奏、自主节奏和自动播放。

完整版的状态机使用固件的 LocalControls、MenuController、Engine、ChordPractice、GameAssist 和 GameFlow。浏览器画布显示固件的菜单、简谱和音游渲染，AudioWorklet 使用 22.05 kHz 琴端混音。HPS 采样与快速试玩共用，默认音乐厅钢琴。浏览器文件导入与本地存储代替 SD／闪存和 Wi-Fi 传输，手机连接及外部扬声器切换在页面中由浏览器功能代替。

同一个 Build-TryAudio.ps1 同时编译快速试玩和完整版，完整版文件输出到 [docs/try/full](../../docs/try/full/)。打开站点时使用 HTTPS 或 localhost。现有单文件离线包仍为快速试玩。

更多见[网页在线曲库](在线曲库.md)。
