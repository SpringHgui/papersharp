// 离线基准：把 e2e --dump 落盘的真实快照拿来分段计时，定位「引擎慢」到底慢在哪一类内容。
//   node extension/test/bench-snapshot.mjs [out/snapshot.html] [--author-css]
// 变体：整页 / 去掉内联 svg / 去掉文本 / 只留前 40 个元素 / scale 1 vs 2。
// 默认按扩展真实参数跑（precomputed 展平快照，见 extension/offscreen.js）；
//   --author-css 关掉它，量的是作者手写 CSS 那条 AngleSharp 全量层叠路（O(n^2.5)，慢一个量级）。
// 首行含 mono 冷启动，热态要看后面几行。

import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { renderRaw, installSubsetFont } from "../papersharp/runtime.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "out");
mkdirSync(out, { recursive: true });
const precomputed = !process.argv.includes("--author-css");
const htmlPath = process.argv[2]?.startsWith("--") ? join(out, "snapshot.html") : (process.argv[2] ? join(here, process.argv[2]) : join(out, "snapshot.html"));
const html = readFileSync(htmlPath, "utf8");
const css = readFileSync(join(dirname(htmlPath), "snapshot.css"), "utf8");

await installSubsetFont(
  readFileSync(join(here, "..", "fonts", "YaheiSub-Regular.ttf")),
  readFileSync(join(here, "..", "fonts", "YaheiSub-Bold.ttf")),
);

const stripSvg = (h) => h.replace(/<svg[\s\S]*?<\/svg>/g, "");
const stripText = (h) => h.replace(/>([^<>]+)</g, "><");            // 只留标签，去掉元素间文本
const firstTags = (h, n) => {
  const parts = h.split(/(?=<)/);
  return parts.slice(0, n).join("");
};
const count = (h, re) => (h.match(re) || []).length;

const cases = [
  ["整页", html],
  ["去掉 <svg>", stripSvg(html)],
  ["去掉文本", stripText(html)],
  ["前 200 个节点", firstTags(html, 200)],
  ["前 60 个节点", firstTags(html, 60)],
];

const t0all = Date.now();
const rows = [];
for (const [name, h] of cases) {
  const t0 = Date.now();
  const bytes = await renderRaw({ html: h, css, format: "png", width: 1406, scale: 1, precomputed });
  rows.push([name, Date.now() - t0, `${(bytes.length / 1024) | 0} KB`, count(h, /<svg/g), count(h, /<[a-z]/gi)]);
}
const tScale = Date.now();
await renderRaw({ html, css, format: "png", width: 1406, scale: 2, precomputed });
rows.push(["整页 scale=2", Date.now() - tScale, "", count(html, /<svg/g), count(html, /<[a-z]/gi)]);
const tPdf = Date.now();
await renderRaw({ html, css, format: "pdf", width: 1406, pageSize: "A4", precomputed });
rows.push(["整页 PDF", Date.now() - tPdf, "", "", ""]);

console.log("变体".padEnd(18), "耗时".padStart(7), "  输出".padEnd(9), "svg", "  标签数");
for (const r of rows) console.log(String(r[0]).padEnd(18), String(r[1] + "ms").padStart(7), String(r[2]).padEnd(9), r[3], "   ", r[4]);
writeFileSync(join(out, "bench.txt"), JSON.stringify(rows, null, 1));
console.log("总计", ((Date.now() - t0all) / 1000).toFixed(1) + "s");
process.exit(0);
