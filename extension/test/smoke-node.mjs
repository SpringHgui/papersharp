// 快速冒烟（不起浏览器）：验证 extension/papersharp 里的 wasm 包能被 node 拉起、
// 子集字体兜底生效、6 种格式都能出字节。真实网页链路在 e2e-chrome.mjs 里跑。
//
//   node extension/test/smoke-node.mjs        # 产物写 extension/test/out/smoke.*

import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { renderRaw, installSubsetFont, info } from "../papersharp/runtime.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "out");
mkdirSync(out, { recursive: true });

const regular = readFileSync(join(here, "..", "fonts", "YaheiSub-Regular.ttf"));
const bold = readFileSync(join(here, "..", "fonts", "YaheiSub-Bold.ttf"));
await installSubsetFont(regular, bold);
console.log("runtime:", JSON.stringify(await info()));

// 故意用引擎里不存在的字体族：中文若出豆腐块 = 子集兜底没生效
const html = `<div class="page">
  <h1>加油站日报 · 冒烟</h1>
  <p class="weird">未知字体族也要正常出中文：地磅收粮、车牌识别、水分扣杂。</p>
  <ol class="counters"><li>PDF 矢量分页</li><li>PNG 长图</li><li>SVG 可选中</li></ol>
  <table><thead><tr><th>格式</th><th>说明</th></tr></thead>
  <tbody><tr><td>JPEG / WebP</td><td>带质量参数</td></tr></tbody></table>
  <p class="grad">渐变文字</p></div>`;
const css = `body{font-family:"Totally Made-Up Font",sans-serif;margin:0}
  .page{padding:24px;background:#f8fafc} h1{font-size:28px;color:#1e293b}
  .weird{font-family:"Nope Font";font-size:18px;color:#334155}
  table{border-collapse:collapse} th,td{border:1px solid #94a3b8;padding:6px 12px;font-size:14px}
  ol.counters{counter-reset:item} ol.counters li{counter-increment:item}
  ol.counters li::before{content:"第 " counter(item) " 步：";color:#b91c1c;font-weight:700}
  .grad{font-size:24px;font-weight:700;background:linear-gradient(90deg,#f472b6,#38bdf8);background-clip:text;color:transparent}`;

for (const [format, extra] of [
  ["png", { scale: 2 }], ["jpeg", { scale: 2, quality: 88 }], ["webp", { scale: 2, quality: 88 }],
  ["svg", {}], ["pdf", { pageSize: "A4" }], ["ico", { icoSize: 256, scale: 1 }],
]) {
  const bytes = await renderRaw({ html, css, format, width: 760, animationTime: 1.0, ...extra });
  const ext = format === "jpeg" ? "jpg" : format;
  writeFileSync(join(out, `smoke.${ext}`), bytes);
  console.log(`  ${format.padEnd(5)} ${String(bytes.length).padStart(9)} B`);
}
console.log("冒烟通过，产物在", out);
process.exit(0);
