// 临时取证脚本：按漂移签名筛盒并打印量级分布与样本。
//   node tools/tri.mjs <样例> [签名过滤，如 "dx,dy-,dw-"]
import { readFileSync } from "node:fs";
import { join } from "node:path";
const NAME = process.argv[2] || "m1";
const WANT = process.argv[3] || "";
const d = JSON.parse(readFileSync(join(process.env.TEMP, "drift", NAME + ".json"), "utf8"));
const b = (v) => { const a = Math.abs(v); return a <= 2 ? "<=2" : a <= 8 ? "<=8" : a <= 30 ? "<=30" : a <= 120 ? "<=120" : "big"; };
const sig = (r) => ["dx", "dy", "dw", "dh"].map((k) => (Math.abs(r[k]) > 0.5 ? k + (r[k] > 0 ? "+" : "-") : "")).filter(Boolean).join(" ") || "none";
const rows = d.rows.filter((r) => r.kind === "drift");
const groups = new Map();
for (const r of rows) { const s = sig(r); if (!groups.has(s)) groups.set(s, []); groups.get(s).push(r); }
const sorted = [...groups.entries()].sort((a, b2) => b2[1].length - a[1].length);
if (!WANT) {
  for (const [s, g] of sorted.slice(0, 8)) {
    console.log(`\n━━ ${s}  ${g.length} 盒`);
    for (const k of ["dx", "dy", "dw", "dh"]) { const m = {}; for (const r of g) m[b(r[k])] = (m[b(r[k])] || 0) + 1; console.log("   " + k, JSON.stringify(m)); }
    for (const r of g.slice(0, 4)) console.log(`     ${r.path}  E(${r.e.x.toFixed(1)},${r.e.y.toFixed(1)}) ${r.e.w.toFixed(1)}×${r.e.h.toFixed(1)}  B(${r.b.x.toFixed(1)},${r.b.y.toFixed(1)}) ${r.b.w.toFixed(1)}×${r.b.h.toFixed(1)}`);
  }
} else {
  const g = rows.filter((r) => sig(r) === WANT);
  console.log(`${WANT} → ${g.length} 盒`);
  for (const r of g) console.log(`dx${r.dx.toFixed(1).padStart(8)} dy${r.dy.toFixed(1).padStart(8)} dw${r.dw.toFixed(1).padStart(8)} dh${r.dh.toFixed(1).padStart(7)}  ${r.path}  E(${r.e.x.toFixed(1)},${r.e.y.toFixed(1)}) ${r.e.w.toFixed(1)}×${r.e.h.toFixed(1)}  B(${r.b.x.toFixed(1)},${r.b.y.toFixed(1)}) ${r.b.w.toFixed(1)}×${r.b.h.toFixed(1)}`);
}
