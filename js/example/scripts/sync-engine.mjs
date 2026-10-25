// 把 .NET WASM 引擎从 papersharp 包拷进站点 public/，供 vite 原样搬运。
// 站点构建链里没有 dotnet：引擎随 npm 包 papersharp 分发，装在 node_modules/papersharp/wasm/_framework。
// 中文字体不用同步：那份 Noto SC 子集已经嵌在 PaperSharp.wasm 的内嵌资源里，引擎自带兜底。
// 本地若在改引擎本体、想让站点直接吃仓内副本，设 PAPERSHARP_PKG_DIR=js/papersharp。
// 可用 PAPERSHARP_ENGINE_DIR 直接指向某个 _framework 目录（跳过包解析）。

import { cpSync, existsSync, mkdirSync, readdirSync, realpathSync, rmSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const siteRoot = resolve(here, "..");
const publicDir = join(siteRoot, "public");

const pkgDir = findPackageDir();
const engineSrc = process.env.PAPERSHARP_ENGINE_DIR
  ? resolve(process.env.PAPERSHARP_ENGINE_DIR)
  : join(pkgDir, "wasm", "_framework");

if (!existsSync(engineSrc)) {
  console.error(
    [
      `找不到 WASM 引擎：${engineSrc}`,
      "",
      "引擎由 npm 包 papersharp 提供。请先在 js/ 目录执行：",
      "  1) npm install",
      "  2) 重新执行 npm run dev",
      "",
      "或设 PAPERSHARP_ENGINE_DIR 指向已有的 _framework 目录。",
    ].join("\n"),
  );
  process.exit(1);
}

copyEngine(engineSrc, join(publicDir, "_framework"));
rmSync(join(publicDir, "fonts"), { recursive: true, force: true });
copySamples();

console.log(`引擎 → public/_framework（${human(sizeOf(join(publicDir, "_framework")))}）`);

/** 站点大样例（挑战页 22 KB）不入库两份：唯一真源在 src/PaperSharpSamples/assets，构建前拷进 public/samples。 */
function copySamples() {
  const assets = resolve(siteRoot, "..", "..", "src", "PaperSharpSamples", "assets");
  const to = join(publicDir, "samples");
  mkdirSync(to, { recursive: true });
  for (const name of ["challenge.html"]) {
    const src = join(assets, name);
    if (!existsSync(src)) {
      console.error(`找不到样例真源：${src}（站点从 npm 包安装时没有仓内 assets，样例需另行放到 public/samples/）`);
      process.exit(1);
    }
    cpSync(src, join(to, name));
  }
}

/** .pdb / *.map 只服务 DevTools 源码映射，站点不带；
 *  dotnet.native.js.symbols 必须保留 —— mono 无条件下载它，缺文件运行时起不来。 */
function copyEngine(from, to) {
  rmSync(to, { recursive: true, force: true });
  mkdirSync(to, { recursive: true });
  for (const entry of readdirSync(from, { withFileTypes: true })) {
    if (!entry.isDirectory() && /\.(pdb|map)$/.test(entry.name)) continue;
    cpSync(join(from, entry.name), join(to, entry.name), { recursive: entry.isDirectory() });
  }
}

function findPackageDir() {
  if (process.env.PAPERSHARP_PKG_DIR) return resolve(process.env.PAPERSHARP_PKG_DIR);
  let dir = siteRoot;
  for (;;) {
    const cand = join(dir, "node_modules", "papersharp");
    if (existsSync(cand)) return realpathSync(cand);
    const up = dirname(dir);
    if (up === dir) return join(siteRoot, "..", "papersharp");
    dir = up;
  }
}

function sizeOf(path) {
  let total = 0;
  for (const entry of readdirSync(path, { withFileTypes: true })) {
    const p = join(path, entry.name);
    total += entry.isDirectory() ? sizeOf(p) : statSync(p).size;
  }
  return total;
}

function human(bytes) {
  return bytes > 1048576 ? (bytes / 1048576).toFixed(1) + " MB" : (bytes / 1024).toFixed(0) + " KB";
}
