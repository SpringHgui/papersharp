// 读 boxdiff 的 --json 输出，算每样例的「全盒平均偏差」等指标，输出一行 TSV。
// 用法：node tools/boxstats.mjs <sample>   （需先有 output/boxes-<sample>.{json,html}）
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
const NAME = process.argv[2];
let txt;
try { txt = execFileSync("node", ["tools/boxdiff.mjs", NAME, "--tol", "0", "--json"], { encoding: "utf8", maxBuffer: 1 << 28 }); }
catch (e) { console.log(`${NAME}\tERR\t${(e.message || "").split("\n")[0]}`); process.exit(0); }
const j = JSON.parse(txt.slice(txt.indexOf("{")));
const drift = j.rows.filter(r => r.kind === "drift");
const matched = j.summary.ok + j.summary.drift;
const sum = drift.reduce((s, r) => s + r.max, 0);
const maxes = drift.map(r => r.max).sort((a, b) => a - b);
const med = maxes.length ? maxes[Math.floor(maxes.length / 2)] : 0;
const over1 = drift.filter(r => r.max > 1).length;
const over05 = drift.filter(r => r.max > 0.5).length;
const meanAll = matched ? sum / matched : 0;
console.log([NAME, matched, j.summary.engineOnly + j.summary.browserOnly,
  meanAll.toFixed(3), med.toFixed(2), j.summary.worst.toFixed(2), over05, over1].join("\t"));
