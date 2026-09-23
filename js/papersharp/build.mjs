// 把 dotnet publish 产物拷进包目录：node js/papersharp/build.mjs
// （前置：dotnet publish src/PaperSharp.Wasm/PaperSharp.Wasm.csproj -c Release）
import { cpSync, existsSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const bundle = join(here, "../../src/PaperSharp.Wasm/bin/Release/net8.0/browser-wasm/AppBundle/_framework");
if (!existsSync(bundle)) {
  console.error("找不到 wasm 产物，请先执行: dotnet publish src/PaperSharp.Wasm/PaperSharp.Wasm.csproj -c Release");
  process.exit(1);
}
const dest = join(here, "wasm/_framework");
rmSync(dest, { recursive: true, force: true });
cpSync(bundle, dest, { recursive: true });
console.log("已拷贝 _framework →", dest);
