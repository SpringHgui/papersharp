// 端到端实测：装载未打包扩展 → 打开测试页 → 逐个格式导出 → 核对 chrome.downloads 落地文件。
// 这是唯一能真正验证「展平 + 图片内联 + WASM 渲染 + 下载」全链路的方式（不引第三方依赖，Node 22 内置 WebSocket 直连 CDP）。
//
//   node extension/test/e2e-browser.mjs                        # 全 6 个格式
//   node extension/test/e2e-browser.mjs --formats png,pdf --diag
//   node extension/test/e2e-browser.mjs --page long-article
//   node extension/test/e2e-browser.mjs --no-scale             # 跳过末尾的等比缩放回归
//   node extension/test/e2e-browser.mjs --scalek 0.5           # 缩放回归用的 k（默认 0.6）
//   node extension/test/e2e-browser.mjs --no-idle              # 跳过末尾的宿主休眠恢复回归
//   node extension/test/e2e-browser.mjs --browser chrome       # 默认 edge
//
// 除了逐个格式导出，结尾还有两项回归（--url 真实站点模式下都跳过）：
//   缩放回归   —— 同页按 k=1 与 k=--scalek 各导一次，比对快照里 sv() 管的 px 是否真按比例下发
//   休眠恢复回归 —— 在 SW 里关掉 offscreen（复刻空闲闹钟回收），再导一次看能否自愈
//
// ⚠ 品牌版 Chrome 会忽略 --load-extension（实测：只加了内置组件扩展，我们的扩展根本没装载），
//   所以默认用 Edge（Chromium 内核，同一套 MV3 代码）。要跑 Chrome 需换 Chromium/Canary。
//
// 产物：extension/test/out/downloads/*（--default-download-directory 指到这里）+ report.json

import { spawn } from "node:child_process";
import { existsSync, copyFileSync, mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { startServers, PAGE_ORIGIN } from "./serve.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const extDir = join(here, "..");
const outDir = join(here, "out");
const dlDir = join(outDir, "downloads");
const arg = (name, dflt) => { const i = process.argv.indexOf(`--${name}`); return i >= 0 ? process.argv[i + 1] : dflt; };
const flag = (name) => process.argv.includes(`--${name}`);

const BROWSERS = {
  edge: ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"],
  chrome: ["C:/Program Files/Google/Chrome/Application/chrome.exe", "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe"],
};
const EXT_NAME = "PaperSharp 网页导出";
const PORT = Number(arg("port", 9333));
const FORMATS = (arg("formats", "pdf,png,jpeg,webp,svg,ico")).split(",").filter(Boolean);
const PAGE = arg("page", "showcase");
const TARGET = arg("url", "");   // 直接指向真实站点（跳过本地 fixture 服务器）
const EXPORT_TIMEOUT = Number(arg("timeout", 150)) * 1000;

// setAutoAttach 会替我们抢先 attach 新 target，sessionId 只能从事件里拿（再 attachToTarget 会报 already attached）
const autoSessions = new Map();   // targetId → sessionId

main().catch((e) => { console.error("\n[e2e] 失败：", e); process.exitCode = 1; });

async function main() {
  mkdirSync(dlDir, { recursive: true });
  const browser = BROWSERS[arg("browser", "edge")]?.find(existsSync);
  if (!browser) { console.error(`找不到浏览器可执行文件（${arg("browser", "edge")}），用 --browser edge|chrome 指定`); process.exit(1); }

  const servers = await startServers();
  const profile = mkdtempSync(join(tmpdir(), "papersharp-e2e-"));
  const proc = spawn(browser, [
    ...(flag("headed") ? [] : ["--headless=new"]),
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    `--load-extension=${extDir}`,
    `--default-download-directory=${dlDir}`,
    "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--hide-scrollbars",
    "--window-size=1440,1000", "about:blank",
  ], { stdio: "ignore" });

  const logs = [];
  const sessions = new Map();
  let cdp = null;
  try {
    const version = await pollJson("/json/version", 30000);
    cdp = new Cdp(version.webSocketDebuggerUrl);
    await cdp.open();
    cdp.onEvent((msg) => absorb(msg));
    // 自动挂上所有新建 target（offscreen 文档是导出时才创建的），逐个开 Runtime 收 console
    await cdp.send("Target.setAutoAttach", { autoAttach: true, waitForDebuggerOnStart: false, flatten: true });
    cdp.onEvent(async (msg) => {
      if (msg.method !== "Target.attachedToTarget") return;
      const { sessionId, targetInfo } = msg.params;
      autoSessions.set(targetInfo.targetId, sessionId);
      if (sessions.has(sessionId)) return;
      sessions.set(sessionId, targetInfo.url.includes("offscreen") ? "offscreen" : `target:${targetInfo.type}`);
      try { await cdp.send("Runtime.enable", {}, sessionId); } catch { /* 忽略 */ }
    });
    console.log(`${basename(browser)} ${version.Browser} · CDP :${PORT} · 无头=${!flag("headed")}`);

    // SW 在 MV3 里是懒唤醒的，冷启动时可能还没起来；扩展是否装载由页面侧注入证据判定
    findServiceWorker(cdp, sessions).then((sw) => sw && console.log(`扩展 SW：${sw.url}`));

    const page = await attachTarget(cdp, sessions, (t) => t.type === "page", "page", async (t) => {
      const { sessionId } = await cdp.send("Target.attachToTarget", { targetId: t.targetId, flatten: true });
      await cdp.send("Runtime.enable", {}, sessionId);
      await cdp.send("Page.enable", {}, sessionId);
      return sessionId;
    });

    const target = TARGET || `${PAGE_ORIGIN}/${PAGE}.html`;
    await cdp.send("Page.navigate", { url: target }, page.sessionId);
    await waitReady(cdp, page.sessionId);
    await sleep(2000);   // content script 在 document_idle 挂载，再等阴影 DOM / canvas / 懒加载

    let mounted = false;
    for (let i = 0; i < 20 && !mounted; i++) {
      mounted = await evalIn(cdp, page.sessionId, `!!document.querySelector('[data-papersharp-ui]')`);
      if (!mounted) await sleep(500);
    }
    console.log(`测试页：${target} · 右上角按钮已注入=${mounted}\n`);
    if (!mounted) throw new Error("content script 未注入（检查扩展是否被浏览器策略禁用）");
    await evalIn(cdp, page.sessionId, `window.__psTrace=[];addEventListener('message',e=>{if(e.data&&String(e.data.type||'').startsWith('papersharp:'))window.__psTrace.push(e.data)});1`);
    await uiSmoke(cdp, page.sessionId);

    const report = [];
    for (const format of FORMATS) {
      const t0 = Date.now();
      let res;
      try { res = await exportVia(cdp, page.sessionId, format); }
      catch (e) { res = { ok: false, error: String(e.message || e) }; }
      if (flag("dump") && res.dump) {
        writeFileSync(join(outDir, "snapshot.html"), res.dump.html);
        writeFileSync(join(outDir, "snapshot.css"), res.dump.css);
        console.log("   快照已落盘：out/snapshot.html + snapshot.css（视口 " + res.dump.viewportWidth + "px · 文档高 " + res.dump.contentHeight + "px）");
      }
      const row = { format, ok: !!res.ok, bytes: res.bytes || 0, ms: Date.now() - t0, filename: res.filename, error: res.error, warnings: res.warnings || [], timing: res.timing || null };
      report.push(row);
      console.log(`${row.ok ? "✔" : "✘"} ${format.padEnd(5)} ${String(row.bytes).padStart(9)} B  ${(row.ms / 1000).toFixed(1)}s  ${row.error || ""}`);
      if (row.warnings?.length) console.log(`   提示：${row.warnings.join(" / ")}`);
      if (row.timing) {
        const t = row.timing;
        console.log(`   耗时：总 ${(row.ms / 1000).toFixed(1)}s = 冷启 ${(t.bootMs / 1000).toFixed(1)}s + 展平 ${(t.walkMs / 1000).toFixed(1)}s(其中图片 ${(t.imageMs / 1000).toFixed(1)}s, getComputedStyle ${t.computedStyles} 次) + 传快照 ${(t.transportMs / 1000).toFixed(1)}s + 引擎 ${(t.renderMs / 1000).toFixed(1)}s`);
        console.log(`   规模：${t.elements} 元素 / ${t.classes} 组样式 / 快照 ${t.payloadKB} KB (html ${t.htmlKB} + css ${t.cssKB})`);
      }
      if (!row.ok && flag("diag")) {
        console.log("   页面回执轨迹：" + JSON.stringify(await evalIn(cdp, page.sessionId, "window.__psTrace") || []).slice(0, 700));
        console.log("   扩展侧日志：\n" + (logs.length ? logs.map((l) => `     [${l.label}/${l.level}] ${l.text}`).join("\n") : "     (无)"));
      }
    }

    const items = (await evalInExtensionPage(cdp, sessions, `new Promise(r=>chrome.downloads.search({limit:40,orderBy:['-startTime']},r))`)) || [];
    console.log(`\nchrome.downloads：`);
    for (const d of items.slice(0, FORMATS.length)) {
      const moved = normalize(d);
      const size = d.fileSize || d.bytes || 0;   // blob 下载的 bytes 常报 0，用 fileSize
      console.log(`  ${String(d.state).padEnd(9)} ${String(Math.round(size / 1024)).padStart(7)} KB  ${moved || d.filename}`);
    }
    writeFileSync(join(outDir, "report.json"), JSON.stringify({ report, downloads: items.slice(0, FORMATS.length), logs }, null, 2));
    console.log(`\n报告：${join(outDir, "report.json")}`);
    const failed = report.filter((r) => !r.ok);
    if (failed.length) console.log(`${failed.length} 个格式失败：${failed.map((f) => f.format).join(", ")}`);

    // 收尾两项回归各自还要再导一张图，所以放在 report.json 之后：早跑会把上面的 chrome.downloads 展示冲掉
    const extras = [
      flag("no-scale") || TARGET ? null : () => scaleCheck(cdp, page.sessionId),
      flag("no-idle") || TARGET ? null : () => idleRecoverCheck(cdp, sessions, page.sessionId),
    ].filter(Boolean);
    let bad = failed.length;
    for (const run of extras) {
      try { await run(); } catch (e) { bad++; console.log(`✘ ${String(e.message || e)}\n`); }
    }
    if (!extras.length) console.log(`\n(收尾回归已跳过${TARGET ? "：--url 真实站点模式" : ""})\n`);
    if (bad) process.exitCode = 1;
  } finally {
    cdp?.close();
    proc.kill("SIGKILL");
    servers.stop();
    await sleep(400);
  }

  function absorb(msg) {
    if (!msg.sessionId || !msg.method) return;
    const label = sessions.get(msg.sessionId) || msg.sessionId;
    const p = msg.params || {};
    if (msg.method === "Runtime.consoleAPICalled") {
      logs.push({ label, level: p.type, text: (p.args || []).map((a) => a.value ?? a.description ?? a.type).join(" ") });
    } else if (msg.method === "Runtime.exceptionThrown") {
      logs.push({ label, level: "error", text: p.exceptionDetails?.exception?.description || p.exceptionDetails?.text || "exception" });
    }
  }

  /** 下载没落到 out/downloads 时（浏览器忽略了目录开关），复制一份回来，别污染用户下载目录 */
  function normalize(d) {
    if (!d.filename) return null;
    if (dirname(d.filename).toLowerCase() === dlDir.toLowerCase()) return null;
    const target = join(dlDir, basename(d.filename));
    try { copyFileSync(d.filename, target); return target; } catch { return null; }
  }
}

/** 逐个 chrome-extension service worker 问 manifest.name，认出我们自己的那个（内置组件扩展也在列表里）。 */
async function findServiceWorker(cdp, sessions, rounds = 8) {
  const tried = new Set();
  for (let i = 0; i < rounds; i++) {
    const { targetInfos } = await cdp.send("Target.getTargets");
    for (const t of targetInfos.filter((x) => x.type === "service_worker" && !tried.has(x.targetId))) {
      tried.add(t.targetId);
      try {
        const sessionId = autoSessions.get(t.targetId)
          ?? (await cdp.send("Target.attachToTarget", { targetId: t.targetId, flatten: true })).sessionId;
        await cdp.send("Runtime.enable", {}, sessionId);
        const name = await evalIn(cdp, sessionId, `chrome.runtime.getManifest().name`);
        if (name === EXT_NAME) { sessions.set(sessionId, "sw"); return { ...t, sessionId }; }
      } catch { /* 休眠的 SW 连不上，下一轮再试 */ }
    }
    await sleep(500);
  }
  return null;
}

async function attachTarget(cdp, sessions, match, label, attach) {
  for (let i = 0; i < 60; i++) {
    const { targetInfos } = await cdp.send("Target.getTargets");
    const t = targetInfos.find(match);
    if (t) {
      const sessionId = autoSessions.get(t.targetId)
        ?? (attach ? await attach(t) : (await cdp.send("Target.attachToTarget", { targetId: t.targetId, flatten: true })).sessionId);
      sessions.set(sessionId, label);
      return { ...t, sessionId };
    }
    await sleep(500);
  }
  throw new Error(`没等到 ${label} 目标`);
}

/** 真鼠标点一次右上角按钮：closed shadow DOM 里够不着，但 elementFromPoint 会返回宿主，足以判定命中/展开。 */
async function uiSmoke(cdp, sessionId) {
  const vw = await evalIn(cdp, sessionId, `innerWidth`);
  const fab = { x: vw - 56, y: 33 };
  const hit = (x, y) => evalIn(cdp, sessionId, `!!document.elementFromPoint(${x},${y})?.hasAttribute('data-papersharp-ui')`);
  const onFab = await hit(fab.x, fab.y);
  for (const type of ["mousePressed", "mouseReleased"]) {
    await cdp.send("Input.dispatchMouseEvent", { type, x: fab.x, y: fab.y, button: "left", clickCount: 1 }, sessionId);
  }
  await sleep(500);
  const onCard = await hit(vw - 160, 140);
  console.log(`UI 交互：按钮命中=${onFab} · 点击后面板展开=${onCard}\n`);
  if (!onFab) throw new Error("右上角按钮不在预期位置（注入或层级有问题）");
  if (!onCard) console.log("   ⚠ 点击后没在面板区域命中，可能几何变了 —— 用 --diag 看扩展侧日志\n");
}

async function exportVia(cdp, sessionId, format, widthOverride = 0, wantDump = flag("dump")) {
  // 输出宽度默认取视口宽（= 面板默认值，k=1 不缩放）；--width 900 才走等比缩放路径
  const width = Number(widthOverride) || Number(arg("width", 0)) || "document.documentElement.clientWidth";
  // --scroll 用于验证「滚动后导出」：fixed 元素不该被钉到滚动位置那么远
  const scrollTo = Number(arg("scroll", 0));
  if (scrollTo) await evalIn(cdp, sessionId, `scrollTo(0,${scrollTo});1`);
  const expression = `new Promise((resolve)=>{
    const done=(e)=>{ if(e.data&&e.data.type==='papersharp:export-result'){ removeEventListener('message',done); resolve(e.data);} };
    addEventListener('message',done);
    postMessage({type:'papersharp:export', wantDump:${!!wantDump}, format:${JSON.stringify(format)}, width:${width}, scale:${format === "pdf" || format === "svg" ? 1 : 2}, quality:88, pageSize:'A4', icoSize:256, animationTime:1.0}, '*');
    setTimeout(()=>resolve({type:'papersharp:export-result', ok:false, error:'页面侧等待超时'}), ${EXPORT_TIMEOUT});
  })`;
  const res = await withTimeout(
    cdp.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true, timeout: EXPORT_TIMEOUT + 10000 }, sessionId),
    EXPORT_TIMEOUT + 20000, `${format} 导出超时`);
  if (res.exceptionDetails) throw new Error((res.exceptionDetails.exception?.description || res.exceptionDetails.text || "").slice(0, 300));
  const value = res.result?.value;
  if (!value) throw new Error("没有拿到导出回执");
  return value;
}

/**
 * 等比缩放回归：面板里「输出宽度」≠ 视口宽时走 k≠1 路径，capture.js 要把浏览器算好的
 * used px 按比例下发（sv() 负责纯声明文本里的那些）。这里同页导两次再比快照文本 ——
 * sv() 一旦退化成空操作（例如正则里的反斜杠丢了），这两份快照的对应声明会一模一样，
 * 只测渲染字节数是发现不了的。
 */
async function scaleCheck(cdp, sessionId) {
  // k 必须由「取整后的目标宽」反推：capture.js 用的是 k = 目标宽 / 视口宽，而宽度只能传整数
  // （实测视口 1341 → 目标 805 → k=0.60030，按名义 0.6 算会差 0.06px 判成失败）
  const vw = Math.max(280, Math.min(4096, Math.round(Number(await evalIn(cdp, sessionId, "document.documentElement.clientWidth")))));
  const target = Math.max(280, Math.round(vw * Number(arg("scalek", 0.6))));
  const k = target / vw;
  const big = await exportVia(cdp, sessionId, "png", vw, true);
  const small = await exportVia(cdp, sessionId, "png", target, true);
  if (!big.ok || !small.ok) throw new Error(`缩放回归跑不起来：${big.error || small.error}`);
  if (!big.dump?.css || !small.dump?.css) throw new Error("缩放回归拿不到快照 CSS（wantDump 链路断了）");

  let checked = 0;
  for (const prop of SCALED_PROPS) {
    // 排序后按多重集合配对：类名聚合与继承去重会让声明的出现次序变化，条数则必须一致
    const na = propPx(big.dump.css, prop).sort((x, y) => x - y);
    const nb = propPx(small.dump.css, prop).sort((x, y) => x - y);
    if (na.length !== nb.length) {
      throw new Error(`缩放回归：${prop} 的 px 条数随 k 变了（k=1 有 ${na.length} 条，k=${k.toFixed(5)} 有 ${nb.length} 条）` +
        ` —— 多半是「与父级相同就不发」的去重被缩放破坏（缩放后永远不等于父级原值）`);
    }
    for (let i = 0; i < na.length; i++) {
      const want = round2(na[i] * k);
      if (Math.abs(nb[i] - want) > 0.011) {
        throw new Error(`缩放回归：k=${k.toFixed(5)} 时 ${prop} 的 ${na[i]}px 应缩成 ${want}px，实得 ${nb[i]}px（sv() 没生效？）`);
      }
      checked++;
    }
  }
  if (checked < 20) throw new Error(`缩放回归：只比对了 ${checked} 个 px 值（<20），用例已退化，换页面或更新 SCALED_PROPS`);
  console.log(`✔ 缩放回归 k=${k.toFixed(5)}（${vw}→${target}px）：${checked} 个 sv() 管的 px 值全部按比例下发\n`);
}

// 这些属性在 capture.js 里走 sv()（不是 px()），正是容易静默失效的那一批
const SCALED_PROPS = ["max-width", "min-width", "gap", "flex-basis", "top", "left", "box-shadow", "filter", "font-size"];

/** 快照 CSS 形如 .c12{max-width:860px;padding:24px} —— 取某属性里出现的所有 px 数值 */
function propPx(css, prop) {
  const re = new RegExp(`(?:^|[;{])\\s*${prop}:([^;}]+)`, "g");
  return [...css.matchAll(re)].flatMap((m) => pxNums(m[1]));
}

function pxNums(v) {
  return [...String(v).matchAll(/(-?\d*\.?\d+)px/g)].map((m) => Number(m[1]));
}

const round2 = (n) => Math.round(n * 100) / 100;

/**
 * 宿主休眠恢复回归：MV3 的空闲闹钟会在 3 分钟后回收 offscreen，而页面侧的 booted 记不住这件事 ——
 * 这正是「第一次能导、过一会儿就不行、刷新页面又能导」那条现象。真等 3 分钟不现实，
 * 这里直接在 SW 里关掉文档复刻同一个状态（成因就是文档没了），再导一次看能否自愈。
 */
async function idleRecoverCheck(cdp, sessions, pageSession) {
  const sw = await findServiceWorker(cdp, sessions);
  if (!sw) throw new Error("休眠恢复回归：找不到扩展的 service worker");
  const alive = await evalIn(cdp, sw.sessionId, `chrome.offscreen.closeDocument().then(() => chrome.offscreen.hasDocument())`);
  if (alive !== false) throw new Error(`休眠恢复回归：offscreen 文档没被关掉（hasDocument=${alive}）`);
  const res = await exportVia(cdp, pageSession, "png");
  if (!res.ok) throw new Error(`休眠恢复回归：宿主被回收后没能自愈 —— ${res.error}`);
  console.log(`✔ 休眠恢复回归：offscreen 被回收后自动重启并重推快照，导出仍成功（${res.bytes} B）\n`);
}

async function evalIn(cdp, sessionId, expression) {
  const r = await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }, sessionId);
  if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails).slice(0, 300));
  return r.result?.value;
}

async function waitReady(cdp, sessionId) {
  for (let i = 0; i < 60; i++) {
    if (await evalIn(cdp, sessionId, "document.readyState") === "complete") return;
    await sleep(300);
  }
  throw new Error("页面未在 18s 内进入 complete");
}

/** 在扩展自己的上下文里求值（offscreen 优先，退回 service worker） */
async function evalInExtensionPage(cdp, sessions, expression) {
  const { targetInfos } = await cdp.send("Target.getTargets");
  const candidates = targetInfos
    .filter((t) => t.url.startsWith("chrome-extension://"))
    .sort((a, b) => Number(b.url.includes("offscreen")) - Number(a.url.includes("offscreen")));
  for (const t of candidates) {
    try {
      const sessionId = autoSessions.get(t.targetId)
        ?? (await cdp.send("Target.attachToTarget", { targetId: t.targetId, flatten: true })).sessionId;
      sessions.set(sessionId, t.url.includes("offscreen") ? "offscreen" : "sw");
      await cdp.send("Runtime.enable", {}, sessionId);
      const value = await evalIn(cdp, sessionId, expression);
      if (value != null) return value;
    } catch { /* 换下一个上下文 */ }
  }
  return null;
}

async function pollJson(path, timeoutMs) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try { return await (await fetch(`http://127.0.0.1:${PORT}${path}`)).json(); } catch { await sleep(400); }
  }
  throw new Error("DevTools 端点未在超时内就绪（端口被占用？用 --port 换一个）");
}

function withTimeout(promise, ms, message) {
  let timer;
  return Promise.race([
    promise.finally(() => clearTimeout(timer)),
    new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); }),
  ]);
}

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

class Cdp {
  constructor(url) { this.url = url; this.seq = 0; this.pending = new Map(); this.listeners = []; }
  onEvent(fn) { this.listeners.push(fn); }
  open() {
    this.ws = new WebSocket(this.url);
    return new Promise((resolve, reject) => {
      this.ws.addEventListener("open", () => resolve(), { once: true });
      this.ws.addEventListener("error", (e) => reject(new Error("CDP 连接失败 " + (e.message || ""))));
      this.ws.addEventListener("message", (ev) => {
        const msg = JSON.parse(ev.data);
        if (msg.id && this.pending.has(msg.id)) {
          const { res, rej } = this.pending.get(msg.id);
          this.pending.delete(msg.id);
          msg.error ? rej(new Error(JSON.stringify(msg.error))) : res(msg.result);
        } else if (msg.method) {
          for (const fn of this.listeners) fn(msg);
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
