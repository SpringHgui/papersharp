// 临时：把 _probe-vmet 的对拍结果按书写顺序排成表（族 / 字号 / 引擎行盒高 / 浏览器行盒高 / 宽）。
import { execFileSync } from "node:child_process";
const NAME = process.argv[2] || "_probe-vmet";
const txt = execFileSync("node", ["tools/boxdiff.mjs", NAME, "--tol", "0", "--json"], { encoding: "utf8", maxBuffer: 1 << 28 });
const j = JSON.parse(txt.slice(txt.indexOf("{")));
const rows = new Map(j.rows.map(r => [r.path, r]));
const idx = p => Number(p.match(/\[(\d+)\]$/)[1]);
const spans = [...rows.keys()].filter(p => p.endsWith("/span[1]")).sort((a, b) => idx(a) - idx(b));
for (const p of spans) {
  const r = rows.get(p), pr = rows.get(p.replace("/span[1]", ""));
  console.log(`${idx(p)}\teng h=${r.e.h.toFixed(2)}\tbio h=${r.b.h.toFixed(2)}\tdh=${r.dh.toFixed(2)}\teng w=${r.e.w.toFixed(2)}\tbio w=${r.b.w.toFixed(2)}\tdw=${r.dw.toFixed(2)}\tblockH=${pr ? pr.e.h.toFixed(2) + "/" + pr.b.h.toFixed(2) : "?"}`);
}
