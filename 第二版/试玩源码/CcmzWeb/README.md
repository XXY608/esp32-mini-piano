# 网页 CCMZ 转换器

试玩页在后台线程中调用与配套软件共享的 `CcmzImporter` 和 `SongPayloadFactory`。浏览器读取用户选择的文件，转换后交给原有模拟琴校验入库。文件内容无需提交到服务器。

使用 .NET 10 SDK 从仓库根目录构建：

```powershell
dotnet publish '第二版/试玩源码/CcmzWeb/CcmzWeb.csproj' -c Release
```

从终端报告的发布目录取 `wwwroot/_framework`，整体替换 `docs/try/full/ccmz-runtime/_framework`。GitHub Pages 使用原始 JS 和 WASM，省略 `.gz`、`.br`、`.map` 和 `.pdb`。`dotnet.js` 内嵌依赖的文件名和哈希，同一批输出一起更新。运行时来自 NuGet 的 `Microsoft.NETCore.App.Runtime.Mono.browser-wasm`，发布时附带该包的 `LICENSE.TXT` 和 `THIRD-PARTY-NOTICES.TXT`。
