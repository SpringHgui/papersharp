// 引擎盒矩形 vs 无头浏览器 getBoundingClientRect() 的对拍（Playground 差异表的命令行版）。
//
//   node tools/boxdiff.mjs img                 # 先跑 dotnet run --project src/PaperSharpSamples -- boxes img
//   node tools/boxdiff.mjs img --tol 0.5 --json
//
// 量法与 wwwroot/playground.js 的 measure() 逐字同构（不成盒兄弟占号、svg/input 不下钻、
// display:none 两边都不记、行组透传），坐标系同样减 body 边框盒原点。
// 浏览器用 Edge 无头 + --hide-scrollbars（品牌版 Chrome 会忽略部分开关，e2e 实测过）。

import { spawn } from "node:child_process";
import { existsSync, readFileSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const arg = (name, dflt) => { const i = process.argv.indexOf(`--${name}`); return i >= 0 ? process.argv[i + 1] : dflt; };
const flag = (name) => process.argv.includes(`--${name}`);

const NAME = process.argv[2] || "img";
const WIDTH = Number(arg("width", 1284));
const TOL = Number(arg("tol", 0.5));
const PORT = Number(arg("port", 9411));
const BROWSERS = {
  edge: ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"],
  chrome: ["C:/Program Files/Google/Chrome/Application/chrome.exe", "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe"],
};


async function main() {
  const outDirs = ["bin/Release/net10.0", "bin/Debug/net10.0"].map((d) => join(root, "src", "PaperSharpSamples", d, "output"));
  const pick = (f) => outDirs.map((d) => join(d, f)).find(existsSync);
  const boxesPath = pick(`boxes-${NAME}.json`);
  const docPath = pick(`boxes-${NAME}.html`);
  if (!boxesPath || !docPath) {
    console.error(`缺产物：先跑  dotnet run --project src/PaperSharpSamples -- boxes ${NAME} ${WIDTH}`);
    process.exit(1);
  }
  const engine = JSON.parse(readFileSync(boxesPath, "utf8"));
  const browser = await measureInBrowser(docPath);

  // ── 与 PlaygroundDiff.Compare 同一口径 ─────────────────────────────
  const emap = new Map(engine.boxes.map((b) => [b.domPath, b]));
  const bmap = new Map(browser.map((b) => [b.domPath, b]));
  const rows = [];
  let ok = 0, drift = 0, engineOnly = 0, browserOnly = 0, worst = 0, sum = 0;
  for (const [path, e] of emap) {
    const b = bmap.get(path);
    if (!b) { engineOnly++; rows.push({ path, tag: e.tag, kind: "E-only", max: 1e9 }); continue; }
    const dx = b.x - e.x, dy = b.y - e.y, dw = b.w - e.w, dh = b.h - e.h;
    const max = Math.max(Math.abs(dx), Math.abs(dy), Math.abs(dw), Math.abs(dh));
    if (max <= TOL) ok++; else { drift++; sum += max; }
    worst = Math.max(worst, max);
    rows.push({ path, tag: e.tag, kind: max <= TOL ? "ok" : "drift", e, b, dx, dy, dw, dh, max });
  }
  for (const [path, b] of bmap) if (!emap.has(path)) { browserOnly++; rows.push({ path, tag: b.tag, kind: "B-only", max: 1e9 }); }

  if (flag("json")) { console.log(JSON.stringify({ summary: { ok, drift, engineOnly, browserOnly, worst, mean: drift ? sum / drift : 0 }, rows: rows.filter((r) => r.kind !== "ok") }, null, 1)); return; }

  console.log(`${NAME} @ ${WIDTH}px · 引擎 ${emap.size} 盒 / 浏览器 ${bmap.size} 矩形 · ok ${ok} · 漂移 ${drift} · 引擎独 ${engineOnly} · 浏览器独 ${browserOnly} · 最坏 ${worst.toFixed(1)}px · 均值 ${(drift ? sum / drift : 0).toFixed(1)}px\n`);
  const f = (n) => (n >= 0 ? "+" : "") + n.toFixed(1).padStart(7);
  for (const r of rows.filter((r) => r.kind !== "ok").sort((a, b) => b.max - a.max)) {
    if (r.kind !== "drift") { console.log(`${r.kind.padEnd(5)} ${r.path}`); continue; }
    console.log(`dx${f(r.dx)} dy${f(r.dy)} dw${f(r.dw)} dh${f(r.dh)}  max ${String(r.max.toFixed(1)).padStart(6)}  ${r.path}`);
    console.log(`      引擎 e=(${r.e.x.toFixed(1)},${r.e.y.toFixed(1)}) ${r.e.w.toFixed(1)}×${r.e.h.toFixed(1)}   浏览器=(${r.b.x.toFixed(1)},${r.b.y.toFixed(1)}) ${r.b.w.toFixed(1)}×${r.b.h.toFixed(1)}`);
  }
}

/** 无头浏览器量矩形：与 playground.js 的 measure() 同构。 */
async function measureInBrowser(docPath) {
  const browser = BROWSERS[arg("browser", "edge")]?.find(existsSync);
  if (!browser) throw new Error("找不到 Edge/Chrome 可执行文件");
  const profile = mkdtempSync(join(tmpdir(), "papersharp-boxdiff-"));
  const proc = spawn(browser, [
    "--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
    "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--hide-scrollbars",
    `--window-size=${WIDTH},1200`, "about:blank",
  ], { stdio: "ignore" });

  const cdp = new Cdp();
  try {
    const version = await pollJson(PORT, 30000);
    await cdp.open(version.webSocketDebuggerUrl);
    // 自开 target：Edge 会把 edge://sync-confirmation-dialog 注册成第一个 page，find(type==='page') 量到的是它
    const { targetId } = await cdp.send("Target.createTarget", { url: "file:///" + docPath.replace(/\\/g, "/") });
    const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
    await cdp.send("Page.enable", {}, sessionId);
    // 钉死布局视口：headless=new 的 --window-size 会受系统 DPI 影响（实测 200% 下 body 凭空窄 34px），
    // 与 Playground 的 iframe 定宽不等价，会造出一整类假宽度漂移。
    await cdp.send("Emulation.setDeviceMetricsOverride", { width: WIDTH, height: 1000, deviceScaleFactor: 1, mobile: false }, sessionId);
    for (let i = 0; i < 60; i++) {
      const ready = await evalIn(cdp, sessionId, `document.readyState`);
      if (ready === "complete") break;
      await sleep(300);
    }
    // 图全解码 + 字体就绪 + 两帧稳定，量晚不量早（与 playground.js 的 settle 同一意图）
    await evalIn(cdp, sessionId, `(async()=>{
      await Promise.all([...document.images].map(img=>img.decode().catch(()=>{})));
      try { await document.fonts.ready; } catch {}
      await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
      return 1; })()`, true);
    if (flag("diag")) console.log("视口诊断：" + JSON.stringify(await evalIn(cdp, sessionId,
      `({innerWidth, clientWidth: document.documentElement.clientWidth, dpr: devicePixelRatio, bodyW: document.body.getBoundingClientRect().width})`)));
    return await evalIn(cdp, sessionId, MEASURE_SNIPPET, true);
  } finally {
    cdp.close();
    proc.kill();
  }
}

const MEASURE_SNIPPET = `(() => {
  const NO_BOX = new Set(["STYLE","SCRIPT","TEMPLATE","META","LINK","TITLE","BASE","NOSCRIPT","HEAD","BR"]);
  const NO_DESCEND = new Set(["SVG","INPUT"]);
  const PASS_THROUGH_DISPLAY = new Set(["table-row-group","table-header-group","table-footer-group"]);
  const pathOf = (p, tag, seq) => p + "/" + tag.toLowerCase() + "[" + seq + "]";
  const childSeqMap = () => { const seen = new Map(); return (t) => { const n = (seen.get(t) ?? 0) + 1; seen.set(t, n); return n; }; };
  const doc = document, win = window, body = doc.body;
  const bodyRect = body.getBoundingClientRect();
  const out = [];
  const visit = (el, path) => {
    const cs = win.getComputedStyle(el);
    if (cs.display === "none") return;
    if (!PASS_THROUGH_DISPLAY.has(cs.display)) {
      const r = el.getBoundingClientRect();
      out.push({ domPath: path, tag: el.tagName.toLowerCase(),
        x: r.left + win.scrollX - bodyRect.left, y: r.top + win.scrollY - bodyRect.top, w: r.width, h: r.height });
    }
    if (NO_DESCEND.has(el.tagName)) return;
    const seq = childSeqMap();
    for (const kid of el.children) {
      const n = seq(kid.tagName);
      if (NO_BOX.has(kid.tagName)) continue;
      visit(kid, pathOf(path, kid.tagName, n));
    }
  };
  visit(body, "body[1]");
  return out; })()`;

async function evalIn(cdp, sessionId, expression, awaitPromise = false) {
  const r = await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise }, sessionId);
  if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails).slice(0, 400));
  return r.result?.value;
}

async function pollJson(port, timeoutMs) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try { return await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { await sleep(400); }
  }
  throw new Error("DevTools 端点未在超时内就绪（端口被占用？--port 换一个）");
}

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

class Cdp {
  constructor() { this.seq = 0; this.pending = new Map(); }
  open(url) {
    this.ws = new WebSocket(url);
    return new Promise((resolve, reject) => {
      this.ws.addEventListener("open", () => resolve(), { once: true });
      this.ws.addEventListener("error", (e) => reject(new Error("CDP 连接失败 " + (e.message || ""))));
      this.ws.addEventListener("message", (ev) => {
        const msg = JSON.parse(ev.data);
        if (msg.id && this.pending.has(msg.id)) {
          const { res, rej } = this.pending.get(msg.id);
          this.pending.delete(msg.id);
          msg.error ? rej(new Error(JSON.stringify(msg.error))) : res(msg.result);
        }
      });
    });
  }
  send(method, params = {}, sessionId) {
    const id = ++this.seq;
    this.ws.send(JSON.stringify(sessionId ? { id, method, params, sessionId } : { id, method, params }));
    return new Promise((res, rej) => this.pending.set(id, { res, rej }));
  }
  close() { try { this.ws?.close(); } catch { /* 已关闭 */ } }
}

main().catch((e) => { console.error("[boxdiff] 失败：", e); process.exitCode = 1; });
