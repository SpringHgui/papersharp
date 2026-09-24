// 临时：问浏览器「这段文字实际用了哪张平台字体」——用 CDP CSS.getPlatformFontsForNode。
// 用法：node tools/_platfont.mjs
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const PORT = 9533;
const profile = mkdtempSync(join(tmpdir(), "ps-plat-"));
const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });

const doc = join(profile, "p.html");
writeFileSync(doc, `<!doctype html><meta charset=utf-8><body>
<p id=a style="font-family:sans-serif">abc 皮</p>
<p id=b style="font-family:serif">abc 皮</p>
<p id=c style="font-family:monospace">abc 皮</p>
<p id=d style="font-family:sans-serif;font-size:15px">abc 皮</p></body>`);

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
await send("Emulation.setDeviceMetricsOverride", { width: 1000, height: 800, deviceScaleFactor: 1, mobile: false }, sessionId);
for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result.value === "complete") break; await sleep(200); }
const { root } = await send("DOM.getDocument", { depth: -1 }, sessionId);
for (const id of ["a", "b", "c", "d"]) {
  const { nodeId } = await send("DOM.querySelector", { nodeId: root.nodeId, selector: "#" + id }, sessionId);
  const r = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
  console.log(id, JSON.stringify(r.fonts));
}
ws.close(); proc.kill();
