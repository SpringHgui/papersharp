// 临时探针：(1) Chrome 把 generic monospace / 各 font-family 解析成哪张真实平台字体；
// (2) 该行内盒的 height 随 font-size 怎么变（用来反推 face 的 ascent/descent/normalLineHeight）。
// 用法：node tools/_platfont2.mjs
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const PORT = 9537;
const profile = mkdtempSync(join(tmpdir(), "ps-plat2-"));
const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });

const sizes = [];
for (let s = 10; s <= 24; s++) sizes.push(s);

function span(fam, px, id) {
  return `<span id="${id}" style="font-family:${fam};font-size:${px}px">Hxpg 皮</span>`;
}

let rows = "";
let n = 0;
const cases = [];
for (const fam of ["monospace", "Consolas, monospace", "sans-serif"]) {
  for (const px of sizes) {
    const id = `c${n++}`;
    cases.push([id, fam, px]);
    rows += span(fam, px, id) + "<br>";
  }
}

const doc = join(profile, "p.html");
writeFileSync(doc, `<!doctype html><meta charset=utf-8><body style="margin:0;font-size:16px">
<p style="margin:0">${rows}</p>
<p id=monoDefault style="margin:0"><code>x</code></p>
<p id=monoDefault2 style="margin:0;font-size:22px"><code style="font-size:inherit">x</code></p>
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
await send("DOM.enable", {}, sessionId);
await send("CSS.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1000, height: 2000, deviceScaleFactor: 1, mobile: false }, sessionId);
for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result.value === "complete") break; await sleep(200); }

// 行内盒高度（用 Range 拿到 inline box 的度量）
const meas = cases.map(c => c[0]);
const expr = `(${JSON.stringify(meas)}).map(id=>{const e=document.getElementById(id);const r=e.getBoundingClientRect();return [id,+r.height.toFixed(4),+r.width.toFixed(4)];})`;
const res = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
const byId = new Map(res.result.value.map(r => [r[0], r]));
const { root } = await send("DOM.getDocument", { depth: -1 }, sessionId);
for (const [id, fam, px] of cases) {
  const { nodeId } = await send("DOM.querySelector", { nodeId: root.nodeId, selector: "#" + id }, sessionId);
  const r = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
  const h = byId.get(id);
  console.log(`${fam.padEnd(22)} ${String(px).padStart(2)}px  h=${h[1]}  w=${h[2]}  fonts=${(r.fonts || []).map(f => `${f.familyName}(${f.postscriptName})x${f.glyphCount}`).join(",")}`);
}
for (const sel of ["#monoDefault", "#monoDefault2"]) {
  const { nodeId } = await send("DOM.querySelector", { nodeId: root.nodeId, selector: sel }, sessionId);
  const r = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
  const e = await send("Runtime.evaluate", { expression: `(()=>{const c=document.querySelector('${sel} code');const q=c.getBoundingClientRect();const p=c.parentElement.getBoundingClientRect();return [c.offsetHeight,+q.height.toFixed(4),+q.width.toFixed(4),+p.height.toFixed(4),getComputedStyle(c).fontSize];})()`, returnByValue: true }, sessionId);
  console.log(`${sel} ${JSON.stringify(e.result.value)} fonts=${(r.fonts || []).map(f => `${f.familyName}(${f.postscriptName})x${f.glyphCount}`).join(",")}`);
}
ws.close(); proc.kill();
