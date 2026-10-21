// 对拍 triage：列出某样例最坏的 N 个漂移盒（引擎 vs 浏览器），带四分量与两侧矩形。
// 用法：node tools/boxtriage.mjs <sample> [N]   （需先有 output/boxes-<sample>.{json,html}）
// 浏览器对拍偶发「Execution context destroyed」——多因上一次无头 Edge 的僵尸进程仍占着固定 9411，
// 新实例换了端口而 pollJson 连到了僵尸。这里每次用随机高端口并重试，规避该噪声。
import { execFileSync } from "node:child_process";
const NAME = process.argv[2], N = +(process.argv[3] || 10);
function run() {
  const port = 10000 + Math.floor(Math.random() * 50000);
  return execFileSync("node", ["tools/boxdiff.mjs", NAME, "--tol", "1", "--json", "--port", String(port)],
    { encoding: "utf8", maxBuffer: 1 << 28 });
}
let txt, last;
for (let t = 0; t < 4; t++) { try { txt = run(); break; } catch (e) { last = e; } }
if (!txt) { console.log(`${NAME}: 对拍失败（重试 4 次）${(last?.message || "").split("\n")[0]}`); process.exit(0); }
const j = JSON.parse(txt.slice(txt.indexOf("{")));
const f = (n) => (n >= 0 ? "+" : "") + n.toFixed(1);
console.log(`${NAME}: 漂移 ${j.summary.drift} / 引擎 ${j.summary.engineOnly} 独 / 浏览器 ${j.summary.browserOnly} 独 / 最坏 ${j.summary.worst}px`);
for (const r of j.rows.slice(0, N)) {
  if (r.kind !== "drift") { console.log(`  ${r.kind.padEnd(6)} ${r.path}`); continue; }
  console.log(`  max ${String(r.max.toFixed(1)).padStart(6)}  dx${f(r.dx)} dy${f(r.dy)} dw${f(r.dw)} dh${f(r.dh)}  ${r.path}`);
  console.log(`        引擎 ${r.e.w.toFixed(1)}×${r.e.h.toFixed(1)} @(${r.e.x.toFixed(1)},${r.e.y.toFixed(1)})  浏览器 ${r.b.w.toFixed(1)}×${r.b.h.toFixed(1)} @(${r.b.x.toFixed(1)},${r.b.y.toFixed(1)})`);
}
