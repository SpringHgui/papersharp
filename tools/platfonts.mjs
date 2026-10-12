// 问浏览器：给定文档里每个 div 实际用了哪张平台字体、量到多高、line-height 算成多少。
// 用法：node tools/platfonts.mjs <path/to/doc.html> [css选择器根=div]
// 用于核对「引擎的字族回退链」与 Chrome 是否选到同一张 face —— 行盒高取各行内片段的最大值，
// 回退选错一族，整行高度就差一档，往下每一行都跟着错位（重影的累计来源）。
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const DOC = resolve(process.argv[2] || "src/PaperSharpSamples/assets/_probe-fb.html");
if (!existsSync(DOC)) { console.error("找不到 " + DOC); process.exit(1); }
const PORT = 10000 + Math.floor(Math.random() * 50000);
const profile = mkdtempSync(join(tmpdir(), "ps-plat-"));
const EXE = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
const proc = spawn(EXE, ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--hide-scrollbars", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

let v = null;
for (let i = 0; i < 60 && !v; i++) { try { v = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); } catch { await sleep(300); } }
if (!v) { console.error("DevTools 未就绪"); proc.kill(); process.exit(1); }
const ws = new WebSocket(v.webSocketDebuggerUrl);
let seq = 0; const pend = new Map();
ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && pend.has(m.id)) { const p = pend.get(m.id); pend.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); } });
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
const send = (method, params = {}, sessionId) => { const id = ++seq; ws.send(JSON.stringify(sessionId ? { id, method, params, sessionId } : { id, method, params })); return new Promise((res, rej) => pend.set(id, { res, rej })); };

const { targetId } = await send("Target.createTarget", { url: "file:///" + DOC.replace(/\\/g, "/") });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId);
await send("DOM.enable", {}, sessionId);
await send("CSS.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 800, deviceScaleFactor: 1, mobile: false }, sessionId);
for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result?.value === "complete") break; await sleep(200); }
await send("Runtime.evaluate", { expression: "document.fonts.ready", awaitPromise: true }, sessionId);

const SEL = process.argv[3] || "div";
const { root } = await send("DOM.getDocument", { depth: -1 }, sessionId);
const { nodeIds } = await send("DOM.querySelectorAll", { nodeId: root.nodeId, selector: SEL }, sessionId);
const info = await send("Runtime.evaluate", {
  expression: `[...document.querySelectorAll(${JSON.stringify(SEL)})].map(d => ({ id: d.id, h: d.getBoundingClientRect().height,
    ty: +(d.getBoundingClientRect().top - d.parentElement.getBoundingClientRect().top).toFixed(2),
    lh: getComputedStyle(d).lineHeight, f: getComputedStyle(d).fontFamily, s: getComputedStyle(d).fontSize }))`,
  returnByValue: true,
}, sessionId);
const rows = info.result.value;
for (let i = 0; i < nodeIds.length; i++) {
  const r = await send("CSS.getPlatformFontsForNode", { nodeId: nodeIds[i] }, sessionId);
  const fonts = (r.fonts || []).map(f => `${f.familyName}${f.isCustomFont ? "(web)" : ""}×${f.glyphCount}`).join(" ");
  console.log(`${(rows[i]?.id ?? "?").padEnd(4)} h=${String(rows[i]?.h).padStart(6)}  ty=${String(rows[i]?.ty).padStart(6)}  lh=${String(rows[i]?.lh).padEnd(12)} ${String(rows[i]?.f).padEnd(26)} → ${fonts}`);
}
ws.close(); proc.kill();
