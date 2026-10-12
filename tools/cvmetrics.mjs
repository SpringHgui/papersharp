// 问浏览器：canvas measureText 的 fontBoundingBox 上下伸出量 —— 直接读字体层的纵向度量。
// 用法：node tools/cvmetrics.mjs "Segoe UI Light 10.5px" "Microsoft YaHei Light 10.5px" ...
// 布局里的 line-height:normal 由这两个数取整后相加，排版对不上时先来这里的数字找分歧点。
import { spawn } from "node:child_process";
import { mkdtempSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const specs = process.argv.slice(2);
if (!specs.length) { console.error('用法：node tools/cvmetrics.mjs "<family> <sizePx>" ...'); process.exit(1); }
const PORT = 10000 + Math.floor(Math.random() * 50000);
const profile = mkdtempSync(join(tmpdir(), "ps-cvm-"));
const EXE = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
const proc = spawn(EXE, ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  "--no-first-run", "--no-default-browser-check", "--disable-gpu", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
let v = null;
for (let i = 0; i < 60 && !v; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); } catch { await sleep(300); } }
if (!v) { console.error("DevTools 未就绪"); proc.kill(); process.exit(1); }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sessionId) => { const id = ++seq; ws.send(JSON.stringify(sessionId ? { id, method, params, sessionId } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };

const html = `data:text/html,<canvas id=c></canvas>`;
const { targetId } = await send("Target.createTarget", { url: html });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Runtime.enable", {}, sessionId);
const r = await send("Runtime.evaluate", { returnByValue: true, expression: `(() => {
  const ctx = document.getElementById('c').getContext('2d');
  return ${JSON.stringify(specs)}.map(s => {
    const i = s.lastIndexOf(' ');
    const fam = s.slice(0, i), size = +s.slice(i + 1).replace('px', '');
    ctx.font = size + 'px ' + fam;
    const m = ctx.measureText('中');
    return s + '  asc=' + m.fontBoundingBoxAscent.toFixed(3) + ' desc=' + m.fontBoundingBoxDescent.toFixed(3)
      + '  em=' + (m.emHeightAscent||0).toFixed(3) + '/' + (m.emHeightDescent||0).toFixed(3);
  }).join('\\n');
})()` }, sessionId);
console.log(r.result?.value ?? JSON.stringify(r));
ws.close(); proc.kill();
