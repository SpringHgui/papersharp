// 临时：family 关键字 → 浏览器实际命中的 face + normal 行盒高度（用于 FontManager 的按族映射表）。
// 用法：node tools/_famprobe.mjs
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const FAMS = [
  'sans-serif', '"Microsoft YaHei"', '"PingFang SC"', '"SimHei"', 'Arial', 'Helvetica',
  'system-ui', 'serif', '"Times New Roman"', 'Georgia', '"Noto Serif SC"', 'monospace',
  'Consolas', '"Courier New"', '"SimSun"', 'cursive', 'fantasy',
];
const SIZES = [13.3333, 14.6667, 16, 22];

let rows = "";
const ids = [];
FAMS.forEach((f, i) => SIZES.forEach((s, j) => {
  const id = `f${i}_${j}`; ids.push([id, f, s]);
  rows += `<div id="${id}" style="font-family:${f.replace(/"/g, "'")};font-size:${s}px;line-height:normal;margin:0">abc 布局皮</div>`;
}));

const PORT = 9547;
const prof = mkdtempSync(join(tmpdir(), "ps-fam-"));
const proc = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${prof}`, "--no-first-run", "--disable-gpu", "about:blank"],
  { stdio: "ignore" });
const doc = join(prof, "p.html");
writeFileSync(doc, `<!doctype html><meta charset=utf-8><body style="margin:0">${rows}</body>`);

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
await send("Emulation.setDeviceMetricsOverride", { width: 600, height: 2400, deviceScaleFactor: 1, mobile: false }, sessionId);
for (let i = 0; i < 40; i++) { const s = await send("Runtime.evaluate", { expression: "document.readyState", returnByValue: true }, sessionId); if (s.result.value === "complete") break; await sleep(200); }
const { root } = await send("DOM.getDocument", { depth: -1 }, sessionId);
const expr = "[" + ids.map(([id]) => `["${id}",+document.getElementById("${id}").getBoundingClientRect().height.toFixed(4),+document.getElementById("${id}").getBoundingClientRect().width.toFixed(4)]`).join(",") + "]";
const rr = await send("Runtime.evaluate", { expression: expr, returnByValue: true }, sessionId);
const byId = new Map(rr.result.value.map(x => [x[0], x]));
for (const [id, fam, s] of ids) {
  const { nodeId } = await send("DOM.querySelector", { nodeId: root.nodeId, selector: "#" + id }, sessionId);
  const f = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
  const b = byId.get(id);
  console.log(`${fam.padEnd(20)} ${String(s).padStart(9)}px  h=${String(b[1]).padEnd(8)} w=${String(b[2]).padEnd(9)} ${(f.fonts || []).map(x => x.familyName + "×" + x.glyphCount).join(", ")}`);
}
ws.close(); proc.kill();
