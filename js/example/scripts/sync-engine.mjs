// 把 .NET WASM 引擎与中文字体子集从 papersharp 包拷进站点 public/，供 vite 原样搬运。
// 站点构建链里没有 dotnet：引擎是 npm 依赖的一部分（js/papersharp/wasm/_framework）。
// 本地开发若包目录为空，先跑：
//   dotnet publish src/PaperSharp.Wasm/PaperSharp.Wasm.csproj -c Release
//   node js/papersharp/build.mjs
// 可用 PAPERSHARP_ENGINE_DIR 直接指向某个 _framework 目录（跳过包解析）。

import { cpSync, existsSync, mkdirSync, readdirSync, realpathSync, rmSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const siteRoot = resolve(here, "..");
const publicDir = join(siteRoot, "public");

const FONTS = ["NotoSansSCSub-Regular.ttf", "NotoSansSCSub-Bold.ttf"];

const pkgDir = findPackageDir();
const engineSrc = process.env.PAPERSHARP_ENGINE_DIR
  ? resolve(process.env.PAPERSHARP_ENGINE_DIR)
  : join(pkgDir, "wasm", "_framework");

if (!existsSync(engineSrc)) {
  console.error(
    [
      `找不到 WASM 引擎：${engineSrc}`,
      "",
      "papersharp 包里的 wasm/ 目录是构建产物（未入库）。本机请先跑：",
      "  1) dotnet publish src/PaperSharp.Wasm/PaperSharp.Wasm.csproj -c Release",
      "  2) node js/papersharp/build.mjs",
      "  3) 重新执行 npm run dev",
      "",
      "或设 PAPERSHARP_ENGINE_DIR 指向已有的 _framework 目录。",
    ].join("\n"),
  );
  process.exit(1);
}

copyEngine(engineSrc, join(publicDir, "_framework"));
const fontsSrc = findFontsDir();
if (fontsSrc) copyFonts(fontsSrc, join(publicDir, "fonts"));
else console.warn("警告：未找到 Noto 中文子集字体，Playground 里的中文会出豆腐块。");

console.log(
  `引擎 → public/_framework（${human(sizeOf(join(publicDir, "_framework")))}）` +
    (fontsSrc ? `，字体 → public/fonts（${human(sizeOf(join(publicDir, "fonts"))) }）` : ""),
);

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

function copyFonts(from, to) {
  rmSync(to, { recursive: true, force: true });
  mkdirSync(to, { recursive: true });
  for (const f of FONTS) cpSync(join(from, f), join(to, f));
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

/** 优先包内字体（将来随 npm 分发），否则退回仓内样例资源。 */
function findFontsDir() {
  const candidates = [
    join(pkgDir, "wasm", "fonts"),
    resolve(siteRoot, "..", "..", "src", "PaperSharpSamples", "assets", "fonts"),
  ];
  return candidates.find((dir) => FONTS.every((f) => existsSync(join(dir, f)))) ?? null;
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
