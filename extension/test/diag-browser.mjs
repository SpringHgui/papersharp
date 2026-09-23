// 诊断 3：换浏览器/换开关，找出哪个组合能真正装载未打包扩展（branded Chrome 会忽略 --load-extension）。
//   node extension/test/diag-browser.mjs [chrome|edge] [headless|headed]
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { startServers, PAGE_ORIGIN } from "./serve.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const extDir = join(here, "..");
const WHICH = process.argv[2] || "edge";
const MODE = process.argv[3] || "headless";
const BROWSERS = {
  chrome: "C:/Program Files/Google/Chrome/Application/chrome.exe",
  edge: "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
};
const PORT = WHICH === "edge" ? 9381 : 9380;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const chrome = BROWSERS[WHICH];
if (!existsSync(chrome)) { console.error("没有", chrome); process.exit(1); }

const servers = await startServers();
const proc = spawn(chrome, [
  ...(MODE === "headless" ? ["--headless=new"] : []),
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "ps-br-"))}`,
  `--load-extension=${extDir}`,
  "--no-first-run", "--no-default-browser-check", "--disable-gpu", "about:blank",
], { stdio: "ignore" });

let ver;
for (let i = 0; i < 50 && !ver; i++) { await sleep(500); try { ver = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); } catch { /* 等 */ } }
if (!ver) { console.log(`${WHICH}/${MODE}: DevTools 未就绪`); proc.kill("SIGKILL"); servers.stop(); process.exit(0); }
console.log(`${WHICH}/${MODE}: ${ver.Browser}`);

const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
let seq = 0; const pending = new Map();
ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { const { res } = pending.get(m.id); pending.delete(m.id); res(m.result); } });
const send = (method, params = {}, sessionId) => new Promise((res) => { const id = ++seq; pending.set(id, { res }); ws.send(JSON.stringify(sessionId ? { id, method, params, sessionId } : { id, method, params })); });

const { targetInfos } = await send("Target.getTargets");
const page = targetInfos.find((t) => t.type === "page");
const { sessionId } = await send("Target.attachToTarget", { targetId: page.targetId, flatten: true });
await send("Runtime.enable", {}, sessionId);
await send("Page.navigate", { url: `${PAGE_ORIGIN}/showcase.html` }, sessionId);
await sleep(5000);
const injected = await send("Runtime.evaluate", { expression: "!!document.querySelector('[data-papersharp-ui]')", returnByValue: true }, sessionId);
const extTargets = (await send("Target.getTargets")).targetInfos.filter((t) => t.url.startsWith("chrome-extension://"));
console.log(`  注入=${injected.result?.value}`);
console.log(`  扩展 target：${extTargets.map((t) => `${t.type}#${new URL(t.url).host.slice(0, 8)}`).join(", ") || "无"}`);
ws.close(); proc.kill("SIGKILL"); servers.stop(); await sleep(300); process.exit(0);
