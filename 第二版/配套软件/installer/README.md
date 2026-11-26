# 独立安装版

通过 `Build-Release.ps1 -Installer` 执行既有 Python、C#、WPF 检查、自包含发布及安装包构建。正式安装包在工作区 `work/bin/Release/电子琴安装程序.exe`，不在根目录添加测试入口。

构建机需要现有 `work/lyrics-alignment` 的固定依赖、TIFA 源码和模型，以及 `work/dotnet-sdk`。安装到其他电脑时不需要这些开发目录、Codex、系统 Python、.NET SDK 或 GPT。安装包仅带运行库、模型、辅助程序和说明，不带用户曲谱、录音、任务、凭据或训练数据。

构建工具为官方 Inno Setup 6.7.3，安装到工作区 `work/build-tools/InnoSetup`。来源：https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3 。下载后验证 Authenticode 状态 Valid、签发者 Pyrsys B.V.；工具本身不随产品安装。中文语言文件取自 Inno 官方源码。打包路径保持较短，避免模型哈希文件名和包内许可证超过编译工具的路径限制。

私有 Python 使用相对 `python312._pth`，只加载安装目录标准库、固定依赖和配词源码，不读取开发机的 Python 注册表、PYTHONHOME、PYTHONPATH 或用户包。Windows C++ release CRT 随应用放在私有 Python 旁，不修改系统版本。`verify_runtime.py` 检查实际模块及 CRT 路径，并离线加载 Demucs。

AudioLyricsAligner 为整个子进程树设置 `MPLBACKEND=Agg`：TIFA 的 `--stat` 在预测结束后保存诊断图，默认 Tk 后端会依赖开发机 Tcl 并导致独立安装失败。保留统计图，使用后台 PNG 渲染；预检实际绘图并确认未加载 tkinter。参见 Matplotlib 官方后端说明：https://matplotlib.org/stable/users/explain/figure/backends.html 。

`Test-Installer.ps1` 在工作区隔离目录实测静默安装、无外部 Python 的模型加载、覆盖更新、卸载与用户 TXT／绑定保留。测试结束卸载临时安装；测试证据仍在 `work/installer-smoke-*`。这是本机隔离验证，不能宣称所有其他 Windows 电脑均已测试。

本次实测目录为 `work/installer-service`。传入 `-InstalledDirectory` 可在已经完成真实歌曲测试的工作区临时安装上继续生命周期验证，避免重复首次安装；该参数拒绝工作区work以外的目录。测试启动自己创建的WPF进程，先保存、结束后恢复原有设置，不结束用户播放器。

运行时联网查询 LRCLIB、公开音频和参考谱网站；在本机执行 Demucs、TIFA 和谱面对齐，无 GPT API、提示词修正或逐曲人工修改步骤。当前算法仍为14，准确率不因打包提高，原有诊断与保护保持。
