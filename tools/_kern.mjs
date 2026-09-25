// 临时：读 _probe-kern 页面里每个 span 的浏览器宽度 + Blink 实际命中的字形面。
// 用法：node tools/_kern.mjs  →  写 tools/_kern.out.json
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const NAME = process.argv[2] || "_probe-kern";
const assets = join(process.cwd(), "src", "PaperSharpSamples", "assets");
const PORT = 9557;
const prof = mkdtempSync(join(tmpdir(), "ps-kern-"));
const doc = join(prof, `${NAME}.html`);
writeFileSync(doc, readFileSync(join(assets, `${NAME}.html`), "utf8"));

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
await send("DOM.enable", {}, sessionId);
await send("CSS.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1284, height: 2000, deviceScaleFactor: 1, mobile: false }, sessionId);
await send("Page.navigate", { url }, sessionId);
await sleep(1500);

const { root } = await send("DOM.getDocument", {}, sessionId);
const { nodeIds } = await send("DOM.querySelectorAll", { nodeId: root.nodeId, selector: "body > p > span" }, sessionId);
const out = [];
for (const nodeId of nodeIds) {
  const { model } = await send("DOM.getBoxModel", { nodeId }, sessionId);
  const w = model.content[2] - model.content[0];
  const { fonts } = await send("CSS.getPlatformFontsForNode", { nodeId }, sessionId);
  out.push({ w, face: fonts.map(f => `${f.familyName}@${f.glyphCount}`).join(",") });
}
writeFileSync(join(process.cwd(), "tools", "_kern.out.json"), JSON.stringify(out, null, 1));
console.log(out.map((o, i) => `${i}\t${o.w.toFixed(3)}\t${o.face}`).join("\n"));
ws.close(); proc.kill();
