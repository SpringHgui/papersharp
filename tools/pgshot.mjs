// 通用「无头浏览器参照」出图 + 量矩形：给 Playground 的「打印对拍」视图用。
//
//   node tools/pgshot.mjs <htmlFile> <width> <dsf> <outPng> <outRectsJson>
//
// 与 tools/boxdiff.mjs 的无头参照逐字同源（同一套 CDP + MEASURE_SNIPPET），只是吃任意 html 文件、
// 不读引擎产物、不求差——参照物是**无头(Skia)口径**，与引擎的打印/PDF 口径一致（2026-10-02 取证：
// Chrome printToPDF 与无头屏显逐字节相同、且都等于引擎；有头屏显才吃 DirectWrite 那 ±0.5px/行）。
// 所以拿它当叠加参照，CJK 重影应≈0；Playground 默认的有头 iframe 才会露出幻影。
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const [htmlFileRaw, widthArg, dsfArg, outPng, outRects] = process.argv.slice(2);
const htmlFile = htmlFileRaw ? resolve(htmlFileRaw) : htmlFileRaw;   // 相对路径会让 file:/// URL 失效、Edge 落到默认页
if (!htmlFile || !existsSync(htmlFile)) { console.error("缺 html 文件：" + htmlFile); process.exit(2); }
const WIDTH = Math.max(1, Math.round(Number(widthArg) || 1284));
const DSF = Number(dsfArg) || 2;
const PORT = 9641 + (process.pid % 60);
const BROWSER = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
if (!BROWSER) { console.error("找不到 Edge 可执行文件"); process.exit(3); }

const profile = mkdtempSync(join(tmpdir(), "papersharp-pgshot-"));
const proc = spawn(BROWSER, [
  "--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--hide-scrollbars",
  "--disable-lcd-text", `--window-size=${WIDTH},1200`, "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
class Cdp {
  constructor() { this.p = new Map(); this.s = 0; }
  open(url) { return new Promise((res) => { this.ws = new WebSocket(url); this.ws.addEventListener("open", res); this.ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && this.p.has(m.id)) { const h = this.p.get(m.id); this.p.delete(m.id); if (m.error) h.reject(new Error(`${h.method}: ${m.error.message}`)); else h.resolve(m.result); } }); }); }
  send(method, params = {}, sid) { const id = ++this.s; return new Promise((res, rej) => { this.p.set(id, { resolve: res, reject: rej, method }); this.ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params })); }); }
  close() { try { this.ws.close(); } catch { } }
}
const cdp = new Cdp();
// 导航提交那一瞬，页内求值会拿到 "Execution context was destroyed"。这类错误过一会儿自己就好，
// 重试比让它冒泡成「无头参照出图失败」有用（调用方看到的是一句跟渲染无关的噪声）。
async function retry(fn, tries = 4) {
  for (let i = 0; ; i++) {
    try { return await fn(); }
    catch (e) {
      if (i >= tries || !/Execution context was destroyed|Cannot find context|target closed|Cannot attach to this target|Unable to capture screenshot/i.test(String(e && e.message))) throw e;
      await sleep(400);
    }
  }
}
try {
  let version; for (let i = 0; i < 100; i++) { try { version = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
  await cdp.open(version.webSocketDebuggerUrl);
  const url = "file:///" + htmlFile.replace(/\\/g, "/");
  // 先建空白页、attach 完再导航。createTarget 直接带 URL 时，轮询会读到还没被导航顶掉的
  // about:blank —— 它的 readyState 本来就是 complete，于是 settle 求值正好撞在导航提交上。
  const { targetId } = await cdp.send("Target.createTarget", { url: "about:blank" });
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
  await cdp.send("Page.enable", {}, sessionId);
  await cdp.send("Emulation.setDeviceMetricsOverride", { width: WIDTH, height: 1200, deviceScaleFactor: DSF, mobile: false }, sessionId);
  await cdp.send("Page.navigate", { url }, sessionId);
  for (let i = 0; i < 60; i++) {
    let ready = false;
    try {
      const rs = await cdp.send("Runtime.evaluate", { expression: "document.readyState + '|' + document.URL", returnByValue: true }, sessionId);
      ready = String(rs.result?.value).endsWith("|complete") && !String(rs.result?.value).includes("about:blank");
    } catch { /* 导航中，下一轮再问 */ }
    if (ready) break;
    await sleep(300);
  }
  await retry(() => cdp.send("Runtime.evaluate", { expression: `(async()=>{await Promise.all([...document.images].map(i=>i.decode().catch(()=>{})));try{await document.fonts.ready}catch{}try{for(const a of document.getAnimations()){a.pause();a.currentTime=0}}catch{}await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));return 1})()`, awaitPromise: true, returnByValue: true }, sessionId));
  const { data } = await retry(() => cdp.send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true }, sessionId));
  writeFileSync(outPng, Buffer.from(data, "base64"));
  const rects = await retry(() => cdp.send("Runtime.evaluate", { expression: measureSnippet(), returnByValue: true }, sessionId));
  writeFileSync(outRects, JSON.stringify(rects.result?.value ?? []));
  console.log("ok " + outPng);
} catch (e) {
  console.error(String(e && e.message || e)); process.exitCode = 1;
} finally { cdp.close(); proc.kill(); }

// 与 boxdiff.mjs / wwwroot/playground.js 的 measure() 逐字同构（不成盒兄弟占号、svg/input 不下钻、
// display:none 两边都不记、行组透传），坐标系同样减 body 边框盒原点。声明成函数是为了让上面 try 块
// 能在模块求值阶段就调用它（const 模板串放后面会撞 TDZ）。
function measureSnippet() { return `(() => {
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
    const tn = el.tagName.toUpperCase();
    if (cs.display === "none") return;
    if (!PASS_THROUGH_DISPLAY.has(cs.display)) {
      const r = el.getBoundingClientRect();
      out.push({ domPath: path, tag: el.tagName.toLowerCase(),
        x: r.left + win.scrollX - bodyRect.left, y: r.top + win.scrollY - bodyRect.top, w: r.width, h: r.height });
    }
    if (NO_DESCEND.has(tn)) return;
    const seq = childSeqMap();
    for (const kid of el.children) {
      const n = seq(kid.tagName);
      if (NO_BOX.has(kid.tagName.toUpperCase())) continue;
      visit(kid, pathOf(path, kid.tagName, n));
    }
  };
  visit(body, "body[1]");
  return out; })()`; }
