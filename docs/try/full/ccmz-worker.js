import { dotnet } from "./ccmz-runtime/_framework/dotnet.js";

self.onmessage = async ({ data }) => {
  try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const result = exports.CcmzWeb.BrowserScore.Convert(new Uint8Array(data.bytes), data.title, data.id);
    self.postMessage(JSON.parse(result));
  } catch {
    self.postMessage({ error: "CCMZ 转换失败，请检查文件或稍后重试。" });
  }
};
