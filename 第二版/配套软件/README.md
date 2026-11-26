# 配套软件源码

此目录包含 Windows 电子琴源码（项目版本 2.1）和 Android MAUI 项目、共用解析器、测试及辅助工具。`Mobile/src/HarmonicaPlayer.Mobile/HarmonicaPlayer.Mobile.csproj` 为 Android 工程；`HarmonicaPlayer.csproj` 为 Windows 工程。运行 Android 共用层测试：`dotnet run --project Mobile/tests/HarmonicaPlayer.Mobile.Core.Tests -c Release`。

构建需要相应 .NET SDK、Android MAUI 工作负载和 NuGet 依赖。完整安装包、音色库及自动歌词配词运行模型未包含，功能是否可用取决于相应外部依赖。构建脚本含原开发工作区路径与发布流程，迁移后需调整；不要直接用于覆盖本机正式程序。此仓库不包含签名密钥或私人 Wi-Fi 凭据。
