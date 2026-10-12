// 绘制相位探针：把 .scratch/snap-phase.html（一串左边距为分数的实心黑条）在无头 Edge 里
// 按 deviceScaleFactor=2 截图，再用 PowerShell 读回每根条的首/末暗列。
// 要判的问题：Blink 的 snap-to-pixel 折的是**整 CSS px**（偶数设备列）还是**整设备像素**（奇偶都可能）。
//   node tools/snapprobe.mjs [html 路径] [png 输出] [端口]
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const htmlPath = process.argv[2] || ".scratch/snap-phase.html";
const outPng = process.argv[3] || ".scratch/snap-phase.png";
const PORT = Number(process.argv[4] || 9631);
const DSF = Number(process.env.DSF || 2);
const prof = mkdtempSync(join(tmpdir(), "ps-snap-"));
const doc = join(prof, "probe.html");
writeFileSync(doc, readFileSync(htmlPath, "utf8"));

const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${prof}`, "--no-first-run",
   "--disable-gpu", "--hide-scrollbars", "--disable-lcd-text", "about:blank"], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let v; for (let i = 0; i < 60; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sid) => { const id = ++seq; ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };
const url = "file:///" + doc.replace(/\\/g, "/");
const { targetId } = await send("Target.createTarget", { url });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 400, deviceScaleFactor: DSF, mobile: false }, sessionId);
await send("Page.navigate", { url }, sessionId);
await sleep(1200);
const { data } = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true }, sessionId);
writeFileSync(outPng, Buffer.from(data, "base64"));
const rects = await send("Runtime.evaluate", {
  expression: `[...document.querySelectorAll('.bar')].map(e=>{const r=e.getBoundingClientRect();return [r.left,r.top,r.width]})`,
  returnByValue: true,
}, sessionId);
console.log(JSON.stringify(rects.result.value) + "\n→ " + outPng);
ws.close(); proc.kill();
