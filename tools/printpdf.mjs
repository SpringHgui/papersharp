// 无头 Edge 的 Page.printToPDF：给「PDF 打印口径」取浏览器真值用。
//
//   node tools/printpdf.mjs <htmlFile> <outPdf>
//
// 与引擎 RenderPdf 同口径：preferCSSPageSize（吃 html 里的 @page）、printBackground、
// 不给 margin 覆盖（让 @page 决定）。量出来的页数/基线可与引擎 PDF 逐页对账。
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const [htmlFileRaw, outPdf] = process.argv.slice(2);
const htmlFile = htmlFileRaw ? resolve(htmlFileRaw) : htmlFileRaw;
if (!htmlFile || !existsSync(htmlFile)) { console.error("缺 html 文件：" + htmlFile); process.exit(2); }
if (!outPdf) { console.error("缺输出 pdf 路径"); process.exit(2); }
const PORT = 9641 + (process.pid % 60);
const BROWSER = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
if (!BROWSER) { console.error("找不到 Edge 可执行文件"); process.exit(3); }

const profile = mkdtempSync(join(tmpdir(), "papersharp-printpdf-"));
const proc = spawn(BROWSER, [
  "--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--hide-scrollbars",
  "--disable-lcd-text", "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
class Cdp {
  constructor() { this.p = new Map(); this.s = 0; }
  open(url) { return new Promise((res) => { this.ws = new WebSocket(url); this.ws.addEventListener("open", res); this.ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && this.p.has(m.id)) { const h = this.p.get(m.id); this.p.delete(m.id); if (m.error) h.reject(new Error(`${h.method}: ${m.error.message}`)); else h.resolve(m.result); } }); }); }
  send(method, params = {}, sid) { const id = ++this.s; return new Promise((res, rej) => { this.p.set(id, { resolve: res, reject: rej, method }); this.ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params })); }); }
  close() { try { this.ws.close(); } catch { } }
}
const cdp = new Cdp();
async function retry(fn, tries = 4) {
  for (let i = 0; ; i++) {
    try { return await fn(); }
    catch (e) {
      if (i >= tries || !/Execution context was destroyed|Cannot find context|target closed|Cannot attach to this target/i.test(String(e && e.message))) throw e;
      await sleep(400);
    }
  }
}
try {
  let version; for (let i = 0; i < 100; i++) { try { version = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json(); break; } catch { await sleep(300); } }
  await cdp.open(version.webSocketDebuggerUrl);
  const url = "file:///" + htmlFile.replace(/\\/g, "/");
  const { targetId } = await cdp.send("Target.createTarget", { url: "about:blank" });
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
  await cdp.send("Page.enable", {}, sessionId);
  await cdp.send("Page.navigate", { url }, sessionId);
  for (let i = 0; i < 60; i++) {
    let ready = false;
    try {
      const rs = await cdp.send("Runtime.evaluate", { expression: "document.readyState + '|' + document.URL", returnByValue: true }, sessionId);
      ready = String(rs.result?.value).endsWith("|complete") && !String(rs.result?.value).includes("about:blank");
    } catch { }
    if (ready) break;
    await sleep(300);
  }
  await retry(() => cdp.send("Runtime.evaluate", { expression: `(async()=>{try{await document.fonts.ready}catch{}await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));return 1})()`, awaitPromise: true, returnByValue: true }, sessionId));
  const { data } = await retry(() => cdp.send("Page.printToPDF", {
    printBackground: true, preferCSSPageSize: true, displayHeaderFooter: false,
  }, sessionId));
  writeFileSync(outPdf, Buffer.from(data, "base64"));
  console.log("ok " + outPdf);
} catch (e) {
  console.error(String(e && e.message || e)); process.exitCode = 1;
} finally { cdp.close(); proc.kill(); }
