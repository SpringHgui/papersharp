// 把两轮 sweep 汇总按样例并排，输出 before/after 四列（漂移盒数 / 最坏 / 均值 / 全绿盒）。
//   node tools/compare.mjs <before.txt> <after.txt>
import { readFileSync } from "node:fs";
const parse = (f) => {
  const m = new Map();
  for (const line of readFileSync(f, "utf8").split("\n")) {
    const g = line.match(/^(\S+) @ \d+px .*? 引擎 (\d+) 盒.*?ok (\d+) .*?漂移 (\d+) .*?最坏 ([\d.]+)px .*?均值 ([\d.]+)px/);
    if (g) m.set(g[1], { boxes: +g[2], ok: +g[3], drift: +g[4], worst: +g[5], mean: +g[6] });
  }
  return m;
};
const [a, b] = [parse(process.argv[2]), parse(process.argv[3])];
const pad = (s, n) => String(s).padStart(n);
console.log("样例          盒数   漂移(前→后)      最坏px(前→后)     均值px(前→后)");
for (const k of [...b.keys()].sort()) {
  const p = a.get(k), n = b.get(k);
  if (!p) { console.log(`${k.padEnd(13)} ${pad(n.boxes, 5)}   —— 本轮新增 ——`); continue; }
  console.log(`${k.padEnd(13)} ${pad(n.boxes, 5)}  ${pad(p.drift, 5)}→${pad(n.drift, 5)}   ${pad(p.worst.toFixed(1), 7)}→${pad(n.worst.toFixed(1), 7)}   ${pad(p.mean.toFixed(1), 6)}→${pad(n.mean.toFixed(1), 6)}`);
}
const sum = (m, f) => [...m.values()].reduce((s, v) => s + f(v), 0);
console.log("\n合计漂移盒 " + sum(a, v => v.drift) + " → " + sum(b, v => v.drift)
  + " · 全绿样例 " + [...a.values()].filter(v => v.drift === 0).length + " → " + [...b.values()].filter(v => v.drift === 0).length);
