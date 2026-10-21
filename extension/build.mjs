// 同步 PaperSharp 的 wasm 产物与中文字体进扩展目录：node extension/build.mjs [--publish]
//
//   --publish  先跑 dotnet publish（Release）再拷贝；否则复用现有产物
// extension/papersharp/wasm 与 extension/fonts 是构建输入，已进 .gitignore（与 js/papersharp/wasm 同策略）。

import { cpSync, existsSync, mkdirSync, readdirSync, rmSync, statSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join, relative } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..");
const withPublish = process.argv.includes("--publish");

const bundle = join(repo, "src/PaperSharp.Wasm/bin/Release/net8.0/browser-wasm/AppBundle/_framework");
const fontsSrc = join(repo, "src/PaperSharpSamples/assets/fonts");

if (withPublish) {
  const args = ["publish", join(repo, "src/PaperSharp.Wasm/PaperSharp.Wasm.csproj"), "-c", "Release", "-v", "q", "-nologo"];
  console.log(">", "dotnet", args.join(" "));
  const r = spawnSync("dotnet", args, { stdio: "inherit", cwd: repo });
  if (r.status !== 0) { console.error("dotnet publish 失败"); process.exit(r.status ?? 1); }
}

if (!existsSync(bundle)) {
  console.error(`找不到 wasm 产物：${bundle}\n先跑：node extension/build.mjs --publish`);
  process.exit(1);
}

const wasmDest = join(here, "papersharp/wasm/_framework");
rmSync(wasmDest, { recursive: true, force: true });
copyTree(bundle, wasmDest);

const fontsDest = join(here, "fonts");
rmSync(fontsDest, { recursive: true, force: true });
mkdirSync(fontsDest, { recursive: true });
for (const f of ["YaheiSub-Regular.ttf", "YaheiSub-Bold.ttf"]) {
  const src = join(fontsSrc, f);
  if (!existsSync(src)) { console.error(`缺字体文件：${src}（先跑 src/PaperSharpSamples/tools/subset-font.py）`); process.exit(1); }
  cpSync(src, join(fontsDest, f));
}

console.log(`wasm → ${relative(repo, wasmDest)}（${human(sizeOf(wasmDest))}）`);
console.log(`字体 → ${relative(repo, fontsDest)}（${human(sizeOf(fontsDest))}）`);
console.log("装载：chrome://extensions → 开发者模式 → 加载已解压的扩展程序 → 选 extension 目录");

/** .pdb / *.js.map 只服务调试（DevTools 源码映射），扩展里不带；
 *  dotnet.native.js.symbols 必须保留 —— mono 运行时会无条件下载它，缺文件直接起不来（实测报 ENOENT 500）。 */
function copyTree(from, to) {
  mkdirSync(to, { recursive: true });
  for (const entry of readdirSync(from, { withFileTypes: true })) {
    if (!entry.isDirectory() && /\.(pdb|map)$/.test(entry.name)) continue;
    const src = join(from, entry.name);
    const dst = join(to, entry.name);
    if (entry.isDirectory()) copyTree(src, dst);
    else cpSync(src, dst);
  }
}

function sizeOf(path) {
  let total = 0;
  const info = statSync(path);
  if (!info.isDirectory()) return info.size;
  for (const entry of readdirSync(path, { withFileTypes: true })) {
    total += entry.isDirectory() ? sizeOf(join(path, entry.name)) : statSync(join(path, entry.name)).size;
  }
  return total;
}

function human(bytes) { return `${(bytes / 1024 / 1024).toFixed(1)} MB`; }
