// 临时：每个样例在参考浏览器里**实际命中的 face**是什么（决定引擎该打包哪张字体）。
// 用法：node tools/_faces.mjs m1 inline table m3
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const names = process.argv.slice(2).filter(a => !a.startsWith("--"));
const PORT = Number(process.argv.find(a => a.startsWith("--port="))?.split("=")[1] || 9545);

const outDirs = ["bin/Release/net10.0", "bin/Release/net10.0"].map(d => join(root, "src", "PaperSharpSamples", d, "output"));
const pick = f => outDirs.map(d => join(d, f)).find(existsSync);

const profile = mkdtempSync(join(tmpdir(), "ps-faces-"));
const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let v; for (let i = 0; i < 60; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", ev => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise(r => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sid) => { const id = ++seq; ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };
try { await send("Runtime.enable", {}); } catch { /* 有些构建不给 flat session 开 Runtime */ }

for (const NAME of names) {
  const docPath = pick(`boxes-${NAME}.html`);
  if (!docPath) { console.log(`${NAME}: 缺产物`); continue; }
  const { targetId } = await send("Target.createTarget", { url: "file:///" + docPath.replace(/\\/g, "/") });
  const sessionId = (await send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
  await send("Page.enable", {}, sessionId); await send("DOM.enable", {}, sessionId); await send("CSS.enable", {}, sessionId);
  await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 900, deviceScaleFactor: 1, mobile: false }, sessionId);
  for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result.value === "complete") break; await sleep(200); }
  // 取若干带直接文本的元素：body 本身 + 前若干个 p/td/span/code
  const ev = await send("Runtime.evaluate", {
    expression: `(()=>{const out=[];const seen=new Set();const push=(el,tag)=>{if(!el||seen.has(el))return;seen.add(el);
      for(const n of el.childNodes) if(n.nodeType===3&&n.nodeValue.trim()){out.push([tag, getComputedStyle(el).fontFamily.slice(0,40), getComputedStyle(el).fontSize]);return;}};
      push(document.body,'body');
      for(const sel of ['h1','p','td','th','li','code','span']){const els=[...document.querySelectorAll(sel)];for(const e of els){push(e,sel);if(out.length>7)break;}}
      return out.slice(0,9);})()`,
    returnByValue: true
  }, sessionId);
  const cases = ev.result.value;
  const { root: droot } = await send("DOM.getDocument", { depth: -1 }, sessionId);
  const fams = new Map();
  for (const [sel] of cases) {
    const { nodeId } = await send("DOM.querySelector", { nodeId: droot.nodeId, selector: sel }, sessionId);
    if (!nodeId) continue;
    const f = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
    for (const x of (f.fonts || [])) fams.set(x.familyName, (fams.get(x.familyName) || 0) + x.glyphCount);
  }
  console.log(`${NAME.padEnd(12)} ${cases.length ? cases[0][1] + " " + cases[0][2] : "?"}  ->  ${[...fams].map(([k, n]) => `${k}×${n}`).join(", ")}`);
  await send("Target.closeTarget", { targetId });
}
ws.close(); proc.kill();
