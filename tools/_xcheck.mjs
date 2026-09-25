// 临时：定位 _platfont2 与 _lhnorm 的 span 高度差(21 vs 24 @16px)来源
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const PORT = 9541;
const prof = mkdtempSync(join(tmpdir(), "ps-x-"));
const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${prof}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });
const doc = join(prof, "p.html");
writeFileSync(doc, `<!doctype html><meta charset=utf-8><style>
body{font-family:"Microsoft YaHei",sans-serif;margin:0;font-size:16px}
p{margin:0;border:0}
</style><body>
<p><span id=s1 style="font-size:16px">abc</span></p>
<p><span id=s2 style="font-size:16px">皮</span></p>
<p><span id=s3 style="font-size:16px">abc 皮</span></p>
<p id=q1><span style="font-size:16px">abc 皮</span></p>
<p id=q2 style="font-size:16px">abc 皮</p>
<div id=q3 style="font-size:16px">abc 皮</div>
<p><span id=s4 style="font-family:sans-serif;font-size:16px">abc 皮</span></p>
</body>`);

const sleep = ms => new Promise(r => setTimeout(r, ms));
let v; for (let i = 0; i < 60; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", ev => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise(r => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sid) => { const id = ++seq; ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };
const { targetId } = await send("Target.createTarget", { url: "file:///" + doc.replace(/\\/g, "/") });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId); await send("DOM.enable", {}, sessionId); await send("CSS.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1000, height: 800, deviceScaleFactor: 1, mobile: false }, sessionId);
for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result.value === "complete") break; await sleep(200); }
const ids = ["s1", "s2", "s3", "s4", "q1", "q2", "q3"];
const expr = "(()=>{const o=[];" + ids.map(i => `o.push(["${i}",+document.getElementById("${i}").getBoundingClientRect().height.toFixed(4)]);`).join("") + "return o})()";
const r = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
console.log(r.result.value.map(x => x.join(" = ")).join("   "));
const { root } = await send("DOM.getDocument", { depth: -1 }, sessionId);
for (const id of ["s3", "s4", "q1"]) {
  const { nodeId } = await send("DOM.querySelector", { nodeId: root.nodeId, selector: "#" + id }, sessionId);
  const f = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
  console.log(id, (f.fonts || []).map(x => x.familyName + "×" + x.glyphCount).join(", "));
}
ws.close(); proc.kill();
