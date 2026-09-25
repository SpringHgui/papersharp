// 临时探针：把「normal 行高」的真值一次问清 —— 同一个字号下分别读
// computed line-height、块盒高、纯 Latin span、纯 CJK span、mono span 的内联盒高，
// 用来判定引擎 TextExtents/NormalLineHeight 该取哪张 face 的哪组度量。
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const PORT = 9539;
const profile = mkdtempSync(join(tmpdir(), "ps-lhnorm-"));
const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });

const sizes = ["10pt", "11pt", "22px", "13.3333px", "14.6667px", "16px", "14px", "15px"];
let rows = "";
const ids = [];
sizes.forEach((s, i) => {
  const id = `d${i}`; ids.push([id, s]);
  rows += `<div id="${id}" style="font-size:${s};line-height:normal;margin:0;border:0">
<span class=lat>abc</span>|<span class=cjk>布局</span>|<span class=mono>x</span></div>`;
});

const doc = join(profile, "p.html");
writeFileSync(doc, `<!doctype html><meta charset=utf-8><style>
body{font-family:"Microsoft YaHei",sans-serif;margin:0;font-size:16px}
.mono{font-family:monospace}
</style><body>${rows}
<div id=codecase style="font-size:14.6667px;margin:0"><code>x</code></div>
</body>`);

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
let v; for (let i = 0; i < 60; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sessionId) => { const id = ++seq; ws.send(JSON.stringify(sessionId ? { id, method, params, sessionId } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };

const { targetId } = await send("Target.createTarget", { url: "file:///" + doc.replace(/\\/g, "/") });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1000, height: 2000, deviceScaleFactor: 1, mobile: false }, sessionId);
for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result.value === "complete") break; await sleep(200); }

const expr = `(()=>{const out=[];
${ids.map(([id, s]) => `out.push(["${id}","${s}",getComputedStyle(document.getElementById("${id}")).lineHeight,+document.getElementById("${id}").getBoundingClientRect().height.toFixed(4),+document.querySelector("#${id} .lat").getBoundingClientRect().height.toFixed(4),+document.querySelector("#${id} .cjk").getBoundingClientRect().height.toFixed(4),+document.querySelector("#${id} .mono").getBoundingClientRect().height.toFixed(4),+document.querySelector("#${id} .mono").getBoundingClientRect().width.toFixed(4)]);`).join("")}
const c=document.querySelector("#codecase code");
out.push(["code","14.6667",getComputedStyle(c).lineHeight,+c.getBoundingClientRect().height.toFixed(4),getComputedStyle(c).fontSize,"","",+c.getBoundingClientRect().width.toFixed(4)]);
return out;})()`;
const res = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
console.log("id      font-size   computed-lh  blockH   latH  cjkH  monoH monoW  (fontSize for code)");
for (const r of res.result.value) console.log(r.map(x => String(x)).join("\t"));
ws.close(); proc.kill();
