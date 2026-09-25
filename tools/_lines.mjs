// 临时：用 Range.getClientRects 读出浏览器里 .calc-box 的逐行断点（对比引擎 WrappedLines）。
// 用法：node tools/_lines.mjs [selector] [sample]
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const sel = process.argv[2] || ".calc-box";
const sample = process.argv[3] || "m4";
const PORT = 9551;
const prof = mkdtempSync(join(tmpdir(), "ps-lines-"));
const assets = join(process.cwd(), "src", "PaperSharpSamples", "assets");
const doc = join(prof, `${sample}.html`);
const assets_ = join(assets, `${sample}.html`);
let html = readFileSync(assets_, "utf8");
const cssPath = join(assets, `${sample}.css`);
if (existsSync(cssPath)) html = html.replace("</head>", `<style>${readFileSync(cssPath, "utf8")}</style></head>`);
writeFileSync(doc, html);

const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${prof}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });

const sleep = ms => new Promise(r => setTimeout(r, ms));
let v; for (let i = 0; i < 60; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", ev => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise(r => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sid) => { const id = ++seq; ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };
const { targetId } = await send("Target.createTarget", { url: "file:///" + doc.replace(/\\/g, "/") });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 2000, deviceScaleFactor: 1, mobile: false }, sessionId);
await send("Page.navigate", { url: "file:///" + doc.replace(/\\/g, "/") }, sessionId);
await sleep(1500);

const expr = `(() => {
  const els = [...document.querySelectorAll(${JSON.stringify(sel)})];
  return els.map(el => {
  const tn = (function firstText(n){ for (const c of n.childNodes) { if (c.nodeType===3 && c.textContent.trim()) return c; const r=firstText(c); if (r) return r; } return null; })(el);
  const s = tn.textContent;
  const rectsOf = (a,b) => { const r = document.createRange(); r.setStart(tn,a); r.setEnd(tn,b); return r.getBoundingClientRect(); };
  // 逐字符所在行号
  const lineOf = [];
  let cur = -1, lastTop = null;
  for (let i = 0; i < s.length; i++) {
    const bb = rectsOf(i, i+1);
    const top = Math.round(bb.top * 100) / 100;
    if (top !== lastTop) { cur++; lastTop = top; }
    lineOf.push(cur);
  }
  const lines = [];
  for (let L = 0; L <= cur; L++) {
    let a = s.length, b = 0;
    for (let i = 0; i < s.length; i++) { if (lineOf[i]===L) { if (i<a) a=i; b=i; } }
    lines.push({ text: s.slice(a, b+1), w: Math.round(rectsOf(a, b+1).width*100)/100 });
  }
  const all = document.createRange(); all.selectNodeContents(tn);
  return { id: el.id, n: lines.length, clientW: el.clientWidth, lineHeight: getComputedStyle(el).lineHeight, fontSize: getComputedStyle(el).fontSize, rects: [...all.getClientRects()].map(r=>Math.round(r.width*100)/100), lines };
  });
})()`;
const res = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
for (const v of res.result.value) console.log(JSON.stringify(v));
ws.close(); proc.kill();
