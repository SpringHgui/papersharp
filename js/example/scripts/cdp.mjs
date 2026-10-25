// 真无头 Chrome/Edge + CDP 的最小驱动：站点验证专用。
// 内置浏览器视口读数不可信（vw=0），布局与 wasm 行为一律以这里为准。
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const BROWSER = [
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
].find(existsSync);

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class Cdp {
  constructor() {
    this.pending = new Map();
    this.seq = 0;
    this.events = [];
  }
  open(wsUrl) {
    return new Promise((res, rej) => {
      this.ws = new WebSocket(wsUrl);
      this.ws.addEventListener("open", res);
      this.ws.addEventListener("error", (e) => rej(new Error("cdp socket error: " + e?.message)));
      this.ws.addEventListener("message", (ev) => {
        const m = JSON.parse(ev.data);
        if (m.id && this.pending.has(m.id)) {
          const h = this.pending.get(m.id);
          this.pending.delete(m.id);
          if (m.error) h.reject(new Error(`${h.method}: ${m.error.message}`));
          else h.resolve(m.result);
        } else if (m.method) {
          this.events.push(m);
        }
      });
    });
  }
  send(method, params = {}, sid) {
    const id = ++this.seq;
    return new Promise((res, rej) => {
      this.pending.set(id, { resolve: res, reject: rej, method });
      this.ws.send(JSON.stringify(sid ? { id, method, params, sessionId: sid } : { id, method, params }));
    });
  }
  evaluate(sid, expression, { awaitPromise = true, timeout = 120000 } = {}) {
    return this.send(
      "Runtime.evaluate",
      { expression, returnByValue: true, awaitPromise, timeout },
      sid,
    ).then((r) => {
      if (r.exceptionDetails) {
        throw new Error(
          r.exceptionDetails.exception?.description ?? r.exceptionDetails.text ?? "页内求值失败",
        );
      }
      return r.result?.value;
    });
  }
  close() {
    try {
      this.ws.close();
    } catch {}
  }
}

/**
 * 起一个无头浏览器、导航到 url，把 {cdp, sessionId, events} 交给 fn；结束就杀进程（按 PID）。
 * @param {{url: string, width?: number, height?: number, fn: (ctx: any) => Promise<any>}} opts
 */
export async function withBrowser({ url, width = 1440, height = 1000, fn }) {
  if (!BROWSER) throw new Error("找不到 Chrome/Edge 可执行文件");
  const port = 9700 + (process.pid % 60);
  const profile = join(tmpdir(), `papersharp-site-${process.pid}`);
  mkdirSync(profile, { recursive: true });
  const proc = spawn(
    BROWSER,
    [
      "--headless=new",
      `--remote-debugging-port=${port}`,
      `--user-data-dir=${profile}`,
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-gpu",
      "--hide-scrollbars",
      `--window-size=${width},${height}`,
      "about:blank",
    ],
    { stdio: "ignore" },
  );

  const cdp = new Cdp();
  try {
    let version;
    for (let i = 0; i < 100 && !version; i++) {
      try {
        version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
      } catch {
        await sleep(300);
      }
    }
    if (!version) throw new Error("CDP 端口没起来");
    await cdp.open(version.webSocketDebuggerUrl);

    const { targetId } = await cdp.send("Target.createTarget", { url: "about:blank" });
    const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
    await cdp.send("Page.enable", {}, sessionId);
    await cdp.send("Runtime.enable", {}, sessionId);
    await cdp.send("Network.enable", {}, sessionId);
    await cdp.send(
      "Emulation.setDeviceMetricsOverride",
      { width, height, deviceScaleFactor: 1, mobile: false },
      sessionId,
    );
    await cdp.send("Page.navigate", { url }, sessionId);

    for (let i = 0; i < 80; i++) {
      try {
        const v = await cdp.evaluate(
          sessionId,
          `document.readyState + '|' + document.URL`,
          { awaitPromise: false },
        );
        if (String(v).includes("|") && !String(v).includes("about:blank")) break;
      } catch {
        /* 导航提交那一瞬会拿不到上下文，下一轮再问 */
      }
      await sleep(300);
    }

    return await fn({ cdp, sessionId });
  } finally {
    cdp.close();
    proc.kill();
  }
}

export async function writeShot(cdp, sessionId, outPath) {
  const { data } = await cdp.send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true }, sessionId);
  writeFileSync(resolve(outPath), Buffer.from(data, "base64"));
  return resolve(outPath);
}

/** 页内异常 / console 报错 / 失败请求，三类噪声一次捞干净。 */
export function digest(cdp) {
  return {
    exceptions: cdp.events
      .filter((m) => m.method === "Runtime.exceptionThrown")
      .map((m) => m.params.exceptionDetails?.exception?.description ?? m.params.exceptionDetails?.text)
      .slice(0, 8),
    console: cdp.events
      .filter((m) => m.method === "Runtime.consoleAPICalled" && ["error", "warning"].includes(m.params.type))
      .map((m) => m.params.args?.map((a) => a.value ?? a.description ?? a.type).join(" "))
      .slice(0, 12),
    failed: cdp.events
      .filter((m) => m.method === "Network.loadingFailed")
      .map((m) => `${m.params.errorText} ${m.params.type}`)
      .slice(0, 12),
  };
}
