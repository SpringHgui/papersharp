// 行盒基线探针：把 assets/_probe-baseline.html 原样交给无头浏览器，逐行量
//   行盒 top/height、**基线**（用 height:0 的 inline-block 标记，其底边即基线）、
//   以及该字体/字号/字重下 Chrome 自己的 ascent/descent（canvas fontBoundingBox*）。
// 用法：node tools/baselineprobe.mjs [_probe-baseline] [selector]
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const sample = process.argv[2] || "_probe-baseline";
const sel = process.argv[3] || ".r";
const PORT = Number(process.argv[4] || 9611);
const prof = mkdtempSync(join(tmpdir(), "ps-base-"));
const assets = join(process.cwd(), "src", "PaperSharpSamples", "assets");
const doc = join(prof, `${sample}.html`);
let html = readFileSync(join(assets, `${sample}.html`), "utf8");
const cssPath = join(assets, `${sample}.css`);
if (existsSync(cssPath)) html = html.replace("</head>", `<style>${readFileSync(cssPath, "utf8")}</style></head>`);
writeFileSync(doc, html);

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
await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 2000, deviceScaleFactor: 1, mobile: false }, sessionId);
await send("Page.navigate", { url }, sessionId);
await sleep(1200);

const expr = `(() => {
  const r6 = (x) => Math.round(x * 1e6) / 1e6;
  const out = [...document.querySelectorAll(${JSON.stringify(sel)})].map((el) => {
    const st = getComputedStyle(el);
    const box = el.getBoundingClientRect();
    const m = el.querySelector('.m');
    const b = m ? m.getBoundingClientRect() : null;
    const c = document.createElement('canvas').getContext('2d');
    c.font = st.fontWeight + ' ' + st.fontSize + ' ' + st.fontFamily;
    const tm = c.measureText('圆角阴影ag');
    return { k: el.dataset.k || '', fs: r6(parseFloat(st.fontSize)), lh: st.lineHeight, fw: st.fontWeight,
             top: r6(box.top), h: r6(box.height), base: b ? r6(b.top) : null,
             fba: r6(tm.fontBoundingBoxAscent), fbd: r6(tm.fontBoundingBoxDescent) };
  });
  return out; })()`;
const { result } = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
const rows = result.value;
console.log("k              fs    line-height      fw  boxTop     boxH   baseline   off(=base-top)  chrome fba/fbd");
for (const r of rows) {
  const off = r.base == null ? NaN : r.base - r.top;
  console.log(`${(r.k || '').padEnd(14)} ${String(r.fs).padStart(4)}  ${String(r.lh).padStart(12)}  ${String(r.fw).padStart(3)}  ${String(r.top.toFixed(4)).padStart(8)}  ${String(r.h.toFixed(4)).padStart(8)}  ${String(r.base == null ? "-" : r.base.toFixed(4)).padStart(9)}  ${String(off.toFixed(4)).padStart(8)}   ${r.fba}/${r.fbd}`);
}
ws.close(); proc.kill();
