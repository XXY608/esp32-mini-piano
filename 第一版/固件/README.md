# 第一版 ESP32-S3 固件

这里是 Board2_1 的 0.6.16 源码，包含 TFT 界面、按键演奏、曲谱及 Wi-Fi 网页。选曲后可选择自由演奏、简单练习或节奏练习。节奏练习等待当前音时值结束后接受下一音，不提供拍点评分。

## 使用与构建

Arduino IDE 打开 [Board2_1Piano.ino](Board2_1Piano/Board2_1Piano.ino)，使用 ESP32-S3-WROOM-1-N16R8，Arduino ESP32 Core 2.0.17，16 MB QIO Flash、8 MB OPI PSRAM；依赖 Adafruit ST7789 1.11.0、GFX 1.12.6、BusIO 1.17.4。采用默认分区，不要在更新时擦除整个 Flash。页面源码在 `Board2_1Piano/web/`，更改后运行 `Embed-WebUi.ps1` 生成 `WebUi.h`。`Board2_1Tests` 为测试源码；原自动化构建依赖作者本机工具及音色文件，未在本仓库打包，其他环境需自行配置。

| 功能 | GPIO |
| --- | --- |
| 白键 1–7 | 4 / 5 / 6 / 7 / 38 / 39 / 40 |
| LOW / SHARP / HIGH | 41 / 42 / 47 |
| I2S BCLK / WS / DIN | 17 / 18 / 21 |
| SPI SCK / MOSI / MISO | 12 / 11 / 13 |
| TFT CS / DC / RST / BL | 10 / 14 / 15 / 16 |
| SD CS | 2 |

默认热点 `HarmonicaPlayer-ESP32`，密码 `harmonica`，浏览器访问 `http://192.168.4.1/`。可通过私有 `Board2_1Secrets.h` 覆盖配置，该文件不应提交。默认密码为公开值，请按使用环境自行设置。

项目记录显示 0.6.16 构建通过并已烧录；最新选曲和演奏模式流程尚待实机验收。模拟测试不等于实机验收。采样音色文件未包含在仓库内，使用采样功能需另行准备。

中文点阵来自 GNU Unifont 17.0.03 子集，版权及 OFL 1.1 许可见 [fonts](Board2_1Piano/fonts/)。测试使用的 cJSON 许可及来源见 `Board2_1Tests/vendor/`。本项目其余代码与模型的开源许可证尚未指定。
