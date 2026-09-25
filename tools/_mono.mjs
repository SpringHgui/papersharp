// 临时：把 _probe-mono 样例的「浏览器 span 宽」和「引擎 span 宽」并排打表。
// 用法：node tools/_mono.mjs [sampleName=_probe-mono]
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const NAME = process.argv[2] || "_probe-mono";
const assets = join(process.cwd(), "src", "PaperSharpSamples", "assets");
const PORT = 9553;
const prof = mkdtempSync(join(tmpdir(), "ps-mono-"));
let html = readFileSync(join(assets, `${NAME}.html`), "utf8");
const cssPath = join(assets, `${NAME}.css`);
if (existsSync(cssPath)) html = html.replace("</head>", `<style>${readFileSync(cssPath, "utf8")}</style></head>`);
const doc = join(prof, `${NAME}.html`);
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
const url = "file:///" + doc.replace(/\\/g, "/");
const { targetId } = await send("Target.createTarget", { url });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 2000, deviceScaleFactor: 1, mobile: false }, sessionId);
await send("Page.navigate", { url }, sessionId);
await sleep(1200);
const expr = `[...document.querySelectorAll('body > p > span')].map(s => { const r = s.getBoundingClientRect(); const c = getComputedStyle(s); return { w: r.width, h: r.height, fam: c.fontFamily, size: c.fontSize, t: s.textContent }; })`;
const got = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
ws.close(); proc.kill();
const bio = got.result.value;

const engPath = join(process.cwd(), "src", "PaperSharpSamples", "bin", "Release", "net10.0", "output", `boxes-${NAME}.json`);
const eng = JSON.parse(readFileSync(engPath, "utf8"));
const espans = eng.boxes.filter(b => /p\[\d+\]\/span\[1\]$/.test(b.domPath))
  .sort((a, b) => Number(a.domPath.match(/p\[(\d+)\]/)[1]) - Number(b.domPath.match(/p\[(\d+)\]/)[1]));
console.log("i\tfam|size\tengAdv\tbioAdv\tdw\tengH\tbioH\tdh");
for (let i = 0; i < Math.max(bio.length, espans.length); i++) {
  const e = espans[i], b = bio[i];
  if (!e || !b) { console.log(`${i + 1}\tMISSING e=${!!e} b=${!!b}`); continue; }
  console.log(`${i + 1}\t${b.fam}|${b.size}\t${(e.w / 10).toFixed(4)}\t${(b.w / 10).toFixed(4)}\t${((e.w - b.w) / 10).toFixed(3)}\t${e.h.toFixed(4)}\t${b.h.toFixed(4)}\t${(e.h - b.h).toFixed(3)}`);
}
