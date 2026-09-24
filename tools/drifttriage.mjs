// 漂移取证聚类：把 DRIFT/<样例>.json 里逐盒的 Δ 按「主导分量 + 符号 + 量级桶」归族，
// 一眼看出「一条根因影响了多少盒、长什么样」，而不是被上千行明细淹掉。
//   node tools/drifttriage.mjs [样例...]
import { readFileSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
const DRIFT = join(tmpdir(), "drift");

const files = process.argv.length > 2
  ? process.argv.slice(2).map((s) => `${DRIFT}/${s}.json`)
  : readdirSync("DRIFT").filter((f) => f.endsWith(".json")).map((f) => `${DRIFT}/${f}`);

const bucket = (v) => { const a = Math.abs(v); return a <= 0.5 ? "0" : a <= 2 ? "~1" : a <= 8 ? "~5" : a <= 30 ? "~15" : a <= 120 ? "~60" : "big"; };

for (const f of files) {
  const d = JSON.parse(readFileSync(f, "utf8"));
  const name = f.split("/").pop().replace(".json", "");
  const s = d.summary;
  console.log(`\n━━ ${name}  漂移 ${s.drift} / 单边 ${s.engineOnly}+${s.browserOnly} / 最坏 ${s.worst.toFixed(1)} / 均值 ${s.mean.toFixed(1)}`);
  if (!d.rows.length) { console.log("   （全绿）"); continue; }

  const groups = new Map();
  for (const r of d.rows) {
    if (r.kind !== "drift") continue;
    const parts = [];
    if (Math.abs(r.dx) > 0.5) parts.push(`dx${r.dx > 0 ? "+" : "-"}${bucket(r.dx)}`);
    if (Math.abs(r.dy) > 0.5) parts.push(`dy${r.dy > 0 ? "+" : "-"}${bucket(r.dy)}`);
    if (Math.abs(r.dw) > 0.5) parts.push(`dw${r.dw > 0 ? "+" : "-"}${bucket(r.dw)}`);
    if (Math.abs(r.dh) > 0.5) parts.push(`dh${r.dh > 0 ? "+" : "-"}${bucket(r.dh)}`);
    const key = parts.join(" ") || "none";
    if (!groups.has(key)) groups.set(key, { n: 0, worst: 0, ex: [] });
    const g = groups.get(key);
    g.n++; g.worst = Math.max(g.worst, r.max);
    if (g.ex.length < 3) g.ex.push(r);
  }
  for (const [key, g] of [...groups].sort((a, b) => b[1].n - a[1].n).slice(0, 9)) {
    console.log(`   ${String(g.n).padStart(4)}×  ${key.padEnd(22)} 最坏 ${g.worst.toFixed(1).padStart(7)}`);
    for (const r of g.ex.slice(0, 2)) {
      const p = r.path.replace("body[1]/", "").replace(/(\/\w+\[\d+\])/g, "$1");
      console.log(`          ${p.length > 46 ? p.slice(0, 46) + "…" : p.padEnd(46)} e=(${r.e.x.toFixed(0)},${r.e.y.toFixed(0)}) ${r.e.w.toFixed(0)}×${r.e.h.toFixed(0)}  b=(${r.b.x.toFixed(0)},${r.b.y.toFixed(0)}) ${r.b.w.toFixed(0)}×${r.b.h.toFixed(0)}`);
    }
  }
  if (groups.size > 9) console.log(`   …另有 ${groups.size - 9} 个更小的族`);
  const orphans = d.rows.filter((r) => r.kind !== "drift");
  if (orphans.length) console.log(`   单边：${orphans.slice(0, 6).map((o) => `${o.kind}:${o.tag}`).join(" ")}${orphans.length > 6 ? ` …共 ${orphans.length}` : ""}`);
}
